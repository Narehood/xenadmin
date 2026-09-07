/* Copyright (c) Cloud Software Group, Inc. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using XenCenterLib;
using XenCenterLib.Archive;
using XenOvf.Definitions;
using XenOvf.Utilities;


namespace XenOvf
{
    /// <summary>
    /// Provides the properties of a digest for a file in the manifest of an OVF/OVA appliance.
    /// </summary>
    public class FileDigest
    {
        /// <summary>
        /// Creates a new instance from a line in the manifest.
        /// </summary>
        /// <param name="line"></param>
        public FileDigest(string line)
        {
            var fields = line.Split('(', ')', '=');

            // Expect the first field to be the security algorithm used to compute the hash of the file.
            AlgorithmName = fields[0].Trim();

            // Expect the second field to be the name of the file.
            Match nameMatch = new Regex(@"[\(](.*)[\)][=]").Match(line);
            Name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : fields[1].Trim();

            // Expect the last field to be the digest of the file.
            DigestAsString = fields[fields.Length - 1].Trim();
            Digest = ToArray(DigestAsString);
        }

        public FileDigest(string fileName, byte[] digest, string hashingAlgorithm)
        {
            AlgorithmName = hashingAlgorithm;
            Name = fileName;
            Digest = digest;
            DigestAsString = Digest == null ? "" : string.Join("", Digest.Select(b => $"{b:x2}"));
        }

        /// <summary>
        /// Name of the file with the digest.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Name of the algorithm to compute the digest (e.g. SHA1, SHA256).
        /// </summary>
        public string AlgorithmName { get; }

        public string DigestAsString { get; }

        public byte[] Digest{ get; }

        public string ToManifestLine()
        {
            return $"{AlgorithmName}({Name})= {DigestAsString}";
        }

        /// <summary>
        /// Convert a hex string to a binary array.
        /// </summary>
        private static byte[] ToArray(string HexString)
        {
            if (HexString.Length % 2 > 0)
            {
                // The length is odd.
                // Pad the hex string on the left with a space to interpret as a leading zero.
                HexString = HexString.PadLeft(HexString.Length + 1);
            }

            var array = new byte[HexString.Length / 2];

            try
            {
                for (int i = 0; i < HexString.Length; i += 2)
                {
                    array[i / 2] = byte.Parse(HexString.Substring(i, 2), System.Globalization.NumberStyles.HexNumber);
                }
            }
            catch
            {
                throw new Exception(HexString + " contains an invalid hexadecimal number.");
            }

            return array;
        }
    }


    /// <summary>
    /// OVF package, i.e. appliance in the form of a folder
    /// </summary>
    internal class FolderPackage : Package
    {
        private string _Folder;

        public FolderPackage(string path) : this(path, CancellationToken.None)
        {
        }

        public FolderPackage(string path, CancellationToken cancellationToken)
            : base(path, cancellationToken)
        {
            _Folder = path;

            var extension = Path.GetExtension(path);

            if (string.Compare(extension, OVF_EXT, true) == 0)
                _Folder = Path.GetDirectoryName(path);

            if (_Folder == null)
            {
                // Handle a package in the root or current folder.
                _Folder = "";
            }
        }

        public override string DescriptorFileName => Path.GetFileName(PackageSourceFile);

        protected override byte[] ReadAllBytes(string fileName)
        {
            try
            {
                return File.ReadAllBytes(OvfFilePath.Resolve(_Folder, fileName));
            }
            catch
            {
                return null;
            }
        }

        protected override string ReadAllText(string fileName)
        {
            try
            {
                return File.ReadAllText(OvfFilePath.Resolve(_Folder, fileName));
            }
            catch
            {
                return null;
            }
        }

        public override void VerifyManifest()
        {
            var manifest = Manifest;
            if (OvfEnvelope == null)
                throw new InvalidDataException("Cannot verify a manifest without a valid OVF descriptor.");

            var required = new HashSet<string>(OvfFilePath.Comparer)
            {
                OvfFilePath.Resolve(_Folder, DescriptorFileName)
            };
            foreach (var file in OvfEnvelope.References?.File ?? new File_Type[0])
            {
                ValidatePayloadReference(file.href);
                required.Add(OvfFilePath.Resolve(_Folder, file.href));
            }

            var digests = new Dictionary<string, FileDigest>(OvfFilePath.Comparer);
            foreach (var digest in manifest)
            {
                var path = OvfFilePath.Resolve(_Folder, digest.Name);
                if (digests.ContainsKey(path))
                    throw new InvalidDataException("The OVF manifest contains duplicate file entries.");
                digests.Add(path, digest);
            }

            if (required.Any(path => !digests.ContainsKey(path)))
                throw new InvalidDataException(Messages.SECURITY_FILE_MISSING_FROM_MANIFEST);

            // For a folder package, it is efficient to iterate by the order of files in the manifest.
            foreach (var entry in digests)
            {
                var fileDigest = entry.Value;
                using (var stream = File.OpenRead(entry.Key))
                {
                    if (!StreamUtilities.VerifyAgainstDigest(stream, stream.Length, fileDigest.AlgorithmName, fileDigest.Digest))
                        throw new Exception(string.Format(Messages.SECURITY_SIGNATURE_FAILED, fileDigest.Name));
                }
            }
        }

        public override bool HasFile(string fileName)
        {
            try
            {
                return File.Exists(OvfFilePath.Resolve(_Folder, fileName));
            }
            catch
            {
                return false;
            }
        }

        public override void ExtractToWorkingDir(Action cancellingDelegate)
        {
            WorkingDir = Path.GetDirectoryName(PackageSourceFile);
        }
    }

    /// <summary>
    /// OVA package (may additionally be compressed), i.e. Appliance in the form of a tape archive (tar).
    /// </summary>
    internal class TarPackage : Package
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Dictionary<string, byte[]> _DirectoryCache = new Dictionary<string, byte[]>();
        private string _DescriptorFileName;

        private Stream _tarStream;
        private ArchiveIterator _archiveIterator;
        private readonly ArchiveFactory.Type _tarType = ArchiveFactory.Type.Tar;

        public TarPackage(string path) : this(path, CancellationToken.None)
        {
        }

        public TarPackage(string path, CancellationToken cancellationToken)
            : base(path, cancellationToken)
        {
            var extension = Path.GetExtension(PackageSourceFile);

            if (string.Compare(extension, ".gz", true) == 0)
                _tarType = ArchiveFactory.Type.TarGz;
        }

        public override string DescriptorFileName
        {
            get
            {
                if (_DescriptorFileName == null)
                    Load();
                return _DescriptorFileName;
            }
        }

        protected override byte[] ReadAllBytes(string fileName)
        {
            if (!_DirectoryCache.TryGetValue(fileName, out byte[] buffer) || buffer == null)
                Load();
            return _DirectoryCache.TryGetValue(fileName, out buffer) ? buffer : null;
        }

        protected override string ReadAllText(string fileName)
        {
            var buffer = ReadAllBytes(fileName);
            if (buffer == null)
                return null;

            using (var stream = new MemoryStream(buffer))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        /// <summary>
        /// Load metadata files from an OVA package. Rules:
        /// - Load the first file name with an ovf extension as the package descriptor
        /// - Load the first manifest or certificate file that has the same base name as the descriptor
        /// - Do not load any other files
        /// In most cases the ovf file is first in the package, so all metadata files
        /// can be read in one go. Otherwise, the package will have to be searched more times.
        /// </summary>
        private void Load()
        {
            try
            {
                Open();

                while (_archiveIterator.HasNext())
                {
                    var fileName = _archiveIterator.CurrentFileName();
                    var extension = Path.GetExtension(fileName);
                    // Ordinary resources may also use .mf or .cert. Cache their presence
                    // without reading large payloads as package metadata.
                    if (!_DirectoryCache.ContainsKey(fileName))
                        _DirectoryCache.Add(fileName, null);

                    if (_DescriptorFileName == null && string.Compare(extension, OVF_EXT, true) == 0)
                    {
                        _DescriptorFileName = fileName;
                    }
                    else if (string.Compare(extension, MANIFEST_EXT, true) == 0)
                    {
                        if (_DescriptorFileName == null || !SamePackagePath(fileName, ManifestFileName))
                            continue;
                        fileName = ManifestFileName;
                    }
                    else if (string.Compare(extension, CERTIFICATE_EXT, true) == 0)
                    {
                        if (_DescriptorFileName == null || !SamePackagePath(fileName, CertificateFileName))
                            continue;
                        fileName = CertificateFileName;
                    }
                    else
                        continue;

                    _DirectoryCache[fileName] = ReadAllBytes();
                }
            }
            finally
            {
                Close();
            }
        }

        /// <summary>
        /// Always remember to call Close after having used the opened package,
        /// usually in a try-finally block
        /// </summary>
        private void Open()
        {
            ReadCancellationToken.ThrowIfCancellationRequested();
            _tarStream = new CancellableReadStream(File.OpenRead(PackageSourceFile), ReadCancellationToken);
            _archiveIterator = ArchiveFactory.Reader(_tarType, _tarStream);
        }

        /// <summary>
        /// Normally used in conjunction with Open in a try-finally block
        /// </summary>
        private void Close()
        {
            _archiveIterator?.Dispose();
            _tarStream?.Dispose();
        }

        public override void CleanUpWorkingDir()
        {
            try
            {
                Directory.Delete(WorkingDir, true);
            }
            catch
            {
                //ignore
            }
        }

        public override void ExtractToWorkingDir(Action cancellingDelegate)
        {
            try
            {
                Open();

                var dir = Path.GetDirectoryName(PackageSourceFile);
                var temp = Path.Combine(dir, Path.GetRandomFileName());
                WorkingDir = temp; //set this before extraction so it can be cleaned up if extraction is aborted
                _archiveIterator.ExtractAllContents(temp, cancellingDelegate);
            }
            finally
            {
                Close();
            }
        }

        public override void VerifyManifest()
        {
            var manifest = new List<FileDigest>(Manifest);
            if (OvfEnvelope == null)
                throw new InvalidDataException("Cannot verify a manifest without a valid OVF descriptor.");
            foreach (var file in OvfEnvelope.References?.File ?? new File_Type[0])
            {
                ValidatePayloadReference(file.href);
                if (!HasFile(file.href))
                    throw new InvalidDataException(Messages.SECURITY_FILE_MISSING_FROM_PACKAGE);
            }

            try
            {
                Open();

                // For a tar package, it is more efficient to iterate the files in the order within the archive.
                while (_archiveIterator.HasNext())
                {
                    if (IsPackageMetadataFile(_archiveIterator.CurrentFileName()))
                        continue;

                    var fileDigest = manifest.Find(fd => string.Compare(fd.Name, _archiveIterator.CurrentFileName(), true) == 0);

                    if (fileDigest == null)
                    {
                        log.ErrorFormat("File {0} contained in the appliance is not listed in the manifest.", _archiveIterator.CurrentFileName());
                        throw new Exception(Messages.SECURITY_FILE_MISSING_FROM_MANIFEST);
                    }

                    manifest.Remove(fileDigest);
                    if (!_archiveIterator.VerifyCurrentFileAgainstDigest(fileDigest.AlgorithmName, fileDigest.Digest))
                        throw new Exception(string.Format(Messages.SECURITY_SIGNATURE_FAILED, fileDigest.Name));
                }
            }
            finally
            {
                Close();
            }

            if (manifest.Count > 0)
            {
                log.ErrorFormat("The following files are listed in the manifest but missing from the appliance: {0}",
                    string.Join(", ", manifest.Select(fd => fd.Name)));
                throw new Exception(Messages.SECURITY_FILE_MISSING_FROM_PACKAGE);
            }
        }

        public override bool HasFile(string fileName)
        {
            try
            {
                return DescriptorFileName != null && _DirectoryCache.ContainsKey(fileName);
            }
            catch
            {
                return false;
            }
        }

        private byte[] ReadAllBytes()
        {
            using (var ms = new MemoryStream())
            {
                _archiveIterator.ExtractCurrentFile(ms, null);
                return ms.Length > 0 ? ms.ToArray() : null;
            }
        }
    }


    /// <summary>
    /// Abstract class to describe an OVF/OVA appliance.
    /// </summary>
    public abstract class Package
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string MANIFEST_EXT = ".mf";
        public const string CERTIFICATE_EXT = ".cert";
        public const string OVF_EXT = ".ovf";

        // Cache these properties because they are expensive to get
        private string _descriptorXml;
        private byte[] _RawManifest;
        private byte[] _RawCertificate;
        private EnvelopeType _ovfEnvelope;

        protected Package(string path) : this(path, CancellationToken.None)
        {
        }

        protected Package(string path, CancellationToken cancellationToken)
        {
            PackageSourceFile = path;
            ReadCancellationToken = cancellationToken;
        }

        protected CancellationToken ReadCancellationToken { get; }

        public static Package Create(string path)
            => Create(path, CancellationToken.None);

        public static Package Create(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);

            if (string.Compare(extension, OVF_EXT, true) == 0)
                return new FolderPackage(path, cancellationToken);

            // Assume it is an archive.
            return new TarPackage(path, cancellationToken);
        }

        #region Properties

        /// <summary>
        /// According to the OVF specification, name of the *package* is base name of the descriptor file.
        /// </summary>
        public string Name => Path.GetFileNameWithoutExtension(DescriptorFileName);

        /// <summary>
        /// According to the OVF specification, base name of the manifest file must be the same as the descriptor file.
        /// </summary>
        protected string ManifestFileName => Path.ChangeExtension(DescriptorFileName, MANIFEST_EXT);

        /// <summary>
        /// According to the OVF specification, base name of the certificate file must be the same as the descriptor file.
        /// </summary>
        protected string CertificateFileName => Path.ChangeExtension(DescriptorFileName, CERTIFICATE_EXT);

        /// <summary>
        /// Contents of the OVF file.
        /// </summary>
        public string DescriptorXml
        {
            get
            {
                if (_descriptorXml == null)
                    _descriptorXml = ReadAllText(DescriptorFileName);
                return _descriptorXml;
            }
        }

        protected List<FileDigest> Manifest
        {
            get
            {
                var manifest = new List<FileDigest>();

                using (var stream = new MemoryStream(RawManifest))
                using (StreamReader reader = new StreamReader(stream))
                {
                    while (!reader.EndOfStream)
                    {
                        manifest.Add(new FileDigest(reader.ReadLine()));
                    }
                }

                return manifest;
            }
        }

        private byte[] RawManifest
        {
            get
            {
                if (_RawManifest == null)
                    _RawManifest = ReadAllBytes(ManifestFileName);
                return _RawManifest;
            }
        }

        public byte[] RawCertificate
        {
            get
            {
                if (_RawCertificate == null)
                    _RawCertificate = ReadAllBytes(CertificateFileName);
                return _RawCertificate;
            }
        }

        public string PackageSourceFile { get; }

        public string WorkingDir { get; protected set; }

        /// <summary>
        /// May be null if no valid OVF file has been found
        /// </summary>
        public EnvelopeType OvfEnvelope
        {
            get
            {
                if (_ovfEnvelope == null)
                {
                    try
                    {
                        _ovfEnvelope = Tools.DeserializeOvfXml(DescriptorXml);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Failed to load OVF content from appliance {PackageSourceFile}", ex);
                    }
                }
                return _ovfEnvelope;
            }
        }

        #endregion

        protected static bool SamePackagePath(string first, string second)
        {
            return OvfFilePath.Comparer.Equals(OvfFilePath.NormalizeReference(first), OvfFilePath.NormalizeReference(second));
        }

        protected bool IsPackageMetadataFile(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            return (string.Equals(extension, MANIFEST_EXT, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, CERTIFICATE_EXT, StringComparison.OrdinalIgnoreCase)) &&
                (SamePackagePath(fileName, ManifestFileName) || SamePackagePath(fileName, CertificateFileName));
        }

        internal void ValidatePayloadReference(string fileName)
        {
            // References are relative to the descriptor. Only its actual sibling
            // manifest/certificate are excluded by DSP0243 section 5.1; other files
            // using either extension are ordinary payloads and require verification.
            var reference = OvfFilePath.NormalizeReference(fileName);
            if (SamePackagePath(reference, Path.GetFileName(ManifestFileName)) ||
                SamePackagePath(reference, Path.GetFileName(CertificateFileName)))
                throw new InvalidDataException("The package manifest and certificate must not appear in OVF file references.");
        }

        public bool HasEncryption()
        {
            return OVF.HasEncryption(OvfEnvelope, out _);
        }

        public bool HasManifest()
        {
            return RawManifest != null;
        }

        public bool HasSignature()
        {
            return RawCertificate != null;
        }

        /// <exception cref="Exception">Thrown when verification fails for any reason</exception>>
        public void VerifySignature()
        {
            using (var certificate = new X509Certificate2(RawCertificate))
            {
                if (!certificate.Verify())
                    throw new Exception(Messages.CERTIFICATE_IS_INVALID);

                // Get the package signature from the certificate file.
                // This is the digest of the first file listed in the certificate file,
                // hence we only need to read the first line
                FileDigest fileDigest;

                using (Stream stream = new MemoryStream(RawCertificate))
                using (StreamReader reader = new StreamReader(stream))
                {
                    fileDigest = new FileDigest(reader.ReadLine());
                }

                // Verify the stored signature against the computed signature using the certificate's public key.
                // Do this independently to minimize the number of files opened concurrently.
                using (Stream stream = new MemoryStream(RawManifest))
                {
                    if (!StreamUtilities.VerifyAgainstDigest(stream, stream.Length, fileDigest.AlgorithmName, fileDigest.Digest, certificate))
                        throw new Exception(string.Format(Messages.SECURITY_SIGNATURE_FAILED, fileDigest.Name));
                }
            }
        }

        public virtual void CleanUpWorkingDir()
        {
        }

        #region Abstract members

        /// <summary>
        /// The full name of the OVF file within the package
        /// </summary>
        public abstract string DescriptorFileName { get; }

        protected abstract byte[] ReadAllBytes(string fileName);

        protected abstract string ReadAllText(string fileName);

        /// <exception cref="Exception">Thrown when verification fails for any reason</exception>>
        public abstract void VerifyManifest();

        public abstract bool HasFile(string fileName);

        /// <returns>The directory with the extracted files</returns>
        public abstract void ExtractToWorkingDir(Action cancellingDelegate);

        #endregion
    }
}
