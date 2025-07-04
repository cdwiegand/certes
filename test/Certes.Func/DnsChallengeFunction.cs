﻿using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Management.Dns.Fluent;
using Microsoft.Azure.Management.Dns.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using static Certes.Func.Helper;

namespace Certes.Func
{
    public class DnsChallengeFunction
    {
        private readonly ILogger _logger;

        public DnsChallengeFunction(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<HttpChallengeFunction>();
        }

        [Function(nameof(HandleDnsChallenge))]
        public async Task<HttpResponseData> HandleDnsChallenge(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "dns-01/{algo}")] HttpRequestData req,
            string algo)
        {
            Dictionary<string, string> tokens;
            using (var reader = new StreamReader(req.Body))
            {
                var json = await reader.ReadToEndAsync();
                tokens = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? throw new ArgumentException("Invalid JSON body. Expected a dictionary of tokens.");
            }

            var addedRecords = new Dictionary<string, string>();
            var keyType = (KeyAlgorithm)Enum.Parse(typeof(KeyAlgorithm), algo, true);
            var accountKey = GetTestKey(keyType);

            var loginInfo = new ServicePrincipalLoginInformation
            {
                ClientId = Env("CERTES_AZURE_CLIENT_ID"),
                ClientSecret = Env("CERTES_AZURE_CLIENT_SECRET"),
            };

            var credentials = new AzureCredentials(loginInfo, Env("CERTES_AZURE_TENANT_ID"), AzureEnvironment.AzureGlobalCloud);
            var builder = RestClient.Configure();
            var resClient = builder.WithEnvironment(AzureEnvironment.AzureGlobalCloud)
                .WithCredentials(credentials)
                .Build();
            using (var client = new DnsManagementClient(resClient))
            {
                client.SubscriptionId = Env("CERTES_AZURE_SUBSCRIPTION_ID");

                foreach (var p in tokens)
                {
                    var name = "_acme-challenge." + p.Key.Replace(".wiegandtech.net", "");
                    var value = accountKey.SignatureKey.DnsTxt(p.Value);
                    await client.RecordSets.CreateOrUpdateAsync(
                        "certes-ci",
                        "wiegandtech.net",
                        name,
                        RecordType.TXT,
                        new RecordSetInner(
                            name: name,
                            tTL: 1,
                            txtRecords: new[] { new TxtRecord(new[] { value }) }));

                    addedRecords.Add(name, value);
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(addedRecords);
            return response;
        }
    }
}
