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


        [FunctionName("StripeGateway")]
        public async static Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequest req, ILogger log)
        {
            var stripeEvent = StripeEventUtility.ParseEvent(await req.ReadAsStringAsync());
            log.LogInformation($"Received stripe event of type: {stripeEvent.Type}");

            var keyvault = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(new AzureServiceTokenProvider().KeyVaultTokenCallback));

            // todo this is maybe not always the case... but good enough for now
            if (stripeEvent.LiveMode)
            {
                _fortnoxAccessToken = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-access-token-prod")).Value;
                _fortnoxClientSecret = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-client-secret-prod")).Value;
            }
            else
            {
                _fortnoxAccessToken = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-access-token-test")).Value;
                _fortnoxClientSecret = (await keyvault.GetSecretAsync(VaultUrl, "fortnox-client-secret-test")).Value;
            }


            if (stripeEvent.Type == StripeEvents.CustomerCreated)
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
                    log.LogInformation(await result.Content.ReadAsStringAsync());
                }
            }
            else if (stripeEvent.Type == StripeEvents.ChargeSucceeded)
            {
                // todo do something useful
            }


            return new OkResult();
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
