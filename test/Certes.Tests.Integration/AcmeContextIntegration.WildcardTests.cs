﻿using System.Threading.Tasks;
using Certes.Pkcs;
using Xunit;
using Xunit.Abstractions;

using static Certes.Helper;
using static Certes.IntegrationHelper;

namespace Certes
{
    public partial class AcmeContextIntegration
    {
        public class WildcardTests : AcmeContextIntegration
        {
            public WildcardTests(ITestOutputHelper output)
                : base(output)
            {
            }

            [Fact]
            public async Task CanGenerateWildcard()
            {
                var dirUri = await GetAcmeUriV2();
                var hosts = new[] { $"*.wildcard-es256.certes-ci.wiegandtech.net" };
                var ctx = new AcmeContext(dirUri, GetKeyV2(), http: GetAcmeHttpClient(dirUri));

                var orderCtx = await AuthzDns(ctx, hosts);
                Assert.NotNull(orderCtx);
                var certKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
                Assert.NotNull(certKey);
                var finalizedOrder = await orderCtx.Finalize(new CsrInfo
                {
                    CountryName = "CA",
                    State = "Ontario",
                    Locality = "Toronto",
                    Organization = "Certes",
                    OrganizationUnit = "Dev",
                    CommonName = hosts[0],
                }, certKey);
                var pem = await orderCtx.Download(null);

                // as time might be off a little, wait 1 second before proceeding to 
                // increase likelihood we don't think the certificate's not valid for
                // another second or two..
                System.Threading.Thread.Sleep(System.TimeSpan.FromSeconds(1));

                var builder = new PfxBuilder(pem.Certificate.ToDer(), certKey);
                foreach (var issuer in pem.Issuers)
                {
                    builder.AddIssuer(issuer.ToDer());
                }

                builder.AddTestCerts();

                var pfx = builder.Build("ci", "abcd1234");
                Assert.NotNull(pfx);
            }
        }
    }
}
