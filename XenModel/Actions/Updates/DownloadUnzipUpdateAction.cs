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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using XenCenterLib.Archive;
using XenAdmin.Actions.Updates;
using XenAdmin.Core;

namespace XenAdmin.Actions
{
    internal enum DownloadState
    {
        InProgress,
        Cancelled,
        Completed,
        Error
    }

    public abstract class DownloadUnzipUpdateAction : AsyncAction
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        protected readonly string UpdateName;
        protected readonly string[] updateFileSuffixes;

        public string UpdatePath { get; protected set; }

        public string ByteProgressDescription { get; set; }

        protected DownloadUnzipUpdateAction(string updateName, params string[] updateFileExtensions)
            : base(null, string.Empty, string.Empty, true) 
        {
            UpdateName = updateName;
            updateFileSuffixes = (from item in updateFileExtensions select $".{item}").ToArray();
        }

        protected string ExtractFile(string zippedFilePath, bool deleteOriginal)
        {
            log.DebugFormat("Extracting XenServer update '{0}'", UpdateName);
            Description = string.Format(Messages.DOWNLOAD_AND_EXTRACT_ACTION_EXTRACTING_DESC, UpdateName);

            ArchiveIterator iterator = null;
            ZipArchiveIterator zipIterator =  null;

            try
            {
                using (Stream stream = new FileStream(zippedFilePath, FileMode.Open, FileAccess.Read))
                {
                    iterator = ArchiveFactory.Reader(ArchiveFactory.Type.Zip, stream);
                    zipIterator = iterator as ZipArchiveIterator;

                    if (zipIterator != null)
                        zipIterator.CurrentFileExtracted += UpdateExtractionProgress;

                    while (iterator.HasNext())
                    {
                        string currentExtension = Path.GetExtension(iterator.CurrentFileName());

                        if (updateFileSuffixes.Any(item => item.ToLowerInvariant() == currentExtension.ToLowerInvariant()))
                        {
                            // Use the leaf name only so archive paths cannot escape the temp directory (Zip Slip).
                            string leafName = Path.GetFileName(iterator.CurrentFileName());
                            if (string.IsNullOrEmpty(leafName))
                                continue;

                            string path = Path.Combine(Path.GetTempPath(), leafName);

                            log.InfoFormat(
                                "Found '{0}' in the downloaded archive when looking for a '{1}' file. Extracting...",
                                iterator.CurrentFileName(), currentExtension);

                            using (Stream outputStream = new FileStream(path, FileMode.Create))
                            {
                                iterator.ExtractCurrentFile(outputStream, null);
                                log.InfoFormat("Update file extracted to '{0}'", path);
                                return path;
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                log.Error("Exception occurred when extracting downloaded archive.", e);
                throw new Exception(Messages.DOWNLOAD_AND_EXTRACT_ACTION_EXTRACTING_ERROR);
            }
            finally
            {
                if (zipIterator != null)
                    zipIterator.CurrentFileExtracted -= UpdateExtractionProgress;

                iterator?.Dispose();

                if (deleteOriginal)
                {
                    try
                    {
                        File.Delete(zippedFilePath);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                log.DebugFormat("Extracting XenServer update '{0}' completed", UpdateName);
            }
        }

        protected override void CancelRelatedTask()
        {
        }

        protected abstract void UpdateExtractionProgress(int fileIndex, int totalFileCount);
    }


    public class DownloadAndUnzipUpdateAction : DownloadUnzipUpdateAction, IByteProgressAction
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// If you consider increasing this for any reason (I think 5 is already more than enough),
        /// have a look at the usage of SLEEP_TIME_BEFORE_RETRY_MS in DownloadFile() as well.
        /// </summary>
        private const int MAX_NUMBER_OF_TRIES = 5;
        private const int SLEEP_TIME_BEFORE_RETRY_MS = 5000;

        private HttpFileDownloader _downloader;
        private readonly Uri _updateUri;
        private readonly bool _skipUnzipping;
        private DownloadState _updateDownloadState;
        private Exception _updateDownloadError;

        public DownloadAndUnzipUpdateAction(string updateName, Uri uri, params string[] updateFileExtensions)
            : base(updateName, updateFileExtensions)
        {
            Title = string.Format(Messages.DOWNLOAD_AND_EXTRACT_ACTION_TITLE, updateName);
            _updateUri = uri;
            _skipUnzipping = updateFileSuffixes.Any(item => uri.ToString().ToLowerInvariant().Contains(item.ToLowerInvariant()));
        }

        protected override void Run()
        {
            DownloadFile(out var localPath);

            if (IsCompleted || Cancelled)
                return;

            if (Cancelling)
                throw new CancelledException();

            if (_skipUnzipping)
            {
                try
                {
                    string newFilePath = Path.Combine(Path.GetDirectoryName(localPath), UpdateName);
                    if (File.Exists(newFilePath))
                        File.Delete(newFilePath);
                    File.Move(localPath, newFilePath);
                    UpdatePath = newFilePath;
                    log.DebugFormat("XenServer update '{0}' is ready", UpdateName);
                }
                catch (Exception e)
                {
                    log.Error("Exception occurred when preparing archive.", e);
                    throw;
                }
            }
            else
            {
                UpdatePath = ExtractFile(localPath, true);
            }

            if (string.IsNullOrEmpty(UpdatePath))
            {
                log.InfoFormat(
                    "The downloaded archive does not contain a file with any of the following extensions: {0}",
                    string.Join(", ", updateFileSuffixes));
                throw new Exception(Messages.DOWNLOAD_AND_EXTRACT_ACTION_FILE_NOT_FOUND);
            }

            Description = Messages.COMPLETED;
        }

        private static bool IsFileServiceUri(Uri uri)
        {
            var updateUriPrefix = new Uri(InvisibleMessages.UPDATE_URL_PREFIX);

            if (uri.Host == updateUriPrefix.Host)
                return true;

            var customUpdateUriPrefix = XenAdminConfigManager.Provider.GetCustomFileServicePrefix();
            if (!string.IsNullOrEmpty(customUpdateUriPrefix))
            {
                var customUpdateUri = new Uri(customUpdateUriPrefix);

                if (uri.Host == customUpdateUri.Host)
                    return true;
            }

            return false;
        }

        private void DownloadFile(out string outputFileName)
        {
            outputFileName = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

            _downloader = new HttpFileDownloader();
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;

            string authorizationHeader = null;
            if (IsFileServiceUri(_updateUri))
            {
                log.InfoFormat("Authenticating account...");
                Description = string.Format(Messages.DOWNLOAD_AND_EXTRACT_ACTION_AUTHENTICATING_DESC,
                    BrandManager.CompanyNameLegacy);
                var credential = TokenManager.GetDownloadCredential(XenAdminConfigManager.Provider);
                authorizationHeader = $"Basic {credential}";
            }

            log.InfoFormat("Downloading update '{0}' (from '{1}') to '{2}'", UpdateName, _updateUri, outputFileName);
            Description = string.Format(Messages.DOWNLOAD_AND_EXTRACT_ACTION_DOWNLOADING_DESC, UpdateName);
            LogDescriptionChanges = false;

            int errorCount = 0;
            bool needToRetry = false;

            try
            {
                do
                {
                    if (Cancelling)
                        throw new CancelledException();

                    if (needToRetry)
                        Thread.Sleep(SLEEP_TIME_BEFORE_RETRY_MS);

                    needToRetry = false;
                    _updateDownloadState = DownloadState.InProgress;
                    _updateDownloadError = null;

                    var proxy = XenAdminConfigManager.Provider.GetProxyFromSettings(null, false);

                    try
                    {
                        _downloader.Download(
                            _updateUri,
                            outputFileName,
                            proxy,
                            authorizationHeader,
                            noCache: false,
                            onProgress: ReportProgress);

                        if (Cancelling || Cancelled)
                        {
                            _updateDownloadState = DownloadState.Cancelled;
                            throw new CancelledException();
                        }

                        _updateDownloadState = DownloadState.Completed;
                        log.Debug($"XenServer update '{UpdateName}' download completed successfully");
                    }
                    catch (OperationCanceledException)
                    {
                        if (_updateDownloadState == DownloadState.Error)
                        {
                            needToRetry = true;
                            errorCount++;
                            LogRetry(errorCount);
                            continue;
                        }

                        _updateDownloadState = DownloadState.Cancelled;
                        log.Debug($"XenServer update '{UpdateName}' download cancelled by the user");
                        throw new CancelledException();
                    }
                    catch (Exception ex)
                    {
                        _updateDownloadError = MapDownloadError(ex);
                        _updateDownloadState = DownloadState.Error;
                        needToRetry = true;
                        errorCount++;
                        LogRetry(errorCount);
                    }
                } while (errorCount < MAX_NUMBER_OF_TRIES && needToRetry);
            }
            finally
            {
                LogDescriptionChanges = true;
                NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
                _downloader.Dispose();
                _downloader = null;
            }

            if (_updateDownloadState == DownloadState.Cancelled)
                throw new CancelledException();

            if (_updateDownloadState == DownloadState.Error)
            {
                log.Error($"Giving up; maximum number of retries ({MAX_NUMBER_OF_TRIES}) has been reached.");
                throw _updateDownloadError ?? new Exception(Messages.ERROR_UNKNOWN);
            }
        }

        private void LogRetry(int errorCount)
        {
            log.ErrorFormat("Failed to download '{0}' (attempt {1} of maximum {2}.",
                _updateUri, errorCount, MAX_NUMBER_OF_TRIES);

            if (_updateDownloadError == null)
                log.Error("An unknown error occurred.");
            else
                log.Error(_updateDownloadError);
        }

        private Exception MapDownloadError(Exception error)
        {
            if (error is HttpRequestException httpEx && httpEx.Data["StatusCode"] is HttpStatusCode statusCode)
            {
                switch (statusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        log.Error($"Could not download {UpdateName} (401 Unauthorized). Client ID may be invalid or revoked.");
                        return new Exception(Messages.FILESERVICE_ERROR_401);

                    case HttpStatusCode.Forbidden:
                        log.Error($"Could not download {UpdateName} (403 Forbidden). The account has insufficient permissions.");
                        return new Exception(Messages.FILESERVICE_ERROR_403);

                    case HttpStatusCode.NotFound:
                        log.Error($"Could not download {UpdateName} (404 File not found).");
                        return new Exception(Messages.FILESERVICE_ERROR_404);
                }
            }

            log.Debug($"XenServer patch '{UpdateName}' download failed");
            return error;
        }

        private void ReportProgress(long bytesReceived, long? totalBytes)
        {
            var total = totalBytes.GetValueOrDefault();
            int pc = total > 0 ? (int)(95.0 * bytesReceived / total) : 0;
            var descr = string.Format(Messages.DOWNLOAD_AND_EXTRACT_ACTION_DOWNLOADING_DETAILS_DESC, UpdateName,
                Util.DiskSizeString(bytesReceived, "F1"),
                Util.DiskSizeString(total > 0 ? total : bytesReceived));
            ByteProgressDescription = descr;
            Tick(pc, descr);
        }

        private void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable && _downloader != null && _updateDownloadState == DownloadState.InProgress)
            {
                _updateDownloadError = new WebException(Messages.NETWORK_CONNECTIVITY_ERROR);
                _updateDownloadState = DownloadState.Error;
                _downloader.Cancel();
            }
        }

        protected override void CancelRelatedTask()
        {
            Description = Messages.DOWNLOAD_AND_EXTRACT_ACTION_DOWNLOAD_CANCELLED_DESC;
            _downloader?.Cancel();
        }

        protected override void UpdateExtractionProgress(int fileIndex, int totalFileCount)
        {
            PercentComplete = 95 + (int)(5.0 * fileIndex / totalFileCount);
        }

        public override void RecomputeCanCancel()
        {
            CanCancel = !Cancelling && !IsCompleted && _updateDownloadState == DownloadState.InProgress;
        }
    }


    public class UnzipUpdateAction : DownloadUnzipUpdateAction
    {
        private readonly string _zippedUpdatePath;

        public UnzipUpdateAction(string zippedUpdatePath, params string[] updateFileExtensions)
            : base(Path.GetFileNameWithoutExtension(zippedUpdatePath), updateFileExtensions)
        {
            Title = string.Format(Messages.UPDATES_WIZARD_EXTRACT_ACTION_TITLE, zippedUpdatePath);
            _zippedUpdatePath = zippedUpdatePath;
        }

        protected override void Run()
        {
            UpdatePath = ExtractFile(_zippedUpdatePath, false);
            Description = Messages.COMPLETED;
        }

        protected override void UpdateExtractionProgress(int fileIndex, int totalFileCount)
        {
            PercentComplete = (int)(100.0 * fileIndex / totalFileCount);
        }

        public override void RecomputeCanCancel()
        {
            CanCancel = !Cancelling && !IsCompleted;
        }
    }
}
