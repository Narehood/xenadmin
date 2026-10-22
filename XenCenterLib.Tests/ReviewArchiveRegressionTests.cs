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
        /// <summary>Creates an isolated directory for archive and transformation fixtures.</summary>
        public ReviewArchiveRegressionTests() => Directory.CreateDirectory(root);
        /// <summary>Removes only this fixture instance's disposable archive directory.</summary>
        public void Dispose() => Directory.Delete(root, true);

        /// <summary>Verifies exact checksum-table coverage and per-block integrity while preserving legacy archives without a global table.</summary>
        /// <param name="blocks">Number of payload blocks written into the fixture.</param>
        /// <param name="declared">Number of blocks declared by the global table; zero exercises the legacy format.</param>
        /// <param name="damage">Whether to damage the first payload without changing its checksum.</param>
        /// <param name="valid">Whether the verifier should accept the fixture.</param>
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

        /// <summary>Checks that replacing a large support report creates a shorter valid ZIP with only the new entry and no temporary output.</summary>
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

        /// <summary>Preserves the prior report and removes pending output after archive failure or cancellation.</summary>
        /// <param name="cancel">Whether to cancel instead of failing with a missing source.</param>
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

        /// <summary>Checks compression and decompression destination collisions leave the source, existing destination and descriptor reference unchanged.</summary>
        /// <param name="compress">Whether to compress rather than decompress.</param>
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

        /// <summary>Exercises successful compression and decompression through descriptor updates and verifies the original contents.</summary>
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

        /// <summary>Whether this runner can exercise Windows read-only file-deletion behavior.</summary>
        public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>On Windows, makes the source read-only and verifies output rollback, metadata preservation and a later successful retry.</summary>
        [Fact(Skip = "Read-only source deletion is a Windows file-system behavior.", SkipUnless = nameof(IsWindows))]
        public void FailedSourceDeletionRollsBackOutputAndAllowsRetry()
        {
            var source = Path.Combine(root, "disk.iso");
            File.WriteAllText(source, "original data");
            var env = Envelope("disk.iso", null);
            File.SetAttributes(source, FileAttributes.ReadOnly);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => Transform(env, true));
                Assert.Equal("original data", File.ReadAllText(source));
                Assert.False(File.Exists(source + ".gz"));
                Assert.Equal("disk.iso", env.References.File[0].href);
            }
            finally { File.SetAttributes(source, FileAttributes.Normal); }
            Transform(env, true);
            Assert.True(File.Exists(source + ".gz"));
            Assert.False(File.Exists(source));
        }

        /// <summary>Checks cancellation of either transformation direction preserves the source bytes and descriptor reference.</summary>
        /// <param name="compress">Whether to compress rather than decompress.</param>
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

        /// <summary>Runs the requested production transformation and descriptor save against the fixture directory.</summary>
        /// <param name="env">Descriptor whose local payload references are transformed.</param>
        /// <param name="compress">Whether to compress rather than decompress.</param>
        private void Transform(EnvelopeType env, bool compress)
        {
            if (compress) OVF.CompressFiles(env, Path.Combine(root, "appliance.ovf"), CompressionFactory.Type.Gz, () => { });
            else OVF.UncompressFiles(env, Path.Combine(root, "appliance.ovf"), () => { });
        }
        /// <summary>Creates a minimal descriptor containing one local payload reference for transformation fixtures.</summary>
        /// <param name="name">The private production method or payload name selected by the fixture.</param>
        /// <param name="compression">Optional compression marker on the payload.</param>
        /// <returns>A minimal one-payload descriptor.</returns>
        private static EnvelopeType Envelope(string name, string compression) => new EnvelopeType
        { References = new References_Type { File = new[] { new File_Type { href = name, compression = compression } } } };
        /// <summary>Calls the production report writer directly to test its commit boundary without downloading a support bundle.</summary>
        /// <param name="action">The production report action under test.</param>
        /// <param name="folder">Input directory to archive.</param>
        /// <param name="cancel">Whether to cancel instead of failing with a missing source.</param>
        private static void Zip(ZipStatusReportAction action, string folder, Action cancel) =>
            typeof(ZipStatusReportAction).GetMethod("ZipToOutputFile", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(action, new object[] { folder, cancel, null });
    }
}
