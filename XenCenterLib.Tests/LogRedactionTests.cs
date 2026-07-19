using XenCenterLib;
using Xunit;

namespace XenCenterLib.Tests
{
    public class LogRedactionTests
    {
        [Theory]
        [InlineData("password=hunter2", "password=***")]
        [InlineData("token: abc.def", "token=***")]
        [InlineData("Connecting to host", "Connecting to host")]
        public void RedactSecrets_MasksSensitiveAssignments(string input, string expected)
        {
            Assert.Equal(expected, LogRedaction.RedactSecrets(input));
        }
    }
}
