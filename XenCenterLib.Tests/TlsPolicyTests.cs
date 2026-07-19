using System.Net;
using System.Security.Authentication;
using XenCenterLib;
using Xunit;

namespace XenCenterLib.Tests
{
    public class TlsPolicyTests
    {
        [Fact]
        public void SecurityProtocol_IsTls12OrNewerOnly()
        {
            var protocol = TlsPolicy.AllowedSecurityProtocols;
#pragma warning disable CS0618 // asserting legacy protocols are excluded
            Assert.Equal(0, (int)(protocol & SecurityProtocolType.Ssl3));
            Assert.Equal(0, (int)(protocol & SecurityProtocolType.Tls));
            Assert.Equal(0, (int)(protocol & SecurityProtocolType.Tls11));
#pragma warning restore CS0618
            Assert.NotEqual(0, (int)(protocol & SecurityProtocolType.Tls12));
        }

        [Fact]
        public void SslProtocols_IsTls12OrNewerOnly()
        {
            var protocol = TlsPolicy.AllowedSslProtocols;
#pragma warning disable CS0618, SYSLIB0039
            Assert.Equal(0, (int)(protocol & SslProtocols.Tls));
            Assert.Equal(0, (int)(protocol & SslProtocols.Tls11));
#pragma warning restore CS0618, SYSLIB0039
            Assert.NotEqual(0, (int)(protocol & SslProtocols.Tls12));
        }
    }
}
