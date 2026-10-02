using System;
using System.IO;

namespace XenCenterLib
{
    /// <summary>Redistribution notices embedded in both clients, available offline.</summary>
    public static class LegalNotices
    {
        public const string CopyrightSummary = "Copyright (c) 2023 XCP-ng Project\nCopyright (c) Cloud Software Group, Inc.\nCopyright (c) Citrix Systems, Inc.";
        public static string License { get; } = Read("XenCenterLib.LICENSE");
        public static string ThirdParty { get; } = Read("XenCenterLib.THIRD-PARTY-NOTICES.txt");

        private static string Read(string name)
        {
            using (var stream = typeof(LegalNotices).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("Missing embedded redistribution notice: " + name))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
    }
}
