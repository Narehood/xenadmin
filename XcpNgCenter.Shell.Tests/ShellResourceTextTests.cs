using XcpNgCenter.Shell.Services;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class ShellResourceTextTests
{
    [Theory]
    [InlineData("&Yes, Reboot", "Yes, Reboot")]
    [InlineData("&Yes, Shut Down", "Yes, Shut Down")]
    [InlineData("Enter &Maintenance Mode...", "Enter Maintenance Mode...")]
    [InlineData("Exit &Maintenance Mode", "Exit Maintenance Mode")]
    [InlineData("&Yes", "Yes")]
    [InlineData("&No", "No")]
    [InlineData("&&", "&")]
    [InlineData("CPU && Memory", "CPU & Memory")]
    [InlineData("Select vApps && VMs", "Select vApps & VMs")]
    [InlineData("Save &&&Close", "Save &Close")]
    [InlineData("Confirm", "Confirm")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ResourceMnemonicsBecomePlainCaptions(string? resource, string expected)
        => Assert.Equal(expected, ShellResourceText.WithoutMnemonics(resource));
}
