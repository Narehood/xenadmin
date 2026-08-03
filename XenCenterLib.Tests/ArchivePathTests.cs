using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using XenCenterLib.Archive;
using Xunit;

namespace XenCenterLib.Tests
{
    public class ArchivePathTests
    {
        [Theory]
        [InlineData("../evil.txt")]
        [InlineData("..\\evil.txt")]
        [InlineData("safe/../../evil.txt")]
        [InlineData("/tmp/evil.txt")]
        public void GetSafeExtractPath_RejectsTraversalOrRootedPaths(string entryName)
        {
            var destination = Path.Combine(Path.GetTempPath(), "xcpng-archive-safe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);
            try
            {
                Assert.Throws<InvalidDataException>(() => ArchivePath.GetSafeExtractPath(destination, entryName));
            }
            finally
            {
                Directory.Delete(destination, true);
            }
        }

        [Fact]
        public void GetSafeExtractPath_AllowsNestedRelativePaths()
        {
            var destination = Path.Combine(Path.GetTempPath(), "xcpng-archive-safe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);
            try
            {
                var resolved = ArchivePath.GetSafeExtractPath(destination, "nested/file.txt");
                Assert.StartsWith(Path.GetFullPath(destination), Path.GetFullPath(resolved));
                Assert.EndsWith(Path.Combine("nested", "file.txt"), resolved);
            }
            finally
            {
                Directory.Delete(destination, true);
            }
        }

        [Fact]
        public void ExtractAllContents_RejectsZipSlipEntries()
        {
            var destination = Path.Combine(Path.GetTempPath(), "xcpng-archive-extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);

            try
            {
                using (var zipStream = new MemoryStream())
                {
                    using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                    {
                        var entry = zip.CreateEntry("../evil.txt");
                        using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
                            writer.Write("should-not-extract");
                    }

                    zipStream.Position = 0;
                    using (var iterator = new ZipArchiveIterator(zipStream))
                    {
                        Assert.Throws<InvalidDataException>(() => iterator.ExtractAllContents(destination));
                    }
                }

                Assert.Empty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
            }
            finally
            {
                if (Directory.Exists(destination))
                    Directory.Delete(destination, true);
            }
        }

        [Fact]
        public void ExtractAllContents_WritesSafeNestedEntries()
        {
            var destination = Path.Combine(Path.GetTempPath(), "xcpng-archive-extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(destination);

            try
            {
                using (var zipStream = new MemoryStream())
                {
                    using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, true))
                    {
                        var entry = zip.CreateEntry("nested/ok.txt");
                        using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8))
                            writer.Write("ok");
                    }

                    zipStream.Position = 0;
                    using (var iterator = new ZipArchiveIterator(zipStream))
                        iterator.ExtractAllContents(destination);
                }

                var extracted = Path.Combine(destination, "nested", "ok.txt");
                Assert.True(File.Exists(extracted));
                Assert.Equal("ok", File.ReadAllText(extracted));
            }
            finally
            {
                if (Directory.Exists(destination))
                    Directory.Delete(destination, true);
            }
        }
    }
}
