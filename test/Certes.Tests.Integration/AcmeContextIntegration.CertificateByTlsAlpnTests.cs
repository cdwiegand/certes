﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Certes.Acme;
using Certes.Acme.Resource;
using Certes.Json;
using Newtonsoft.Json;
using Org.BouncyCastle.X509;
using Xunit;
using Xunit.Abstractions;

using static Certes.Helper;
using static Certes.IntegrationHelper;

namespace Certes
{
    public partial class AcmeContextIntegration
    {
        public class CertificateByTlsAlpnTests : AcmeContextIntegration
        {
            public CertificateByTlsAlpnTests(ITestOutputHelper output)
                : base(output)
            {
            }

            // unable to support tls-alpn tests at this time:
            // [Fact]
            // public void CanGenerateCertificateTlsAlpn()
            protected async Task CanGenerateCertificateTlsAlpn()
            {
                var dirUri = await GetAcmeUriV2();
                var hosts = new[] { $"certes-ci.wiegandtech.net" };
                var ctx = new AcmeContext(dirUri, GetKeyV2(), http: GetAcmeHttpClient(dirUri));
                var orderCtx = await ctx.NewOrder(hosts);
                var order = await orderCtx.Resource();
                Assert.NotNull(order);
                Assert.Equal(hosts.Length, order.Authorizations?.Count);
                Assert.True(
                    OrderStatus.Ready == order.Status || OrderStatus.Pending == order.Status || OrderStatus.Processing == order.Status,
                    $"Invalid order status: {order.Status}");

                var authrizations = await orderCtx.Authorizations();

                foreach (var authzCtx in authrizations)
                {
                    var authz = await authzCtx.Resource();
                    Assert.NotNull(authz?.Identifier?.Value);
                    var tlsAlpnChallenge = await authzCtx.TlsAlpn();
                    Assert.NotNull(tlsAlpnChallenge);
                    var alpnCertKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
                    var alpnCert = ctx.AccountKey.TlsAlpnCertificate(tlsAlpnChallenge.Token, authz.Identifier.Value, alpnCertKey);

                    await SetupValidationResponder(authz, alpnCert, alpnCertKey);
                    await tlsAlpnChallenge.Validate();
                }

                while (true)
                {
                    await Task.Delay(100);

                    var statuses = new List<AuthorizationStatus>();
                    foreach (var authz in authrizations)
                    {
                        var a = await authz.Resource();
                        statuses.Add(a?.Status ?? AuthorizationStatus.Pending);
                    }

                    if (statuses.All(s => s == AuthorizationStatus.Valid || s == AuthorizationStatus.Invalid))
                    {
                        break;
                    }
                }

                var certKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
                var finalizedOrder = await orderCtx.Finalize(new CsrInfo
                {
                    CountryName = "CA",
                    State = "Ontario",
                    Locality = "Toronto",
                    Organization = "Certes",
                    OrganizationUnit = "Dev",
                    CommonName = hosts[0],
                }, certKey);
                var certChain = await orderCtx.Download(null);

                // as time might be off a little, wait 1 second before proceeding to 
                // increase likelihood we don't think the certificate's not valid for
                // another second or two..
                System.Threading.Thread.Sleep(System.TimeSpan.FromSeconds(1));

                var pfxBuilder = certChain.ToPfx(certKey);
                pfxBuilder.AddTestCerts();

                var pfx = pfxBuilder.Build("my-pfx", "abcd1234");

                // revoke certificate
                var certParser = new X509CertificateParser();
                var certificate = certParser.ReadCertificate(certChain.Certificate.ToDer());
                var der = certificate.GetEncoded();

                await ctx.RevokeCertificate(der, RevocationReason.Unspecified, null);

                // deactivate authz so the subsequence can trigger challenge validation
                foreach (var authz in authrizations)
                {
                    var authzRes = await authz.Deactivate();
                    Assert.Equal(AuthorizationStatus.Deactivated, authzRes.Status);
                }
            }

            private static async Task SetupValidationResponder(Authorization authz, string alpnCert, IKey certKey)
            {
                // setup validation certificate
                var certC = new CertificateChain(alpnCert);
                var json = JsonConvert.SerializeObject(new
                {
                    Cert = certC.Certificate.ToDer(),
                    Key = certKey.ToDer(),
                }, JsonUtil.CreateSettings());
                if (authz == null || authz.Identifier == null || authz.Identifier.Value == null)
                {
                    throw new ArgumentNullException(nameof(authz), "Authorization or Identifier cannot be null.");
                }

                using (
                    var resp = await http.Value.PostAsync(
                        $"https://{authz.Identifier.Value}/tls-alpn-01/",
                        new StringContent(json, Encoding.UTF8, "application/json")))
                {
                    Assert.Equal(authz.Identifier.Value, await resp.Content.ReadAsStringAsync());
                }
            }
        }
    }
}
