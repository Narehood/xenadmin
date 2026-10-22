using System;
using System.Linq;
using System.Security.Cryptography;
using XenCenterLib;
using Xunit;

namespace XenCenterLib.Tests
{
    public sealed class PasswordCredentialProtectionTests
    {
        [Fact]
        public void CredentialsRoundTripWithIndependentSaltAndIvAndNoPersistedPasswordHash()
        {
            var hash = EncryptionUtils.ComputeHash("synthetic password");
            using (var session = new PasswordCredentialProtection(hash))
            using (var independent = new PasswordCredentialProtection(hash))
            {
                string first = session.Encrypt("username\u202fhost\u202f443\u202fsecret");
                string second = session.Encrypt("username\u202fhost\u202f443\u202fsecret");
                Assert.NotEqual(first, second);
                Assert.NotEqual(first.Split(':')[2], independent.Encrypt("same password").Split(':')[2]);
                Assert.StartsWith("wp2:600000:", first);
                Assert.DoesNotContain(Convert.ToBase64String(hash), first);
                using (var reopened = PasswordCredentialProtection.Open(hash, first))
                {
                    Assert.True(reopened.MatchesEntry(second));
                    Assert.Equal("username\u202fhost\u202f443\u202fsecret", reopened.Decrypt(first));
                }
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void AuthenticatesSaltIvCiphertextAndTag(int field)
        {
            var hash = EncryptionUtils.ComputeHash("synthetic password");
            using (var session = new PasswordCredentialProtection(hash))
            {
                string valid = session.Encrypt("secret");
                var parts = valid.Split(':');
                var changed = Convert.FromBase64String(parts[field]);
                changed[0] ^= 1;
                parts[field] = Convert.ToBase64String(changed);
                Assert.Throws<CryptographicException>(() => session.Decrypt(string.Join(":", parts)));
                using (var wrong = PasswordCredentialProtection.Open(EncryptionUtils.ComputeHash("wrong"), valid))
                    Assert.Throws<CryptographicException>(() => wrong.Decrypt(valid));
            }
        }

        [Fact]
        public void RejectsUnsupportedWorkFactorsBeforeDerivingAndClearsDisposedKeys()
        {
            var hash = EncryptionUtils.ComputeHash("synthetic password");
            var session = new PasswordCredentialProtection(hash);
            string valid = session.Encrypt("secret");
            foreach (string iterations in new[] { "1", "599999", "2147483647", "-1", "invalid" })
                Assert.Throws<CryptographicException>(() => PasswordCredentialProtection.Open(hash, valid.Replace(":600000:", ":" + iterations + ":")));
            session.Dispose();
            Assert.False(session.MatchesPassword(hash));
            Assert.Throws<ObjectDisposedException>(() => session.Decrypt(valid));
        }
    }
}
