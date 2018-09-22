using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Stripe;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace StripeGatewayFunction
{
    public static class StripeGatewayFunction
    {
        private const String VaultUrl = "https://flexinetsbilling.vault.azure.net/";
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
            else if (stripeEvent.Type == StripeEvents.ChargeSucceeded)
            {
                await HandleChargeSucceededAsync(stripeEvent);
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
                    VATType = "EXPORT",
                    VATNumber = stripeCustomer.TaxInfo?.TaxId,
                    DefaultDeliveryTypes = new
                    {
                        Invoice = "EMAIL"
                    }
                }
            };

            var result = await CreateFortnoxHttpClient().PostAsJsonAsync("https://api.fortnox.se/3/customers/", customer);
            if (!result.IsSuccessStatusCode)
            {
                _log.LogError(await result.Content.ReadAsStringAsync());
            }
        }


        /// <summary>
        /// Create order in fortnox
        /// </summary>
        /// <param name="stripeEvent"></param>
        /// <returns></returns>
        public async static Task HandleChargeSucceededAsync(StripeEvent stripeEvent)
        {

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
