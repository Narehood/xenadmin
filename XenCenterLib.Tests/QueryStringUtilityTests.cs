using System.Collections.Specialized;
using XenCenterLib;
using Xunit;

namespace XenCenterLib.Tests
{
    public class QueryStringUtilityTests
    {
        [Fact]
        public void ParseQueryString_Empty_ReturnsEmptyCollection()
        {
            var result = QueryStringUtility.ParseQueryString(null);
            Assert.Empty(result);

            result = QueryStringUtility.ParseQueryString(string.Empty);
            Assert.Empty(result);

            result = QueryStringUtility.ParseQueryString("?");
            Assert.Empty(result);
        }

        [Theory]
        [InlineData("a=1&b=2", "a", "1")]
        [InlineData("?a=1&b=2", "b", "2")]
        [InlineData("token=abc%2Bdef", "token", "abc+def")]
        public void ParseQueryString_DecodesValues(string query, string key, string expected)
        {
            var result = QueryStringUtility.ParseQueryString(query);
            Assert.Equal(expected, result[key]);
        }

        [Fact]
        public void ParseQueryString_PreservesDuplicateKeysAsCommaJoined()
        {
            var result = QueryStringUtility.ParseQueryString("a=1&a=2");
            Assert.Equal("1,2", result["a"]);
        }

        [Fact]
        public void AddAuthTokenToQueryString_EmptyToken_ReturnsExisting()
        {
            Assert.Equal("foo=bar", QueryStringUtility.AddAuthTokenToQueryString(null, "foo=bar"));
            Assert.Equal("foo=bar", QueryStringUtility.AddAuthTokenToQueryString(string.Empty, "foo=bar"));
        }

        [Fact]
        public void AddAuthTokenToQueryString_MergesTokenIntoExisting()
        {
            var result = QueryStringUtility.AddAuthTokenToQueryString("client_id=xyz", "?existing=1");
            Assert.Contains("existing=1", result);
            Assert.Contains("client_id=xyz", result);
            Assert.DoesNotContain("?", result);
        }

        [Fact]
        public void AddAuthTokenToQueryString_EncodesValues()
        {
            var result = QueryStringUtility.AddAuthTokenToQueryString("q=a b", string.Empty);
            Assert.Equal("q=a+b", result);
        }

        [Fact]
        public void ParseQueryString_KeyWithoutValue()
        {
            NameValueCollection result = QueryStringUtility.ParseQueryString("flag");
            Assert.Contains("flag", result.AllKeys);
            Assert.Null(result["flag"]);
        }
    }
}
