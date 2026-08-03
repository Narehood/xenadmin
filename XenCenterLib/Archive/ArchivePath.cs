/* Copyright (c) Cloud Software Group, Inc.
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
using System.IO;

namespace XenCenterLib.Archive
{
    /// <summary>
    /// Helpers to keep archive extraction inside a destination root (Zip Slip).
    /// </summary>
    public static class ArchivePath
    {
        /// <summary>
        /// Resolves <paramref name="entryName"/> under <paramref name="destinationDirectory"/>,
        /// rejecting rooted paths and <c>..</c> traversal outside the destination.
        /// </summary>
        /// <exception cref="ArgumentNullException">Destination is null or empty.</exception>
        /// <exception cref="InvalidDataException">Entry name is empty, rooted, or escapes the destination.</exception>
        public static string GetSafeExtractPath(string destinationDirectory, string entryName)
        {
            if (string.IsNullOrEmpty(destinationDirectory))
                throw new ArgumentNullException(nameof(destinationDirectory));

            if (string.IsNullOrEmpty(entryName))
                throw new InvalidDataException("Archive entry name is empty.");

            var relative = entryName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (Path.IsPathRooted(relative))
                throw new InvalidDataException($"Archive entry '{entryName}' has a rooted path.");

            var destinationFull = Path.GetFullPath(destinationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destinationPrefix = destinationFull + Path.DirectorySeparatorChar;

            var combined = Path.GetFullPath(Path.Combine(destinationFull, relative));
            if (!IsUnderDestination(combined, destinationFull, destinationPrefix))
                throw new InvalidDataException($"Archive entry '{entryName}' would extract outside the destination directory.");

            return combined;
        }

        private static bool IsUnderDestination(string candidateFull, string destinationFull, string destinationPrefix)
        {
            var comparison = PathComparison;
            return candidateFull.StartsWith(destinationPrefix, comparison)
                   || string.Equals(candidateFull, destinationFull, comparison);
        }

        private static StringComparison PathComparison =>
#if NET6_0_OR_GREATER
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
#else
            StringComparison.OrdinalIgnoreCase;
#endif
    }
}
