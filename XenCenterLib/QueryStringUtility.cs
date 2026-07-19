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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;

namespace XenCenterLib
{
    /// <summary>
    /// Query-string helpers that avoid System.Web (HttpUtility) so libraries can target .NET 8.
    /// </summary>
    public static class QueryStringUtility
    {
        /// <summary>
        /// Parses a query string (with or without a leading '?') into a <see cref="NameValueCollection"/>.
        /// Keys and values are URL-decoded. Duplicate keys are preserved like HttpUtility.ParseQueryString.
        /// </summary>
        public static NameValueCollection ParseQueryString(string query)
        {
            var result = new NameValueCollection(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return result;

            var q = query;
            if (q[0] == '?')
                q = q.Substring(1);

            if (q.Length == 0)
                return result;

            foreach (var part in q.Split('&'))
            {
                if (part.Length == 0)
                    continue;

                var eq = part.IndexOf('=');
                string key;
                string value;
                if (eq < 0)
                {
                    key = WebUtility.UrlDecode(part);
                    value = null;
                }
                else
                {
                    key = WebUtility.UrlDecode(part.Substring(0, eq));
                    value = WebUtility.UrlDecode(part.Substring(eq + 1));
                }

                result.Add(key, value);
            }

            return result;
        }

        /// <summary>
        /// Merges <paramref name="authToken"/> (itself a query fragment) into
        /// <paramref name="existingQueryString"/> and returns a query string without a leading '?'.
        /// Duplicate keys emit one pair per value; null values omit '=' (valueless keys).
        /// </summary>
        public static string AddAuthTokenToQueryString(string authToken, string existingQueryString)
        {
            var query = ParseQueryString(existingQueryString);
            if (!string.IsNullOrEmpty(authToken))
                query.Add(ParseQueryString(authToken));

            return SerializeQueryString(query);
        }

        /// <summary>
        /// Serializes a query collection without a leading '?'.
        /// </summary>
        private static string SerializeQueryString(NameValueCollection query)
        {
            var parts = new List<string>();
            foreach (var key in query.AllKeys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var values = query.GetValues(key);
                if (values == null || values.Length == 0)
                {
                    parts.Add(key);
                    continue;
                }

                foreach (var value in values)
                {
                    // Null means a valueless key (no '='); empty string means key=
                    parts.Add(value == null ? key : $"{key}={WebUtility.UrlEncode(value)}");
                }
            }

            return string.Join("&", parts);
        }
    }
}
