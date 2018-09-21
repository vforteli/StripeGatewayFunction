using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        [FunctionName("StripeGateway")]
        public async static Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req, ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");
            var json = await req.ReadAsStringAsync();
            var stripeEvent = StripeEventUtility.ParseEvent(json);

            log.LogInformation($"Received stripe event of type: {stripeEvent.Type}");

            if (stripeEvent.Type == "customer.created")
            {
                var stripeCustomer = Mapper<StripeCustomer>.MapFromJson((string)stripeEvent.Data.Object.ToString());

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
            else if (stripeEvent.Type == "charge.succeeded")
            {
                // todo do something useful
            }


            return new OkResult();
        }


        public static HttpClient CreateFortnoxHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Access-Token", Environment.GetEnvironmentVariable("fortnox:access_token"));
            client.DefaultRequestHeaders.Add("Client-Secret", Environment.GetEnvironmentVariable("fortnox:client_secret"));
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        }
    }
}
