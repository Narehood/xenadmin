using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using XenAdmin.Actions.OvfActions;
using XenCenterLib.Compression;
using XenOvf;
using XenOvf.Definitions;
using XenOvf.Utilities;
using Xunit;

namespace XcpNgCenter.Shell.Tests;

public sealed class OvfImportSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xcpng-ovf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _links = new();
    private string PackageDirectory => Path.Combine(_root, "package");
    private string Descriptor => Path.Combine(PackageDirectory, "appliance.ovf");

    public OvfImportSecurityTests() => Directory.CreateDirectory(PackageDirectory);

    [Theory]
    [InlineData("enc_original.vhd")]
    [InlineData("unc_original.vhd")]
    public void FailedImportPreservesOriginalFilesRegardlessOfName(string filename)
    {
        var path = WriteFile(filename, "original disk sentinel");
        Assert.IsAssignableFrom<Exception>(InvokeImport(filename));
        Assert.Equal("original disk sentinel", File.ReadAllText(path));
    }

    [Fact]
    public void FailedDecompressionDoesNotTouchAnExistingUncFileOrLeaveTemporaryFiles()
    {
        WriteFile("disk.vhd.gz", "invalid gzip");
        var unrelated = WriteFile("unc_disk.vhd", "unrelated original disk");
        var before = Directory.GetFiles(PackageDirectory, "xcpng-import-*").ToHashSet();
        Assert.IsAssignableFrom<Exception>(InvokeImport("disk.vhd.gz", CompressionFactory.Type.Gz));
        Assert.Equal("unrelated original disk", File.ReadAllText(unrelated));
        Assert.DoesNotContain(Directory.GetFiles(PackageDirectory, "xcpng-import-*"), path => !before.Contains(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressedImportUsesDecryptedInputAndCleansEveryIntermediate(bool encrypted)
    {
        var path = Path.Combine(PackageDirectory, "disk.iso.gz");
        using (var output = encrypted ? new OVF().EncryptFile(path, "1.3.1", "test password") : File.Create(path))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(new byte[] { 1, 2, 3, 4 });

        var original = File.ReadAllBytes(path);
        var before = Directory.GetFiles(PackageDirectory, "xcpng-import-*").ToHashSet();
        var action = RuntimeHelpers.GetUninitializedObject(typeof(ImportApplianceAction));
        typeof(ImportApplianceAction).GetField("m_password", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(action, "test password");
        typeof(ImportApplianceAction).GetField("m_encryptionVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(action, "1.3.1");
        var method = typeof(ImportApplianceAction).GetMethod("ImportFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // No ISO SR means a clean skip after transformation, before any host API call.
        Assert.Null(method.Invoke(action, new object?[] { "review", PackageDirectory, "disk.iso.gz", CompressionFactory.Type.Gz, null, "", null, encrypted, (Action<float>)(_ => { }) }));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(PackageDirectory, "xcpng-import-*"), file => !before.Contains(file));
    }

    [Fact]
    public void CancelledDecompressionPreservesSourceAndCleansTemporaryFiles()
    {
        var path = Path.Combine(PackageDirectory, "disk.iso.gz");
        using (var output = File.Create(path))
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(new byte[81921]);
        var original = File.ReadAllBytes(path);
        var before = Directory.GetFiles(PackageDirectory, "xcpng-import-*").ToHashSet();
        Assert.IsType<XenAdmin.CancelledException>(InvokeImport("disk.iso.gz", CompressionFactory.Type.Gz, cancelling: true));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(PackageDirectory, "xcpng-import-*"), file => !before.Contains(file));
    }

    [Fact]
    public void TemporaryFilesStayOnApplianceVolumeAndAreOwnedAndPrivate()
    {
        var type = typeof(ImportApplianceAction).Assembly.GetType("XenAdmin.Actions.OvfActions.ImportTemporaryFiles")!;
        var owner = (IDisposable)Activator.CreateInstance(type, new object[] { PackageDirectory })!;
        string path;
        using (var stream = (FileStream)type.GetMethod("Create")!.Invoke(owner, new object[] { ".vhd" })!)
        {
            path = stream.Name;
            Assert.True(File.Exists(path));
            Assert.Equal(Path.GetFullPath(PackageDirectory), Path.GetDirectoryName(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        owner.Dispose();
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("../enc_outside.vhd")]
    [InlineData("..\\enc_outside.vhd")]
    [InlineData(".. /enc_outside.vhd")]
    [InlineData(".../enc_outside.vhd")]
    [InlineData("file:///outside.vhd")]
    [InlineData("disk.vhd:payload")]
    public void UnsafeReferencesAreRejectedAtValidationAndAtImport(string reference)
    {
        File.WriteAllText(Path.Combine(_root, "enc_outside.vhd"), "outside sentinel");
        WriteDescriptor(reference);
        Assert.False(OVF.Validate(Package.Create(Descriptor), out _));
        Assert.IsType<InvalidDataException>(InvokeImport(reference));
        Assert.Equal("outside sentinel", File.ReadAllText(Path.Combine(_root, "enc_outside.vhd")));
    }

    [Fact]
    public void AbsoluteReferenceIsRejectedEvenInsidePackage()
    {
        var path = WriteFile("disk.vhd", "disk");
        Assert.Throws<InvalidDataException>(() => OvfFilePath.Resolve(PackageDirectory, path));
    }

    [Fact]
    public void NestedOrdinaryFileIsAllowed()
    {
        Directory.CreateDirectory(Path.Combine(PackageDirectory, "disks"));
        var path = WriteFile("disks/disk.vhd", "disk");
        WriteDescriptor("disks/disk.vhd");
        Assert.Equal(path, OvfFilePath.Resolve(PackageDirectory, "disks/disk.vhd"));
        Assert.True(OVF.Validate(Package.Create(Descriptor), out _));
    }

    [Fact]
    public void DirectoryLinkCannotEscapePackage()
    {
        var outsideDirectory = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outsideDirectory, "disk.vhd"), "outside disk");
        var link = Path.Combine(PackageDirectory, "linked");
        CreateDirectoryLink(link, outsideDirectory);
        WriteDescriptor("linked/disk.vhd");
        Assert.False(OVF.Validate(Package.Create(Descriptor), out _));
        Assert.Throws<InvalidDataException>(() => OvfFilePath.Resolve(PackageDirectory, "linked/disk.vhd"));
        Assert.IsType<InvalidDataException>(InvokeImport("linked/disk.vhd"));
        Assert.Equal("outside disk", File.ReadAllText(Path.Combine(outsideDirectory, "disk.vhd")));
    }

    [Fact]
    public void PackageRootLinkIsRejected()
    {
        WriteFile("disk.vhd", "disk");
        var link = Path.Combine(_root, "linked-package");
        CreateDirectoryLink(link, PackageDirectory);
        Assert.Throws<InvalidDataException>(() => OvfFilePath.Resolve(link, "disk.vhd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("descriptor-only")]
    [InlineData("disk-only")]
    public void IncompleteManifestIsRejected(string coverage)
    {
        WriteDescriptor("disk.vhd");
        WriteFile("disk.vhd", "disk");
        var entries = coverage == "descriptor-only" ? Digest("appliance.ovf") :
            coverage == "disk-only" ? Digest("disk.vhd") : "";
        WriteFile("appliance.mf", entries);
        Assert.Throws<InvalidDataException>(() => Package.Create(Descriptor).VerifyManifest());
    }

    [Fact]
    public void CompleteManifestPassesAndThenRejectsTamperedDisk()
    {
        WriteDescriptor("disk.vhd");
        WriteFile("disk.vhd", "disk");
        WriteFile("appliance.mf", Digest("appliance.ovf") + Digest("disk.vhd"));
        Package.Create(Descriptor).VerifyManifest();
        WriteFile("disk.vhd", "tampered");
        Assert.ThrowsAny<Exception>(() => Package.Create(Descriptor).VerifyManifest());
    }

    [Fact]
    public void ManifestRejectsDuplicateCanonicalPaths()
    {
        WriteDescriptor("disk.vhd");
        WriteFile("disk.vhd", "disk");
        WriteFile("appliance.mf", Digest("appliance.ovf") + Digest("disk.vhd") + Digest("./disk.vhd"));
        Assert.Throws<InvalidDataException>(() => Package.Create(Descriptor).VerifyManifest());
    }

    [Fact]
    public void ManifestCannotReadAnOutsideFile()
    {
        WriteDescriptor("disk.vhd");
        WriteFile("disk.vhd", "disk");
        File.WriteAllText(Path.Combine(_root, "outside.vhd"), "outside disk");
        WriteFile("appliance.mf", Digest("appliance.ovf") + Digest("disk.vhd") + Digest("../outside.vhd"));
        Assert.Throws<InvalidDataException>(() => Package.Create(Descriptor).VerifyManifest());
    }

    [Theory]
    [InlineData("config.mf", false)]
    [InlineData("config.cert", false)]
    [InlineData("payloads/appliance.mf", false)]
    [InlineData("payloads/appliance.cert", false)]
    [InlineData("config.mf", true)]
    [InlineData("config.cert", true)]
    [InlineData("payloads/appliance.mf", true)]
    [InlineData("payloads/appliance.cert", true)]
    public void OrdinaryMetadataExtensionsRequireCoverageAndRejectTampering(string payload, bool archive)
    {
        WriteDescriptorWithExtraReference(payload);
        WriteFile("disk.vhd", "disk");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(PackageDirectory, payload))!);
        WriteFile(payload, "original referenced resource");
        var manifest = Digest("appliance.ovf") + Digest("disk.vhd");
        WriteFile("appliance.mf", manifest);

        Package CreatePackage() => Package.Create(archive
            ? WriteArchive("appliance.ovf", "appliance.mf", "disk.vhd", payload)
            : Descriptor);

        var incomplete = CreatePackage();
        Assert.True(OVF.Validate(incomplete, out _));
        Assert.ThrowsAny<Exception>(() => incomplete.VerifyManifest());

        WriteFile("appliance.mf", manifest + Digest(payload));
        CreatePackage().VerifyManifest();
        WriteFile(payload, "tampered referenced resource");
        Assert.ThrowsAny<Exception>(() => CreatePackage().VerifyManifest());
    }

    [Theory]
    [InlineData("missing.mf", false)]
    [InlineData("missing.cert", false)]
    [InlineData("missing.mf", true)]
    [InlineData("missing.cert", true)]
    public void MissingPayloadWithMetadataExtensionFailsValidationAndManifestVerification(string reference, bool archive)
    {
        WriteDescriptorWithExtraReference(reference);
        WriteFile("disk.vhd", "disk");
        WriteFile("appliance.mf", Digest("appliance.ovf") + Digest("disk.vhd"));
        var package = Package.Create(archive ? WriteArchive("appliance.ovf", "appliance.mf", "disk.vhd") : Descriptor);
        Assert.False(OVF.Validate(package, out _));
        Assert.ThrowsAny<Exception>(() => package.VerifyManifest());
    }

    [Theory]
    [InlineData("appliance.mf", false)]
    [InlineData("appliance.cert", false)]
    [InlineData("./appliance.mf", false)]
    [InlineData("sub/../appliance.cert", false)]
    [InlineData("appliance.mf", true)]
    [InlineData("appliance.cert", true)]
    [InlineData("./appliance.mf", true)]
    [InlineData("sub/../appliance.cert", true)]
    public void ActualPackageManifestAndCertificateCannotBePayloadReferences(string reference, bool archive)
    {
        WriteDescriptorWithExtraReference(reference);
        WriteFile("disk.vhd", "disk");
        WriteFile("appliance.mf", Digest("appliance.ovf") + Digest("disk.vhd"));
        WriteFile("appliance.cert", "certificate placeholder");
        var package = Package.Create(archive
            ? WriteArchive("appliance.ovf", "appliance.mf", "appliance.cert", "disk.vhd")
            : Descriptor);
        Assert.False(OVF.Validate(package, out _));
        Assert.Throws<InvalidDataException>(() => package.VerifyManifest());
    }

    [Fact]
    public void OvaMetadataBeforeDescriptorCanStillBeLoadedOnASecondScan()
    {
        WriteDescriptor("disk.vhd");
        WriteFile("disk.vhd", "disk");
        WriteFile("appliance.mf", Digest("appliance.ovf") + Digest("disk.vhd"));
        var package = Package.Create(WriteArchive("appliance.mf", "appliance.ovf", "disk.vhd"));
        Assert.True(package.HasManifest());
        Assert.True(OVF.Validate(package, out _));
        package.VerifyManifest();
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("tampered")]
    [InlineData("incomplete")]
    [InlineData("empty")]
    public void OvaManifestVerificationRetainsCoverageAndIntegrityChecks(string scenario)
    {
        WriteDescriptor("disk.vhd");
        WriteFile("disk.vhd", "original disk");
        var manifest = scenario == "empty" ? "" : Digest("appliance.ovf");
        if (scenario == "complete" || scenario == "tampered")
            manifest += Digest("disk.vhd");
        WriteFile("appliance.mf", manifest);
        if (scenario == "tampered")
            WriteFile("disk.vhd", "modified disk");

        var archive = WriteArchive("appliance.ovf", "appliance.mf", "disk.vhd");

        if (scenario == "complete")
            Package.Create(archive).VerifyManifest();
        else
            Assert.ThrowsAny<Exception>(() => Package.Create(archive).VerifyManifest());
    }

    [Theory]
    [InlineData("ovf:/disk/disk10")]
    [InlineData("disk10")]
    public void DiskPrefixCollisionResolvesExactDiskThroughImportHelpers(string reference)
    {
        var env = CreateTwoDiskEnvelope(reference);
        var rasd = GetDiskRasd(env);
        Assert.Equal("correct.vhd", OVF.FindRasdFileName(env, rasd, out _));
        Assert.Equal("file10", OVF.FindFileReferenceByRASD(env, rasd).id);
        Assert.Equal("disk10", OVF.FindDiskReference(env, rasd).diskId);
        Assert.Equal("disk10", OVF.FindDiskReferenceByFileId(env, "file10").diskId);
    }

    [Fact]
    public void FilePrefixCollisionAndUnsupportedDiskReferenceCannotSelectAnUnrelatedDisk()
    {
        var env = CreateTwoDiskEnvelope("ovf:/disk/disk1");
        Array.Reverse(env.References.File);
        Assert.Equal("wrong.vhd", OVF.FindRasdFileName(env, GetDiskRasd(env), out _));
        Assert.Equal("file1", OVF.FindFileReference(env, "file1").id);
        Assert.Null(OVF.FindDiskReference(env, "arbitrary-prefix-disk1"));
        Assert.Null(OVF.FindFileReference(env, "file"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateDiskOrFileIdsFailValidation(bool duplicateDisk)
    {
        var xml = TwoDiskXml("ovf:/disk/disk10");
        xml = duplicateDisk ? xml.Replace("ovf:diskId=\"disk10\"", "ovf:diskId=\"disk1\"") :
            xml.Replace("ovf:id=\"file10\"", "ovf:id=\"file1\"");
        WriteFile("appliance.ovf", xml);
        WriteFile("correct.vhd", "disk 10");
        WriteFile("wrong.vhd", "disk 1");
        Assert.False(OVF.Validate(Package.Create(Descriptor), out _));
    }

    private Exception InvokeImport(string reference, CompressionFactory.Type? compression = null, bool cancelling = false)
    {
        // Fail before any host API call; this still executes the real import cleanup.
        var action = RuntimeHelpers.GetUninitializedObject(typeof(ImportApplianceAction));
        if (cancelling)
            typeof(XenAdmin.Actions.CancellingAction).GetField("cancelling", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(action, true);
        var method = typeof(ImportApplianceAction).GetMethod("ImportFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(action,
            new object?[] { "review", PackageDirectory, reference, compression, null, "", null, false, (Action<float>)(_ => { }) }));
        return exception.InnerException!;
    }

    private void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            // Junction creation does not require Windows symbolic-link privileges.
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        else
            Directory.CreateSymbolicLink(link, target);
        _links.Add(link);
    }

    private string WriteFile(string name, string contents)
    {
        var path = Path.GetFullPath(Path.Combine(PackageDirectory, name));
        File.WriteAllText(path, contents);
        return path;
    }

    private string Digest(string name) => $"SHA256({name})= {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(PackageDirectory, name))))}\n";
    private void WriteDescriptor(string reference) => WriteFile("appliance.ovf", DescriptorXml(reference));
    private void WriteDescriptorWithExtraReference(string reference) => WriteFile("appliance.ovf", DescriptorXml("disk.vhd")
        .Replace("</References>", $"<File ovf:id=\"extra\" ovf:href=\"{reference}\"/></References>"));

    private string WriteArchive(params string[] names)
    {
        var archive = Path.Combine(_root, "appliance.ova");
        using (var output = File.Create(archive))
        using (var writer = new TarWriter(output))
        {
            foreach (var name in names)
            {
                using var input = File.OpenRead(Path.Combine(PackageDirectory, name));
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = input });
            }
        }
        return archive;
    }

    private static EnvelopeType CreateTwoDiskEnvelope(string reference) => Tools.DeserializeOvfXml(TwoDiskXml(reference));
    private static RASD_Type GetDiskRasd(EnvelopeType env) => ((VirtualHardwareSection_Type)env.Item.Items[0]).Item[0];
    private static string TwoDiskXml(string reference) => DescriptorXml("wrong.vhd")
        .Replace("</References>", "<File ovf:id=\"file10\" ovf:href=\"correct.vhd\"/></References>")
        .Replace("</DiskSection>", "<Disk ovf:diskId=\"disk10\" ovf:fileRef=\"file10\" ovf:capacity=\"1048576\"/></DiskSection>")
        .Replace("ovf:/disk/disk1<", reference + "<");

    private static string DescriptorXml(string reference) => $$"""
        <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1" xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1" xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData">
          <References><File ovf:id="file1" ovf:href="{{reference}}"/></References>
          <DiskSection><Info>Disks</Info><Disk ovf:diskId="disk1" ovf:fileRef="file1" ovf:capacity="1048576"/></DiskSection>
          <VirtualSystem ovf:id="vm1"><Info>VM</Info><Name>Review</Name><VirtualHardwareSection><Info>Hardware</Info><Item><rasd:InstanceID>17</rasd:InstanceID><rasd:ResourceType>17</rasd:ResourceType><rasd:HostResource>ovf:/disk/disk1</rasd:HostResource></Item></VirtualHardwareSection></VirtualSystem>
        </Envelope>
        """;

    public void Dispose()
    {
        foreach (var link in _links) Directory.Delete(link);
        Directory.Delete(_root, recursive: true);
    }
}
