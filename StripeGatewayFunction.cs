using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Stripe;
using System;
using System.Threading.Tasks;

namespace StripeGatewayFunction
{
    public static class StripeGatewayFunction
    {
        private const String VaultUrl = "https://flexinetsbilling.vault.azure.net/";


        [FunctionName("StripeGateway")]
        public async static Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequest req, ILogger log)
        {
            var stripeEvent = StripeEventUtility.ParseEvent(await req.ReadAsStringAsync());
            log.LogInformation($"Received stripe event of type: {stripeEvent.Type}");

            var (accessToken, clientSecret) = await LoadSettingsAsync(stripeEvent.LiveMode);  // todo this is maybe not always the case... but good enough for now
            var fortnoxClient = new FortnoxClient(accessToken, clientSecret, log);

            if (stripeEvent.Type == StripeEvents.CustomerCreated)
            {
                await fortnoxClient.HandleCustomerCreatedAsync(stripeEvent);

            }
            else if (stripeEvent.Type == StripeEvents.InvoiceCreated)
            {
                await fortnoxClient.HandleInvoiceCreatedAsync(stripeEvent);
            }


            return new OkResult();
        }


        /// <summary>
        /// Get settings from key vault
        /// </summary>
        /// <param name="production"></param>
        /// <returns></returns>
        public async static Task<(String accessToken, String clientSecret)> LoadSettingsAsync(Boolean production)
        {
            var keyvault = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(new AzureServiceTokenProvider().KeyVaultTokenCallback));
            if (production)
            {
                return (
                (await keyvault.GetSecretAsync(VaultUrl, "fortnox-access-token-prod")).Value,
                (await keyvault.GetSecretAsync(VaultUrl, "fortnox-client-secret-prod")).Value);
            }
            else
            {
                return (
                (await keyvault.GetSecretAsync(VaultUrl, "fortnox-access-token-test")).Value,
                (await keyvault.GetSecretAsync(VaultUrl, "fortnox-client-secret-test")).Value);
            }
        }
    }
}
