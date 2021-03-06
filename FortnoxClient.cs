﻿using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Stripe;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace StripeGatewayFunction
{
    public class FortnoxClient
    {
        private const String ArticleNumber = "4501";
        private static String _accessToken;
        private static String _clientSecret;
        private static ILogger _log;


        public FortnoxClient(String accessToken, String clientSecret, ILogger logger)
        {
            _accessToken = accessToken;
            _clientSecret = clientSecret;
            _log = logger;
        }


        /// <summary>
        /// Create customer in fortnox
        /// </summary>
        /// <param name="stripeEvent"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> HandleCustomerCreatedAsync(StripeCustomer stripeCustomer)
        {
            var vatType = "EXPORT";
            if (stripeCustomer.Shipping.Address.Country.Equals("SE", StringComparison.InvariantCultureIgnoreCase))
            {
                vatType = "SEVAT";
            }
            // todo hmm EUVAT?

            var companyName = stripeCustomer.Shipping.Name;
            var type = "PRIVATE";
            if (stripeCustomer.Metadata.ContainsKey("CompanyName") && !String.IsNullOrEmpty(stripeCustomer.Metadata["CompanyName"]))
            {
                companyName = stripeCustomer.Metadata["CompanyName"];
                type = "COMPANY";
            }

            var customer = new
            {
                Customer = new
                {
                    Address1 = stripeCustomer.Shipping.Address.Line1,
                    City = stripeCustomer.Shipping.Address.City,
                    CountryCode = stripeCustomer.Shipping.Address.Country,
                    Currency = "EUR",
                    CustomerNumber = stripeCustomer.Id,
                    Email = stripeCustomer.Email,
                    EmailInvoice = stripeCustomer.Email,
                    Name = companyName,
                    YourReference = stripeCustomer.Shipping.Name,
                    ZipCode = stripeCustomer.Shipping.Address.PostalCode,
                    OurReference = "web",
                    TermsOfPayment = "K",
                    VATType = vatType,
                    VATNumber = stripeCustomer.TaxInfo?.TaxId,
                    Type = type,
                    InvoiceRemark = String.IsNullOrEmpty(stripeCustomer.DefaultSourceId) ? "" : "Don't pay this invoice!\n\nYou have prepaid by credit/debit card.",
                    DefaultDeliveryTypes = new
                    {
                        Order = "EMAIL",
                        Invoice = "EMAIL"
                    }
                }
            };

            var result = await CreateFortnoxHttpClient().PostAsJsonAsync("https://api.fortnox.se/3/customers/", customer);
            if (!result.IsSuccessStatusCode)
            {
                _log.LogError(await result.Content.ReadAsStringAsync());
                _log.LogError(JsonConvert.SerializeObject(customer));
            }

            return result;
        }


        /// <summary>
        /// Create order in fortnox
        /// </summary>
        /// <param name="stripeEvent"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> HandleInvoiceCreatedAsync(StripeInvoice invoice)
        {
            _log.LogInformation($"Invoice created with ID {invoice.Id}, type: {invoice.Billing}");

            if (await OrderExists(invoice.Id))
            {
                _log.LogInformation($"Duplicate request for invoice id {invoice.Id}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Duplicate request ignored") };
            }

            var customerId = await GetFortnoxCustomerId(invoice.CustomerId);

            var order = new
            {
                CustomerNumber = customerId,
                Language = "EN",
                ExternalInvoiceReference1 = invoice.Id,
                Remarks = invoice.Billing == StripeBilling.ChargeAutomatically ? "Don't pay this invoice!\n\nYou have prepaid by credit/debit card." : "",
                CopyRemarks = true,
                Currency = invoice.Currency.ToUpperInvariant(),
                EmailInformation = new
                {
                    EmailAddressFrom = "finance@flexinets.eu",
                    EmailAddressBCC = "finance@flexinets.eu",
                    EmailSubject = "Flexinets Invoice/Order Receipt {no}",
                    EmailBody = invoice.Billing == StripeBilling.ChargeAutomatically
                        ? "Dear Flexinets user,<br />This email contains the credit card receipt for your prepaid subscription. No action required.<br /><br />Best regards<br />Flexinets<br />www.flexinets.eu"
                        : "hitta på text för fakturan"
                },
                OrderRows = invoice.StripeInvoiceLineItems.Data.Select(line => new
                {
                    Description = line.Description.Replace("×", "x"),   // thats not an x, this is an x
                    AccountNumber = "",
                    ArticleNumber = ArticleNumber,
                    Price = line.Amount / 100m,
                    OrderedQuantity = line.Quantity.GetValueOrDefault(0),
                    DeliveredQuantity = line.Quantity.GetValueOrDefault(0),
                    VAT = invoice.TaxPercent.HasValue ? Convert.ToInt32(invoice.TaxPercent.Value) : 0,
                    Discount = invoice.StripeDiscount?.StripeCoupon?.PercentOff != null ? invoice.StripeDiscount.StripeCoupon.PercentOff.Value : 0,
                    DiscountType = "PERCENT"
                }).ToList()
            };

            if (invoice.StripeDiscount?.StripeCoupon?.PercentOff != null)
            {
                order.OrderRows.Add(new
                {
                    Description = $"Promo code {invoice.StripeDiscount.StripeCoupon.Id} applied: {invoice.StripeDiscount.StripeCoupon.Name}",
                    AccountNumber = "0",
                    ArticleNumber = "",
                    Price = 0m,
                    OrderedQuantity = 0,
                    DeliveredQuantity = 0,
                    VAT = 0,
                    Discount = 0m,
                    DiscountType = ""
                });
            }

            order.OrderRows.Add(new
            {
                Description = $"Order date {invoice.Date.Value.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC",
                AccountNumber = "0",
                ArticleNumber = "",
                Price = 0m,
                OrderedQuantity = 0,
                DeliveredQuantity = 0,
                VAT = 0,
                Discount = 0m,
                DiscountType = ""
            });


            var result = await CreateFortnoxHttpClient().PostAsJsonAsync("https://api.fortnox.se/3/orders/", new { Order = order });
            if (!result.IsSuccessStatusCode)
            {
                _log.LogError(await result.Content.ReadAsStringAsync());
                _log.LogError(JsonConvert.SerializeObject(order));
            }
            return result;
        }


        /// <summary>
        /// Check if an order with external id exists in FN.
        /// Used for making api idempotent
        /// </summary>
        /// <param name="stripeInvoiceId"></param>
        /// <returns></returns>
        public async Task<Boolean> OrderExists(String stripeInvoiceId)
        {
            var result = await CreateFortnoxHttpClient().GetStringAsync($"https://api.fortnox.se/3/orders/?externalinvoicereference1={stripeInvoiceId}");
            dynamic response = JsonConvert.DeserializeObject(result);
            return (Int32)response.MetaInformation["@TotalResources"] > 0;
        }


        /// <summary>
        /// Gets the real customer id from fortnox in case it doesnt match
        /// Because of the limitations in Fortnox, the only "reasonable" searchable field to put the external id in is phone number...
        /// </summary>
        /// <param name="customerId"></param>
        /// <returns></returns>
        public async Task<String> GetFortnoxCustomerId(String customerId)
        {
            var client = CreateFortnoxHttpClient();
            if ((await client.GetAsync($"https://api.fortnox.se/3/customers/{customerId}")).IsSuccessStatusCode)
            {
                return customerId;
            }
            else
            {
                dynamic response = JsonConvert.DeserializeObject(await client.GetStringAsync($"https://api.fortnox.se/3/customers/?phone={customerId}"));                
                if (response.Customers?.Count == 1)
                {
                    return response.Customers[0].CustomerNumber;
                }                
            }

            throw new InvalidOperationException($"Customer {customerId} not found by id or external id?!");
        }


        /// <summary>
        /// Create an authenticated http client for fortnox
        /// </summary>
        /// <returns></returns>
        public HttpClient CreateFortnoxHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Access-Token", _accessToken);
            client.DefaultRequestHeaders.Add("Client-Secret", _clientSecret);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        }
    }
}