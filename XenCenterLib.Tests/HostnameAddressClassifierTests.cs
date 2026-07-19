using XenCenterLib;
using Xunit;

namespace XenCenterLib.Tests
{
    public class HostnameAddressClassifierTests
    {
        [Theory]
        [InlineData("127.0.0.1", AddressKind.Loopback)]
        [InlineData("::1", AddressKind.Loopback)]
        [InlineData("10.0.0.1", AddressKind.Private)]
        [InlineData("192.168.1.10", AddressKind.Private)]
        [InlineData("172.16.5.1", AddressKind.Private)]
        [InlineData("169.254.1.1", AddressKind.LinkLocal)]
        [InlineData("8.8.8.8", AddressKind.Public)]
        [InlineData("1.2.3.4", AddressKind.Public)]
        [InlineData("xcp-ng.local", AddressKind.Hostname)]
        [InlineData("pool-master", AddressKind.Hostname)]
        public void Classify_ReturnsExpectedKind(string host, AddressKind expected)
        {
            Assert.Equal(expected, HostnameAddressClassifier.Classify(host));
        }

        [Fact]
        public void IsPublicIp_TrueOnlyForPublicAddresses()
        {
            Assert.True(HostnameAddressClassifier.IsPublicIp("203.0.113.10"));
            Assert.False(HostnameAddressClassifier.IsPublicIp("10.1.2.3"));
            Assert.False(HostnameAddressClassifier.IsPublicIp("myhost.example"));
        }

        [Theory]
        [InlineData("host.example:443", "host.example", 443)]
        [InlineData("10.0.0.5", "10.0.0.5", 0)]
        [InlineData("[2001:db8::1]:8443", "2001:db8::1", 8443)]
        public void TryParseHostPort_ParsesCommonForms(string input, string host, int port)
        {
            Assert.True(HostnameAddressClassifier.TryParseHostPort(input, out var parsedHost, out var parsedPort));
            Assert.Equal(host, parsedHost);
            Assert.Equal(port, parsedPort);
        }
    }
}
