/* Copyright (c) XCP-ng
 *
 * Redistribution and use in source and binary forms,
 * with or without modification, are permitted provided
 * that the following conditions are met:
 *
 * *   Redistributions of source code must retain the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer.
 * *   Redistributions in binary form must reproduce the above
 *     copyright notice, this list of conditions and the
 *     following disclaimer in the documentation and/or other
 *     materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
 * SUCH DAMAGE.
 */

using System;
using System.Net;
using System.Net.Sockets;

namespace XenCenterLib
{
    public enum AddressKind
    {
        Unknown,
        Hostname,
        Loopback,
        LinkLocal,
        Private,
        Public
    }

    /// <summary>
    /// Classifies connection targets for security UX.
    /// Used by the Add Server public-IP warning to detect globally routable addresses.
    /// </summary>
    public static class HostnameAddressClassifier
    {
        public static bool TryParseHostPort(string input, out string host, out int port)
        {
            host = null;
            port = 0;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            var value = input.Trim();

            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                var end = value.IndexOf(']');
                if (end <= 1)
                    return false;

                host = value.Substring(1, end - 1);
                if (end + 1 < value.Length && value[end + 1] == ':')
                {
                    if (!int.TryParse(value.Substring(end + 2), out port))
                        return false;
                }

                return !string.IsNullOrWhiteSpace(host);
            }

            var lastColon = value.LastIndexOf(':');
            if (lastColon > 0 && value.IndexOf(':') == lastColon && int.TryParse(value.Substring(lastColon + 1), out var parsedPort))
            {
                host = value.Substring(0, lastColon);
                port = parsedPort;
                return !string.IsNullOrWhiteSpace(host);
            }

            host = value;
            return true;
        }

        public static AddressKind Classify(string hostOrAddress)
        {
            if (string.IsNullOrWhiteSpace(hostOrAddress))
                return AddressKind.Unknown;

            var host = hostOrAddress.Trim();
            if (host.StartsWith("[", StringComparison.Ordinal) && host.EndsWith("]", StringComparison.Ordinal))
                host = host.Substring(1, host.Length - 2);

            if (!IPAddress.TryParse(host, out var address))
                return AddressKind.Hostname;

            // .NET accepts IPv4 shorthand ("1", "10.0", "172.16"). Those are not complete
            // addresses for security UX — keep Unknown so typing private prefixes does not
            // flash the public-IP warning (10/8, 172.16–31/12, 192.168/16, etc.).
            if (address.AddressFamily == AddressFamily.InterNetwork && !IsCompleteIPv4Literal(host))
                return AddressKind.Unknown;

            if (IPAddress.IsLoopback(address))
                return AddressKind.Loopback;

            if (IsLinkLocal(address))
                return AddressKind.LinkLocal;

            if (IsPrivate(address))
                return AddressKind.Private;

            return AddressKind.Public;
        }

        /// <summary>
        /// True only for a dotted-quad IPv4 literal (a.b.c.d). Rejects shorthand forms
        /// that <see cref="IPAddress.TryParse(string, out IPAddress)"/> still accepts.
        /// </summary>
        internal static bool IsCompleteIPv4Literal(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            var parts = host.Split('.');
            if (parts.Length != 4)
                return false;

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 3)
                    return false;
                for (var i = 0; i < part.Length; i++)
                {
                    if (!char.IsDigit(part[i]))
                        return false;
                }

                if (!int.TryParse(part, out var octet) || octet < 0 || octet > 255)
                    return false;
            }

            return true;
        }

        public static bool IsPublicIp(string hostOrAddress)
        {
            return Classify(hostOrAddress) == AddressKind.Public;
        }

        private static bool IsLinkLocal(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] == 169 && bytes[1] == 254;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return address.IsIPv6LinkLocal;

            return false;
        }

        private static bool IsPrivate(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 10)
                    return true;
                if (bytes[0] == 192 && bytes[1] == 168)
                    return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return true;
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
#if NET6_0_OR_GREATER
                return address.IsIPv6UniqueLocal;
#else
                // Unique Local Addresses are fc00::/7 (not on .NET Framework IPAddress).
                var bytes = address.GetAddressBytes();
                return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
#endif
            }

            return false;
        }
    }
}
