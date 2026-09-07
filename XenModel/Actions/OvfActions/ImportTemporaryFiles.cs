using System;
using System.Collections.Generic;
using System.IO;
using XenOvf;

namespace XenAdmin.Actions.OvfActions
{
    /// <summary>Owns only files successfully created by this import operation.</summary>
    internal sealed class ImportTemporaryFiles : IDisposable
    {
        private readonly List<string> paths = new List<string>();
        private readonly string directory;
        private bool disposed;

        public ImportTemporaryFiles(string applianceDirectory)
        {
            directory = Path.GetFullPath(string.IsNullOrEmpty(applianceDirectory) ? "." : applianceDirectory);
        }

        public FileStream Create(string extension)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(ImportTemporaryFiles));
            // Keep large transformed disks on the appliance's volume, as before.
            var path = OvfFilePath.Resolve(directory, "xcpng-import-" + Guid.NewGuid().ToString("N") + extension);
#if NET8_0_OR_GREATER
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var stream = new FileStream(path, options);
#else
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
#endif
            paths.Add(path);
            return stream;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var path in paths)
            {
                try { File.Delete(path); }
                catch { /* A cleanup failure must not hide the import error. */ }
            }
            paths.Clear();
        }
    }
}
