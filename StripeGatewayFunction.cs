using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Stripe;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace StripeGatewayFunction
{
    public static class StripeGatewayFunction
    {
        private const String VaultUrl = "https://flexinetsbilling.vault.azure.net/";
        private const String ArticleNumber = "4501";
        private static String _fortnoxAccessToken;
        private static String _fortnoxClientSecret;
        private static ILogger _log;


        [FunctionName("StripeGateway")]
        public async static Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequest req, ILogger log)
        {
            _log = log;
            var stripeEvent = StripeEventUtility.ParseEvent(await req.ReadAsStringAsync());
            log.LogInformation($"Received stripe event of type: {stripeEvent.Type}");


            await LoadSettingsAsync(stripeEvent.LiveMode);  // todo this is maybe not always the case... but good enough for now

            if (stripeEvent.Type == StripeEvents.CustomerCreated)
            {
                await HandleCustomerCreatedAsync(stripeEvent);

            }
            else if (stripeEvent.Type == StripeEvents.InvoiceCreated)
            {
                await HandleInvoiceCreatedAsync(stripeEvent);
            }


            return new OkResult();
        }


        /// <summary>
        /// Much side effect such sad
        /// </summary>
        /// <param name="production"></param>
        /// <returns></returns>
        public async static Task LoadSettingsAsync(Boolean production)
        {
            var keyvault = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(new AzureServiceTokenProvider().KeyVaultTokenCallback));
            if (production)
            {
                _fortnoxAccessToken = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-access-token-prod")).Value;
                _fortnoxClientSecret = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-client-secret-prod")).Value;
            }
            else
            {
                _fortnoxAccessToken = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-access-token-test")).Value;
                _fortnoxClientSecret = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-client-secret-test")).Value;
            }
        }


        /// <summary>
        /// Create customer in fortnox
        /// </summary>
        /// <param name="stripeEvent"></param>
        /// <returns></returns>
        public async static Task HandleCustomerCreatedAsync(StripeEvent stripeEvent)
        {
            var stripeCustomer = Mapper<StripeCustomer>.MapFromJson((String)stripeEvent.Data.Object.ToString());

            var vatType = "EXPORT";
            if (stripeCustomer.Shipping.Address.Country.Equals("SE", StringComparison.InvariantCultureIgnoreCase))
            {
                vatType = "SEVAT";
            }
            // todo hmm EUVAT?


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
                    InvoiceRemark = "DO NOT PAY THIS INVOICE!\n\nYOU HAVE PREPAID BY CREDIT/DEBIT CARD.",
                    Name = stripeCustomer.Shipping.Name,
                    YourReference = stripeCustomer.Shipping.Name,
                    ZipCode = stripeCustomer.Shipping.Address.PostalCode,
                    OurReference = "web",
                    TermsOfPayment = "K",
                    VATType = vatType,
                    VATNumber = stripeCustomer.TaxInfo?.TaxId,
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
        }


        /// <summary>
        /// Create order in fortnox
        /// </summary>
        /// <param name="stripeEvent"></param>
        /// <returns></returns>
        public async static Task HandleInvoiceCreatedAsync(StripeEvent stripeEvent)
        {
            var invoice = Mapper<StripeInvoice>.MapFromJson((String)stripeEvent.Data.Object.ToString());
            _log.LogInformation($"Invoice created with ID {invoice.Id}, type: {invoice.Billing}");

            var order = new
            {
                CustomerNumber = invoice.CustomerId,
                Language = "EN",
                ExternalInvoiceReference1 = invoice.Id,
                Remarks = invoice.Billing == StripeBilling.ChargeAutomatically ? "Don't pay this invoice!\n\nYou have prepaid by credit/debit card." : "",
                EmailInformation = new
                {
                    EmailAddressFrom = "finance@flexinets.se",
                    EmailAddressBCC = "finance@flexinets.se",
                    EmailSubject = "Flexinets Invoice/Order Receipt {no}",
                    EmailBody = invoice.Billing == StripeBilling.ChargeAutomatically
                        ? "Dear Flexinets user,<br />This email contains the credit card receipt for your prepaid subscription. No action required.<br /><br />Best regards<br />Flexinets<br />www.flexinets.eu"
                        : "hitta på text för fakturan"
                },
                OrderRows = invoice.StripeInvoiceLineItems.Data.Select(line => new
                {
                    Description = line.Description.Replace("×", "x"),   // thats not an x, this is an x
                    ArticleNumber = ArticleNumber,
                    Price = line.Amount / 100m,
                    OrderedQuantity = line.Quantity,
                    DeliveredQuantity = line.Quantity,
                    VAT = invoice.TaxPercent.HasValue ? Convert.ToInt32(invoice.TaxPercent.Value) : 0
                })
            };

            var result = await CreateFortnoxHttpClient().PostAsJsonAsync("https://api.fortnox.se/3/orders/", new { Order = order });
            if (!result.IsSuccessStatusCode)
            {
                _log.LogError(await result.Content.ReadAsStringAsync());
                _log.LogError(JsonConvert.SerializeObject(order));
            }
        }


        /// <summary>
        /// Create an authenticated http client for fortnox
        /// </summary>
        /// <returns></returns>
        public static HttpClient CreateFortnoxHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Access-Token", _fortnoxAccessToken);
            client.DefaultRequestHeaders.Add("Client-Secret", _fortnoxClientSecret);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        }
    }
}
