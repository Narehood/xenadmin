using System.Text;
using XenOvf;
using XenOvf.Definitions;
using XenOvf.Definitions.XENC;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class OvfPasswordValidationTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void PasswordCheckReadsThroughFinalPadding(bool wrongPassword, bool truncated, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "ovf-password-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Existing exported OVFs store this known plaintext as their password check.
            const string plaintext = "Nihil tam munitum quod non expugnari pecunia possit.                                              ";
            const string password = "synthetic OVF password";
            using (var encrypted = new OVF().EncryptFile(path, "1.3.1", password))
                encrypted.Write(Encoding.Unicode.GetBytes(plaintext));
            var bytes = File.ReadAllBytes(path);
            if (truncated)
                bytes = bytes[..^16]; // The visible prefix still matches; the missing padding must invalidate it.
            var envelope = new EnvelopeType
            {
                Sections = new Section_Type[]
                {
                    new SecuritySection_Type
                    {
                        Security = new[]
                        {
                            new Security_Type
                            {
                                version = "1.3.1",
                                EncryptedData = new EncryptedDataType
                                {
                                    CipherData = new CipherDataType { Item = bytes }
                                }
                            }
                        }
                    }
                }
            };
            Assert.Equal(expected, new OVF().CheckPassword(envelope, wrongPassword ? "incorrect" : password));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
