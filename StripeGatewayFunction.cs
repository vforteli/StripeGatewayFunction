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


        [FunctionName("StripeGateway")]
        public async static Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequest req, ILogger log)
        {
            try
            {
                var stripeEvent = StripeEventUtility.ParseEvent(await req.ReadAsStringAsync());
                log.LogInformation($"Received stripe event of type: {stripeEvent.Type}");

                var (accessToken, clientSecret) = await LoadSettingsAsync(stripeEvent.LiveMode);
                var fortnoxClient = new FortnoxClient(accessToken, clientSecret, log);

                var response = await HandleStripeEvent(stripeEvent, fortnoxClient);
                if (response != null)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return new OkObjectResult(await response.Content.ReadAsStringAsync());
                    }
                    else
                    {
                        return new BadRequestObjectResult(await response.Content.ReadAsStringAsync());
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, $"Something went wrong ({ex.Message})"); // wtf is the exception not logged?
                return new BadRequestObjectResult(ex.Message);
            }

            return new BadRequestObjectResult("Unknown unknown?");
        }


        public async static Task<HttpResponseMessage> HandleStripeEvent(StripeEvent stripeEvent, FortnoxClient fortnoxClient)
        {
            if (stripeEvent.Type == StripeEvents.CustomerCreated)
            {
                return await fortnoxClient.HandleCustomerCreatedAsync(stripeEvent);

            }
            else if (stripeEvent.Type == StripeEvents.InvoiceCreated)
            {
                await fortnoxClient.HandleInvoiceCreatedAsync(stripeEvent);
            }

            return null;
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
