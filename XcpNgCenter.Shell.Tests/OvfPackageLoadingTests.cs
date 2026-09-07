using System.Formats.Tar;
using System.Text;
using XenAdmin.Network;
using XenOvf;
using XcpNgCenter.Shell.Services;
using XcpNgCenter.Shell.ViewModels;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class OvfPackageLoadingTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("xcp-shell-ovf-");

    [Fact]
    public async Task PackageIoRunsOnWorkerAndSupportsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = OvfPackageLoader.LoadAsync("synthetic.ova", cancellation.Token, (_, token) =>
        {
            started.SetResult();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            token.ThrowIfCancellationRequested();
            throw new Exception("Cancellation was ignored");
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(load.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
    }

    [Fact]
    public async Task ActualDescriptorValidationProducesSystemsAndRejectsInvalidFile()
    {
        var descriptor = WriteDescriptor();
        var loaded = await OvfPackageLoader.LoadAsync(descriptor, CancellationToken.None);
        Assert.NotNull(loaded.Package);
        Assert.Equal(["Review (vm1)"], loaded.Systems);
        var invalid = Path.Combine(_directory.FullName, "invalid.ovf");
        File.WriteAllText(invalid, "<Envelope/>");
        var rejected = await OvfPackageLoader.LoadAsync(invalid, CancellationToken.None);
        Assert.Null(rejected.Package);
        Assert.NotNull(rejected.Error);
    }

    [Fact]
    public async Task ClosingDialogCancelsWorkAndRejectsLateSuccess()
    {
        var completion = new TaskCompletionSource<OvfPackageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        var path = WriteDescriptor();
        using var vm = CreateVm(path, (_, token) => { received = token; return completion.Task; });
        var browse = vm.BrowseCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoading);
        Assert.False(vm.HasPackage);
        vm.Dispose();
        Assert.True(received.IsCancellationRequested);
        completion.SetResult(new(Package.Create(path), ["Stale VM"], []));
        await browse;
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasPackage);
        Assert.Empty(vm.SystemSummaries);
    }

    [Fact]
    public async Task ChangingPathInvalidatesPreviouslyValidatedPackage()
    {
        var path = WriteDescriptor();
        using var vm = CreateVm(path, OvfPackageLoader.LoadAsync);
        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.True(vm.HasPackage);
        vm.FilePath = Path.Combine(_directory.FullName, "another.ova");
        Assert.False(vm.HasPackage);
        Assert.Empty(vm.SystemSummaries);
        vm.ImportCommand.Execute(null);
        Assert.Contains("valid", vm.StatusMessage);
    }

    [Fact]
    public async Task NewLoadWinsWhenCancelledOlderLoadCompletesLater()
    {
        var oldCompletion = new TaskCompletionSource<OvfPackageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newCompletion = new TaskCompletionSource<OvfPackageLoadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = WriteDescriptor();
        var newerPath = Path.Combine(_directory.FullName, "newer.ovf");
        File.Copy(path, newerPath);
        using var vm = CreateVm(path, (requested, _) => requested == path ? oldCompletion.Task : newCompletion.Task);
        var oldLoad = vm.BrowseCommand.ExecuteAsync(null);
        vm.FilePath = newerPath;
        var newLoad = vm.LoadCommand.ExecuteAsync(null);
        newCompletion.SetResult(new(Package.Create(newerPath), ["New VM"], []));
        await newLoad;
        oldCompletion.SetResult(new(Package.Create(path), ["Stale VM"], []));
        await oldLoad;
        Assert.True(vm.HasPackage);
        Assert.Equal(["New VM"], vm.SystemSummaries);
        Assert.Equal(newerPath, vm.FilePath);
    }

    [Fact]
    public void ArchiveMetadataHonorsCancellationAfterPackageCreation()
    {
        var path = Path.Combine(_directory.FullName, "appliance.ova");
        using (var file = File.Create(path))
        using (var writer = new TarWriter(file))
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "appliance.ovf")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("<Envelope/>"))
            });
        using var cancellation = new CancellationTokenSource();
        var package = Package.Create(path, cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => package.DescriptorFileName);
        // The cancelled read did not retain the file handle.
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private OvfImportViewModel CreateVm(string path,
        Func<string, CancellationToken, Task<OvfPackageLoadResult>> loader)
        => new(new XenConnection(), null, () => Task.FromResult<string?>(path), () => { }, null,
            loader, new ShellAppSettings(Path.Combine(_directory.FullName, "settings.json")));

    private string WriteDescriptor()
    {
        var path = Path.Combine(_directory.FullName, "appliance.ovf");
        File.WriteAllText(Path.Combine(_directory.FullName, "disk.vhd"), "synthetic disk");
        File.WriteAllText(path, """
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1" xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1" xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData">
              <References><File ovf:id="file1" ovf:href="disk.vhd"/></References>
              <DiskSection><Info>Disks</Info><Disk ovf:diskId="disk1" ovf:fileRef="file1" ovf:capacity="1048576"/></DiskSection>
              <VirtualSystem ovf:id="vm1"><Info>VM</Info><Name>Review</Name><VirtualHardwareSection><Info>Hardware</Info><Item><rasd:InstanceID>17</rasd:InstanceID><rasd:ResourceType>17</rasd:ResourceType><rasd:HostResource>ovf:/disk/disk1</rasd:HostResource></Item></VirtualHardwareSection></VirtualSystem>
            </Envelope>
            """);
        return path;
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
