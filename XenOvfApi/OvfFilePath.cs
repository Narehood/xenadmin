using System;
using System.Collections.Generic;
using System.IO;

namespace XenOvf
{
    /// <summary>Resolves local OVF references without leaving the appliance directory.</summary>
    public static class OvfFilePath
    {
        public static StringComparer Comparer => Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public static string Resolve(string directory, string reference)
        {
            var relative = NormalizeReference(reference);
            var root = Path.GetFullPath(string.IsNullOrEmpty(directory) ? "." : directory);
            var boundary = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(Path.Combine(root, relative));
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!fullPath.StartsWith(boundary, comparison))
                throw new InvalidDataException("OVF file reference escapes the appliance directory.");

            // Reject existing links before opening. As with other local file access, the
            // appliance directory must not be concurrently modified by an untrusted process.
            RejectLink(root);
            var current = root;
            foreach (var component in fullPath.Substring(boundary.Length).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                RejectLink(current);
            }

            return fullPath;
        }

        // Logical package paths also need comparison before an OVA has been extracted.
        internal static string NormalizeReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) ||
                reference.IndexOf(':') >= 0 || reference.IndexOf('\0') >= 0)
                throw new InvalidDataException("OVF file references must be relative local paths.");

            // Treat both separators consistently, including Windows paths on Linux.
            var relative = reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative))
                throw new InvalidDataException("OVF file references must be relative local paths.");
            var components = new List<string>();
            foreach (var component in relative.Split(Path.DirectorySeparatorChar))
            {
                // Win32 can normalize trailing dots/spaces after lexical path validation.
                if (component != "." && component != ".." &&
                    (component.EndsWith(".", StringComparison.Ordinal) || component.EndsWith(" ", StringComparison.Ordinal)))
                    throw new InvalidDataException("OVF file references cannot contain ambiguous path components.");
                if (component.Length == 0 || component == ".")
                    continue;
                if (component == "..")
                {
                    if (components.Count == 0)
                        throw new InvalidDataException("OVF file reference escapes the appliance directory.");
                    components.RemoveAt(components.Count - 1);
                }
                else
                    components.Add(component);
            }

            if (components.Count == 0)
                throw new InvalidDataException("OVF file references must name a file within the appliance directory.");
            return string.Join(Path.DirectorySeparatorChar.ToString(), components);
        }

        private static void RejectLink(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("OVF file references cannot traverse symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
