using XenCenterLib;
using Xunit;

namespace XenCenterLib.Tests
{
    /// <summary>
    /// Guards the classification rules used by PublicIpWarningDialog.ConfirmConnect.
    /// </summary>
    public class PublicIpWarningPolicyTests
    {
        [Theory]
        [InlineData("8.8.8.8", true)]
        [InlineData("203.0.113.10", true)]
        [InlineData("10.0.0.1", false)]
        [InlineData("192.168.1.1", false)]
        [InlineData("127.0.0.1", false)]
        [InlineData("xcp-ng.example.com", false)]
        public void IsPublicIp_MatchesWarningPolicy(string host, bool expectWarn)
        {
            Assert.Equal(expectWarn, HostnameAddressClassifier.IsPublicIp(host));
        }
    }
}
