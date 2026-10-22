using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using XenAdmin.Actions;
using XenAdmin;
using XenAdmin.Core;
using XenCenterLib.Archive;
using XenCenterLib.Compression;
using XenOvf;
using XenOvf.Definitions;
using Xunit;

namespace XenCenterLib.Tests
{
    public sealed class ReviewArchiveRegressionTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "review-archives-" + Guid.NewGuid().ToString("N"));
        public ReviewArchiveRegressionTests() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, true);

        [Theory]
        [InlineData(2, 2, false, true)]
        [InlineData(1, 2, false, false)]
        [InlineData(2, 1, false, false)]
        [InlineData(2, 2, true, false)]
        [InlineData(2, 0, false, true)] // Legacy exports without a table remain supported.
        public void XvaRequiresEveryDeclaredBlockAndRejectsUndeclaredOrDamagedBlocks(int blocks, int declared, bool damage, bool valid)
        {
            byte[] data = Enumerable.Repeat((byte)65, 512).ToArray();
            string digest;
            using (var sha = SHA1.Create()) digest = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
            var buffer = new MemoryStream();
            using (var writer = ArchiveFactory.Writer(ArchiveFactory.Type.Tar, buffer))
            {
                Action<string, byte[]> add = (name, bytes) =>
                {
                    using (var input = new MemoryStream(bytes)) writer.Add(input, name, DateTime.UtcNow, null);
                };
                add("ova.xml", Encoding.UTF8.GetBytes("<test/>"));
                for (int i = 0; i < blocks; i++)
                {
                    string name = "Ref:1/" + i.ToString("D8");
                    add(name, damage && i == 0 ? new byte[512] : data);
                    add(name + ".checksum", Encoding.ASCII.GetBytes(digest));
                }
                if (declared != 0)
                    add("checksum.xml", Encoding.UTF8.GetBytes("<checksums>" + string.Concat(Enumerable.Range(0, declared)
                        .Select(i => "<member><name>Ref:1/" + i.ToString("D8") + "</name><value>" + digest + "</value></member>")) + "</checksums>"));
            }
            using (var input = new MemoryStream(buffer.ToArray()))
            {
                Action verify = () => new CommandLib.Export(true).verify(input, null, () => false);
                if (valid) verify();
                else Assert.Throws<CommandLib.BlockChecksumFailed>(verify);
            }
        }

        [Fact]
        public void ReplacingLargeReportContainsOnlyTheNewArchive()
        {
            string dest = Path.Combine(root, "report.zip"), folder = Path.Combine(root, "input");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "new.txt"), "new");
            File.WriteAllBytes(dest, Enumerable.Repeat((byte)0x7e, 100000).ToArray());
            Zip(new ZipStatusReportAction(folder, dest), folder, null);
            Assert.True(new FileInfo(dest).Length < 100000);
            using (var stream = File.OpenRead(dest))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                Assert.Equal("new.txt", Assert.Single(zip.Entries).FullName.TrimStart('/'));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FailedOrCancelledReportPreservesExistingDestination(bool cancel)
        {
            string dest = Path.Combine(root, "report.zip"), folder = Path.Combine(root, "input");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "new.txt"), "new");
            File.WriteAllText(dest, "previous report");
            var action = new ZipStatusReportAction(folder, dest);
            Assert.ThrowsAny<Exception>(() => Zip(action, cancel ? folder : Path.Combine(root, "missing"),
                cancel ? (Action)(() => throw new CancelledException()) : null));
            Assert.Equal("previous report", File.ReadAllText(dest));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CompressionCollisionPreservesSourceDestinationAndMetadata(bool compress)
        {
            string name = compress ? "disk.iso" : "disk.iso.gz";
            string source = Path.Combine(root, name), dest = Path.Combine(root, compress ? "disk.iso.gz" : "disk.iso");
            File.WriteAllText(Path.Combine(root, "payload"), "original data");
            if (compress) File.Copy(Path.Combine(root, "payload"), source);
            else CompressionFactory.CompressFile(Path.Combine(root, "payload"), source, CompressionFactory.Type.Gz, null);
            var before = File.ReadAllBytes(source);
            File.WriteAllText(dest, "existing destination");
            var env = Envelope(name, compress ? null : "gzip");
            Assert.Throws<IOException>(() => Transform(env, compress));
            Assert.Equal(before, File.ReadAllBytes(source));
            Assert.Equal("existing destination", File.ReadAllText(dest));
            Assert.Equal(name, env.References.File[0].href);
        }

        [Fact]
        public void CompressionRoundTripPreservesContents()
        {
            File.WriteAllText(Path.Combine(root, "disk.iso"), "original data");
            var env = Envelope("disk.iso", null);
            Transform(env, true);
            Assert.Equal("disk.iso.gz", env.References.File[0].href);
            Assert.False(File.Exists(Path.Combine(root, "disk.iso")));
            Transform(env, false);
            Assert.Equal("disk.iso", env.References.File[0].href);
            Assert.Equal("original data", File.ReadAllText(Path.Combine(root, "disk.iso")));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void CancelledCompressionPreservesTheSourceAndMetadata(bool compress)
        {
            var source = Path.Combine(root, "disk.iso");
            File.WriteAllText(source, "original data");
            var env = Envelope("disk.iso", null);
            if (!compress) Transform(env, true);
            source = Path.Combine(root, env.References.File[0].href);
            var before = File.ReadAllBytes(source);
            string href = env.References.File[0].href;
            Assert.Throws<OperationCanceledException>(() =>
            {
                if (compress) OVF.CompressFiles(env, Path.Combine(root, "appliance.ovf"), CompressionFactory.Type.Gz, () => throw new OperationCanceledException());
                else OVF.UncompressFiles(env, Path.Combine(root, "appliance.ovf"), () => throw new OperationCanceledException());
            });
            Assert.Equal(before, File.ReadAllBytes(source));
            Assert.Equal(href, env.References.File[0].href);
        }

        private void Transform(EnvelopeType env, bool compress)
        {
            if (compress) OVF.CompressFiles(env, Path.Combine(root, "appliance.ovf"), CompressionFactory.Type.Gz, () => { });
            else OVF.UncompressFiles(env, Path.Combine(root, "appliance.ovf"), () => { });
        }
        private static EnvelopeType Envelope(string name, string compression) => new EnvelopeType
        { References = new References_Type { File = new[] { new File_Type { href = name, compression = compression } } } };
        private static void Zip(ZipStatusReportAction action, string folder, Action cancel) =>
            typeof(ZipStatusReportAction).GetMethod("ZipToOutputFile", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(action, new object[] { folder, cancel, null });
    }
}
