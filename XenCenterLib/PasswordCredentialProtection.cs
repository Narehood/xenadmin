using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace XenCenterLib
{
    /// <summary>
    /// Versioned password protection for the WinForms saved session. The input is
    /// the existing in-memory password hash, never a persisted password verifier.
    /// Derive once per unlocked session; every entry has a fresh IV and an HMAC.
    /// </summary>
    public sealed class PasswordCredentialProtection : IDisposable
    {
        public const string Prefix = "wp2:";
        private const int Iterations = 600000;
        private readonly byte[] passwordHash, salt, encryptionKey, authenticationKey;
        private bool disposed;

        public PasswordCredentialProtection(byte[] passwordHash) : this(passwordHash, RandomBytes(32)) { }

        private PasswordCredentialProtection(byte[] passwordHash, byte[] salt)
        {
            if (passwordHash == null || passwordHash.Length != 32) throw new ArgumentException("A password is required.", nameof(passwordHash));
            this.passwordHash = (byte[])passwordHash.Clone();
            this.salt = (byte[])salt.Clone();
#if NETFRAMEWORK
            using (var kdf = new Rfc2898DeriveBytes(passwordHash, salt, Iterations, HashAlgorithmName.SHA256))
            {
                var keys = kdf.GetBytes(64);
#else
            {
                var keys = Rfc2898DeriveBytes.Pbkdf2(passwordHash, salt, Iterations, HashAlgorithmName.SHA256, 64);
#endif
                encryptionKey = keys.Take(32).ToArray();
                authenticationKey = keys.Skip(32).ToArray();
                Array.Clear(keys, 0, keys.Length);
            }
        }

        public bool MatchesPassword(byte[] candidate) => !disposed && Equal(passwordHash, candidate);

        public static bool IsProtected(string entry) => entry?.StartsWith(Prefix, StringComparison.Ordinal) == true;

        public static PasswordCredentialProtection Open(byte[] passwordHash, string entry)
        {
            var parts = Parse(entry);
            return new PasswordCredentialProtection(passwordHash, Convert.FromBase64String(parts[2]));
        }

        public bool MatchesEntry(string entry)
        {
            ThrowIfDisposed();
            return Equal(salt, Convert.FromBase64String(Parse(entry)[2]));
        }

        public string Encrypt(string clearText)
        {
            ThrowIfDisposed();
            using (var aes = Aes.Create())
            {
                aes.Key = encryptionKey;
                aes.GenerateIV();
                var clear = Encoding.Unicode.GetBytes(clearText);
                byte[] cipher;
                try
                {
                    using (var transform = aes.CreateEncryptor()) cipher = transform.TransformFinalBlock(clear, 0, clear.Length);
                }
                finally { Array.Clear(clear, 0, clear.Length); }
                string authenticated = Prefix + Iterations.ToString(CultureInfo.InvariantCulture) + ":"
                    + Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(aes.IV) + ":" + Convert.ToBase64String(cipher);
                using (var mac = new HMACSHA256(authenticationKey))
                    return authenticated + ":" + Convert.ToBase64String(mac.ComputeHash(Encoding.ASCII.GetBytes(authenticated)));
            }
        }

        public string Decrypt(string entry)
        {
            ThrowIfDisposed();
            var parts = Parse(entry);
            if (!Equal(salt, Convert.FromBase64String(parts[2]))) throw new CryptographicException("Credential salt does not match.");
            string authenticated = entry.Substring(0, entry.LastIndexOf(':'));
            using (var mac = new HMACSHA256(authenticationKey))
                if (!Equal(mac.ComputeHash(Encoding.ASCII.GetBytes(authenticated)), Convert.FromBase64String(parts[5])))
                    throw new CryptographicException("The password or saved credential is invalid.");
            using (var aes = Aes.Create())
            {
                aes.Key = encryptionKey;
                aes.IV = Convert.FromBase64String(parts[3]);
                var cipher = Convert.FromBase64String(parts[4]);
                byte[] clear;
                using (var transform = aes.CreateDecryptor()) clear = transform.TransformFinalBlock(cipher, 0, cipher.Length);
                try { return Encoding.Unicode.GetString(clear); }
                finally { Array.Clear(clear, 0, clear.Length); }
            }
        }

        private static string[] Parse(string entry)
        {
            if (entry == null || entry.Length > 16 * 1024 * 1024) throw new CryptographicException("Invalid saved credential.");
            var parts = entry.Split(':');
            if (parts.Length != 6 || parts[0] != "wp2" || parts[1] != Iterations.ToString(CultureInfo.InvariantCulture)
                || Convert.FromBase64String(parts[2]).Length != 32 || Convert.FromBase64String(parts[3]).Length != 16
                || Convert.FromBase64String(parts[5]).Length != 32)
                throw new CryptographicException("Unsupported or invalid saved credential.");
            var cipher = Convert.FromBase64String(parts[4]);
            if (cipher.Length == 0 || cipher.Length % 16 != 0) throw new CryptographicException("Invalid saved credential.");
            return parts;
        }

        private static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }
        private static bool Equal(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(PasswordCredentialProtection)); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var key in new[] { passwordHash, encryptionKey, authenticationKey }) Array.Clear(key, 0, key.Length);
        }
    }
}
