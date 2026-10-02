using XenCenterLib;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class LegalNoticeTests
{
    [Fact]
    public void DistributedLicenseMatchesEmbeddedOfflineText()
    {
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LICENSE")), LegalNotices.License);
        Assert.Contains("Copyright (c) 2023 XCP-ng Project", LegalNotices.License);
        Assert.Contains("Copyright (c) Cloud Software Group, Inc.", LegalNotices.License);
        Assert.Contains("Copyright (c) Citrix Systems, Inc.", LegalNotices.License);
        Assert.Contains("Redistributions in binary form must reproduce", LegalNotices.License);
        Assert.Contains("THIS SOFTWARE IS PROVIDED", LegalNotices.License);
    }

    [Fact]
    public void CurrentDependencyFontAndLegacyNoticesSurviveDistribution()
    {
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")), LegalNotices.ThirdParty);
        foreach (var notice in new[] { "Avalonia/11.3.20", "SharpZipLib/1.4.2", "log4net/3.4.0", "SIL OPEN FONT LICENSE Version 1.1", "Bundled .NET runtime 10.0.12", "World Wide Web Consortium" })
            Assert.Contains(notice, LegalNotices.ThirdParty);
    }
}
