using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Stripe;
using System;
using System.Linq;
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
        public async Task<HttpResponseMessage> HandleCustomerCreatedAsync(StripeEvent stripeEvent)
        {
            var stripeCustomer = Mapper<StripeCustomer>.MapFromJson((String)stripeEvent.Data.Object.ToString());

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
        public async Task<HttpResponseMessage> HandleInvoiceCreatedAsync(StripeEvent stripeEvent)
        {
            // todo refactor
            var invoice = Mapper<StripeInvoice>.MapFromJson((String)stripeEvent.Data.Object.ToString());
            _log.LogInformation($"Invoice created with ID {invoice.Id}, type: {invoice.Billing}");

            var customerId = invoice.CustomerId;
            if (invoice.Metadata.ContainsKey("FortnoxCustomerId") && !String.IsNullOrEmpty(invoice.Metadata["FortnoxCustomerId"]))
            {
                customerId = invoice.Metadata["FortnoxCustomerId"];
            }

            var order = new
            {
                CustomerNumber = customerId,
                Language = "EN",
                ExternalInvoiceReference1 = invoice.Id,
                Remarks = invoice.Billing == StripeBilling.ChargeAutomatically ? "Don't pay this invoice!\n\nYou have prepaid by credit/debit card." : "",
                CopyRemarks = true,
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
