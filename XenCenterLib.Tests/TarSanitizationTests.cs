using XenCenterLib.Archive;
using Xunit;

namespace XenCenterLib.Tests
{
    public class TarSanitizationTests
    {
        [Theory]
        [InlineData("CON", "_CON")]
        [InlineData("nul.txt", "_nul.txt")]
        [InlineData("file:name", "file_name")]
        [InlineData("ok-name.txt", "ok-name.txt")]
        [InlineData("", "_")]
        [InlineData("trailing.", "trailing_")]
        public void SanitizeTarPathMember_ReplacesIllegalNames(string input, string expected)
        {
            Assert.Equal(expected, TarSanitization.SanitizeTarPathMember(input));
        }
    }
}
