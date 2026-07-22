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
        [InlineData("1")]
        [InlineData("8")]
        [InlineData("10")]
        [InlineData("10.0")]
        [InlineData("10.0.0")]
        [InlineData("172")]
        [InlineData("172.16")]
        [InlineData("172.31")]
        [InlineData("192")]
        [InlineData("192.168")]
        [InlineData("127")]
        [InlineData("169.254")]
        public void Classify_IncompleteIpv4_IsUnknown_NotPublic(string host)
        {
            Assert.Equal(AddressKind.Unknown, HostnameAddressClassifier.Classify(host));
            Assert.False(HostnameAddressClassifier.IsPublicIp(host));
        }

        [Theory]
        [InlineData("10.0.0.1", AddressKind.Private)]
        [InlineData("172.16.0.1", AddressKind.Private)]
        [InlineData("172.31.255.255", AddressKind.Private)]
        [InlineData("192.168.0.1", AddressKind.Private)]
        [InlineData("127.0.0.1", AddressKind.Loopback)]
        [InlineData("169.254.1.1", AddressKind.LinkLocal)]
        [InlineData("8.8.8.8", AddressKind.Public)]
        [InlineData("1.2.3.4", AddressKind.Public)]
        public void Classify_CompleteIpv4_UsesExpectedKind(string host, AddressKind expected)
        {
            Assert.Equal(expected, HostnameAddressClassifier.Classify(host));
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
