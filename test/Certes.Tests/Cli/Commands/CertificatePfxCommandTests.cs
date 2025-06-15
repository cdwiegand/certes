using System;
using System.CommandLine;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Certes.Acme;
using Certes.Acme.Resource;
using Certes.Cli.Settings;
using Microsoft.Azure.Management.AppService.Fluent.Models;
using Moq;
using Newtonsoft.Json;
using Xunit;
using static Certes.Acme.WellKnownServers;
using static Certes.Cli.CliTestHelper;
using static Certes.Helper;

namespace Certes.Cli.Commands
{
    [Collection(nameof(Helper.GetValidCert))]
    public class CertificatePfxCommandTests
    {
        readonly Uri orderLoc = new Uri("http://acme.com/o/1");
        readonly Uri certLoc = new Uri("http://acme.com/c/1");
        readonly string privateKeyPath = "./my-key.pem";

        [Fact]
        public async Task CanProcessCommand()
        {
            #region Setup Test
            var order = new Order
            {
                Certificate = certLoc,
                Identifiers = new[] {
                    new Identifier { Value = "*.a.com" },
                    new Identifier { Value = "*.b.com" },
                },
                Status = OrderStatus.Valid,
            };

            var certChainContent = await GetValidCert();
            foreach (var testRoot in IntegrationHelper.TestCertificates)
            {
                certChainContent += Encoding.UTF8.GetString(testRoot) + Environment.NewLine;
            }

            var certChain = new CertificateChain(certChainContent);

            var settingsMock = new Mock<IUserSettings>(MockBehavior.Strict);
            settingsMock.Setup(m => m.GetDefaultServer()).ReturnsAsync(LetsEncryptV2);
            settingsMock.Setup(m => m.GetAccountKey(LetsEncryptV2)).ReturnsAsync(GetKeyV2());

            var orderMock = new Mock<IOrderContext>(MockBehavior.Strict);
            orderMock.Setup(m => m.Resource()).ReturnsAsync(order);
            orderMock.Setup(m => m.Download(It.Is<string>(s => string.IsNullOrWhiteSpace(s)))).ReturnsAsync(certChain);

            var ctxMock = new Mock<IAcmeContext>(MockBehavior.Strict);
            ctxMock.Setup(m => m.GetDirectory()).ReturnsAsync(MockDirectoryV2);
            ctxMock.Setup(m => m.Order(orderLoc)).Returns(orderMock.Object);

            var fileMock = new Mock<IFileUtil>(MockBehavior.Strict);
            fileMock.Setup(m => m.ReadAllText(privateKeyPath)).ReturnsAsync(KeyAlgorithm.RS256.GetTestKey());

            var envMock = new Mock<IEnvironmentVariables>(MockBehavior.Strict);

            var cmd = new CertificatePfxCommand(
                settingsMock.Object, (u, k) => ctxMock.Object, fileMock.Object, envMock.Object);
            var command = cmd.Define();

            var (console, stdOutput, errOutput) = MockConsole();

            // wait 1 second, in case cert the server generates isn't valid for a second?
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(1));
            #endregion

            #region Basic Command
            await command.InvokeAsync($"pfx {orderLoc} --private-key {privateKeyPath} abcd1234", console.Object);
            Assert.True(errOutput.Length == 0, errOutput.ToString());
            dynamic ret = JsonConvert.DeserializeObject(stdOutput.ToString()) ?? throw new InvalidOperationException("No output");
            Assert.NotNull(ret);
            Assert.Equal(certLoc.ToString(), $"{ret.location}");
            Assert.NotNull(ret.pfx);

            orderMock.Verify(m => m.Download(""), Times.Once);
            var outPath = "./cert.pfx";
            fileMock.Setup(m => m.WriteAllBytes(outPath, It.IsAny<byte[]>()))
                .Returns(Task.CompletedTask);
            #endregion

            // reset!
            errOutput.Clear();
            stdOutput.Clear();

            #region Can Export Private Key
            await command.InvokeAsync($"pfx {orderLoc} --private-key {privateKeyPath} abcd1234 --out {outPath}", console.Object);
            Assert.True(errOutput.Length == 0, errOutput.ToString());
            ret = JsonConvert.DeserializeObject(stdOutput.ToString()) ?? throw new InvalidOperationException("No output");
            Assert.Equal(
                JsonConvert.SerializeObject(new
                {
                    location = certLoc,
                }),
                JsonConvert.SerializeObject(ret));
            fileMock.Verify(m => m.WriteAllBytes(outPath, It.IsAny<byte[]>()), Times.Once);
            #endregion

            // reset!
            errOutput.Clear();
            stdOutput.Clear();

            #region Can export private key with external issuers
            // Export PFX with external issuers
            var leafCert = certChain.Certificate.ToPem();
            orderMock.Setup(m => m.Download(null)).ReturnsAsync(new CertificateChain(leafCert));

            var issuersPem = string.Join(Environment.NewLine, certChain.Issuers.Select(i => i.ToPem()));
            fileMock.Setup(m => m.ReadAllText("./issuers.pem")).ReturnsAsync(issuersPem);

            await command.InvokeAsync($"pfx {orderLoc} --private-key {privateKeyPath} abcd1234 --out {outPath} --issuer ./issuers.pem --friendly-name friendly", console.Object);
            Assert.True(errOutput.Length == 0, errOutput.ToString());
            ret = JsonConvert.DeserializeObject(stdOutput.ToString()) ?? throw new InvalidOperationException("No output");
            // Assert.NotEmpty(ret.pfx); // given an --out, Certes doesn't emit the pfx to stdout!
            Assert.Equal(
                JsonConvert.SerializeObject(new
                {
                    location = certLoc,
                }),
                JsonConvert.SerializeObject(ret));
            fileMock.Verify(m => m.WriteAllBytes(outPath, It.IsAny<byte[]>()), Times.Exactly(2)); // twice now
            #endregion
        }
    }
}
