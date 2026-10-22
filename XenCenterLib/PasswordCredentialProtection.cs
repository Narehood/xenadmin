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
        /// <summary>Version marker for authenticated WinForms credential entries; not an authentication check.</summary>
        public const string Prefix = "wp2:";
        private const int Iterations = 600000;
        private readonly byte[] passwordHash, salt, encryptionKey, authenticationKey;
        private bool disposed;

        /// <summary>Derives separate encryption/authentication keys once from an in-memory password hash and a new or persisted salt.</summary>
        /// <param name="passwordHash">The 32-byte in-memory SHA-256 password hash; it is KDF input and must never be persisted as a verifier.</param>
        /// <exception cref="ArgumentException">The supplied hash is null or is not 32 bytes.</exception>
        public PasswordCredentialProtection(byte[] passwordHash) : this(passwordHash, RandomBytes(32)) { }

        /// <summary>Derives separate encryption/authentication keys once from an in-memory password hash and a new or persisted salt.</summary>
        /// <param name="passwordHash">The 32-byte in-memory SHA-256 password hash; it is KDF input and must never be persisted as a verifier.</param>
        /// <param name="salt">The 32-byte random or persisted derivation salt.</param>
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

        /// <summary>Compares a candidate in-memory hash with this context without early byte mismatch exits; disposed contexts never match.</summary>
        /// <param name="candidate">Candidate in-memory password hash.</param>
        /// <returns>True only for an equal hash in a live context.</returns>
        public bool MatchesPassword(byte[] candidate) => !disposed && Equal(passwordHash, candidate);

        /// <summary>Identifies the wp2 prefix only; this does not validate or authenticate the encoded credential.</summary>
        /// <param name="entry">Versioned encrypted credential text.</param>
        /// <returns>True for the wp2 prefix, without checking envelope validity.</returns>
        public static bool IsProtected(string entry) => entry?.StartsWith(Prefix, StringComparison.Ordinal) == true;

        /// <summary>Validates a wp2 envelope and derives a context from its persisted salt; Decrypt must still authenticate the password and entry.</summary>
        /// <param name="passwordHash">The 32-byte in-memory SHA-256 password hash; it is KDF input and must never be persisted as a verifier.</param>
        /// <param name="entry">Versioned encrypted credential text.</param>
        /// <returns>A derived but not yet password-authenticated context that the caller must dispose.</returns>
        /// <exception cref="CryptographicException">The version, work factor or field lengths are unsupported.</exception>
        /// <exception cref="FormatException">A Base64 field is malformed.</exception>
        public static PasswordCredentialProtection Open(byte[] passwordHash, string entry)
        {
            var parts = Parse(entry);
            return new PasswordCredentialProtection(passwordHash, Convert.FromBase64String(parts[2]));
        }

        /// <summary>Validates the envelope shape and compares its salt with this context; matching alone does not authenticate the entry.</summary>
        /// <param name="entry">Versioned encrypted credential text.</param>
        /// <returns>True for a valid envelope using the same salt; this is not a password check.</returns>
        /// <exception cref="ObjectDisposedException">This context has been disposed.</exception>
        public bool MatchesEntry(string entry)
        {
            ThrowIfDisposed();
            return Equal(salt, Convert.FromBase64String(Parse(entry)[2]));
        }

        /// <summary>Encrypts UTF-16 text using a fresh AES-CBC IV and authenticates the version, iterations, salt, IV and ciphertext with HMAC-SHA256.</summary>
        /// <param name="clearText">Plaintext credential text to encrypt.</param>
        /// <returns>A self-contained wp2 envelope including salt, IV, ciphertext and authentication tag.</returns>
        /// <exception cref="ObjectDisposedException">This context has been disposed.</exception>
        /// <exception cref="ArgumentNullException">The plaintext is null.</exception>
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

        /// <summary>Authenticates a wp2 entry before AES-CBC decryption; wrong passwords or modified authenticated fields fail without returning plaintext.</summary>
        /// <param name="entry">Versioned encrypted credential text.</param>
        /// <returns>Authenticated plaintext; no plaintext is returned on authentication failure.</returns>
        /// <exception cref="CryptographicException">The password, salt, authenticated data or ciphertext is invalid.</exception>
        /// <exception cref="FormatException">A Base64 field is malformed.</exception>
        /// <exception cref="ObjectDisposedException">This context has been disposed.</exception>
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

        /// <summary>Validates the version, fixed supported work factor, field lengths and block alignment before any password derivation.</summary>
        /// <param name="entry">Versioned encrypted credential text.</param>
        /// <returns>The validated six envelope fields.</returns>
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

        /// <summary>Obtains cryptographically random bytes for a new credential context salt.</summary>
        /// <param name="length">Number of random bytes required.</param>
        /// <returns>Random bytes of the requested length.</returns>
        private static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }
        /// <summary>Compares equal-length byte arrays without returning on the first mismatched byte; null or different lengths never match.</summary>
        /// <param name="left">First comparison value.</param>
        /// <param name="right">Second comparison value.</param>
        /// <returns>True only for non-null arrays with identical lengths and bytes.</returns>
        private static bool Equal(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
        /// <summary>Rejects use of a context after its in-memory password and derived keys have been cleared.</summary>
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(PasswordCredentialProtection)); }
        /// <summary>Clears the context's retained password hash and both derived keys; repeated disposal is harmless.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var key in new[] { passwordHash, encryptionKey, authenticationKey }) Array.Clear(key, 0, key.Length);
        }
    }
}
