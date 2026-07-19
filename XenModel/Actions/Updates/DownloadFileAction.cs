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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using XenAdmin.Core;
using XenCenterLib;

namespace XenAdmin.Actions.Updates
{
    public class DownloadFileAction : AsyncAction, IByteProgressAction
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const int SLEEP_TIME_BEFORE_RETRY_MS = 5000;

        //If you consider increasing this for any reason (I think 5 is already more than enough),
        //have a look at the usage of SLEEP_TIME_BEFORE_RETRY_MS in DownloadFile() as well.
        private const int MAX_NUMBER_OF_TRIES = 5;

        private readonly Uri _address;
        private readonly string _outputPathAndFileName;
        private readonly string _fileName;
        private readonly bool _canDownloadFile;
        private DownloadState _fileState;
        private Exception _downloadError;
        private readonly string _authToken;
        private HttpFileDownloader _downloader;

        protected string OutputPathAndFileName => _outputPathAndFileName;
        public string ByteProgressDescription { get; set; }

        protected DownloadFileAction(string fileName, Uri uri, string outputFileName, string title, bool suppressHistory)
            : base(null, title, fileName, suppressHistory)
        {
            _fileName = fileName;
            _address = uri;
            _canDownloadFile = _address != null;
            _outputPathAndFileName = outputFileName;
            _authToken = XenAdminConfigManager.Provider.GetClientUpdatesQueryParam();
        }

        private void DownloadFile()
        {
            int errorCount = 0;
            bool needToRetry = false;

            _downloader = new HttpFileDownloader();
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;

            try
            {
                do
                {
                    if (Cancelling)
                        throw new CancelledException();

                    if (needToRetry)
                        Thread.Sleep(SLEEP_TIME_BEFORE_RETRY_MS);

                    needToRetry = false;
                    _fileState = DownloadState.InProgress;
                    _downloadError = null;

                    var uriBuilder = new UriBuilder(_address);

                    if (!string.IsNullOrEmpty(_authToken) && !uriBuilder.Uri.IsFile)
                        uriBuilder.Query = Helpers.AddAuthTokenToQueryString(_authToken, uriBuilder.Query);

                    var proxy = XenAdminConfigManager.Provider.GetProxyFromSettings(null, false);

                    try
                    {
                        _downloader.Download(
                            uriBuilder.Uri,
                            _outputPathAndFileName,
                            proxy,
                            authorizationHeader: null,
                            noCache: true,
                            onProgress: ReportProgress);

                        if (Cancelling || Cancelled)
                        {
                            _fileState = DownloadState.Cancelled;
                            throw new CancelledException();
                        }

                        _fileState = DownloadState.Completed;
                        log.DebugFormat("'{0}' download completed successfully", _fileName);
                    }
                    catch (OperationCanceledException)
                    {
                        if (_fileState == DownloadState.Error)
                        {
                            needToRetry = true;
                            errorCount++;
                            LogRetry(errorCount);
                            continue;
                        }

                        _fileState = DownloadState.Cancelled;
                        log.DebugFormat("'{0}' download cancelled by the user", _fileName);
                        throw new CancelledException();
                    }
                    catch (Exception ex)
                    {
                        _downloadError = ex;
                        _fileState = DownloadState.Error;
                        needToRetry = true;
                        errorCount++;
                        LogRetry(errorCount);
                    }
                } while (errorCount < MAX_NUMBER_OF_TRIES && needToRetry);
            }
            finally
            {
                NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
                _downloader.Dispose();
                _downloader = null;
            }

            if (_fileState == DownloadState.Cancelled)
                throw new CancelledException();

            if (_fileState == DownloadState.Error)
            {
                log.ErrorFormat("Giving up - Maximum number of retries ({0}) has been reached.", MAX_NUMBER_OF_TRIES);
                throw _downloadError ?? new Exception(Messages.ERROR_UNKNOWN);
            }
        }

        private void LogRetry(int errorCount)
        {
            log.ErrorFormat(
                "Error while downloading from '{0}'. Number of errors so far (including this): {1}. Trying maximum {2} times.",
                _address, errorCount, MAX_NUMBER_OF_TRIES);

            if (_downloadError == null)
                log.Error("An unknown error occurred.");
            else
                log.Error(_downloadError);
        }

        private void ReportProgress(long bytesReceived, long? totalBytes)
        {
            var total = totalBytes.GetValueOrDefault();
            int pc = total > 0 ? (int)(95.0 * bytesReceived / total) : 0;

            var descr = string.Format(Messages.DOWNLOAD_FILE_ACTION_PROGRESS_DESCRIPTION, _fileName,
                Util.DiskSizeString(bytesReceived, "F1"),
                Util.DiskSizeString(total > 0 ? total : bytesReceived));
            ByteProgressDescription = descr;
            Tick(pc, descr);
        }

        private void NetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable && _downloader != null && _fileState == DownloadState.InProgress)
            {
                _downloadError = new WebException(Messages.NETWORK_CONNECTIVITY_ERROR);
                _fileState = DownloadState.Error;
                _downloader.Cancel();
            }
        }

        protected override void CancelRelatedTask()
        {
            Description = Messages.DOWNLOAD_AND_EXTRACT_ACTION_DOWNLOAD_CANCELLED_DESC;
            _downloader?.Cancel();
        }

        protected override void Run()
        {
            if (!_canDownloadFile)
                return;

            log.InfoFormat("Downloading '{0}' (from '{1}') to '{2}'", _fileName, _address, _outputPathAndFileName);
            LogDescriptionChanges = false;
            DownloadFile();
            LogDescriptionChanges = true;

            if (IsCompleted || Cancelled)
                return;

            if (Cancelling)
                throw new CancelledException();

            if (!File.Exists(_outputPathAndFileName))
                throw new Exception(GetDownloadedFileNotFoundMessage());

            ValidateDownloadedFile();

            Description = Messages.COMPLETED;
        }

        protected override void CleanOnError()
        {
            ReleaseDownloadedContent(true);
        }

        protected virtual string GetDownloadedFileNotFoundMessage()
        {
            return Messages.DOWNLOAD_FILE_ACTION_NOT_FOUND;
        }

        protected virtual void ValidateDownloadedFile()
        {
        }

        public virtual void ReleaseDownloadedContent(bool deleteDownloadedContent = false)
        {
            if (!deleteDownloadedContent)
                return;

            try
            {
                if (File.Exists(_outputPathAndFileName))
                    File.Delete(_outputPathAndFileName);
            }
            catch
            {
                //ignore
            }
        }

        public override void RecomputeCanCancel()
        {
            CanCancel = !Cancelling && !IsCompleted && _fileState == DownloadState.InProgress;
        }
    }

    public class DownloadAndUpdateClientAction : DownloadFileAction
    {
        private readonly string _checksum;
        private FileStream _msiStream;

        public DownloadAndUpdateClientAction(string installerName, Uri uri, string outputFileName, string checksum)
            : base(installerName,
                  uri,
                  outputFileName,
                  string.Format(Messages.DOWNLOAD_CLIENT_INSTALLER_ACTION_TITLE, installerName),
                  true)
        {
            _checksum = checksum;
            Description = string.Format(Messages.DOWNLOAD_CLIENT_INSTALLER_ACTION_DESCRIPTION, installerName);
        }

        protected override string GetDownloadedFileNotFoundMessage()
        {
            return Messages.DOWNLOAD_CLIENT_INSTALLER_MSI_NOT_FOUND;
        }

        protected override void ValidateDownloadedFile()
        {
            Description = Messages.UPDATE_CLIENT_VALIDATING_INSTALLER;

            _msiStream = new FileStream(OutputPathAndFileName, FileMode.Open, FileAccess.Read);

            var calculatedChecksum = string.Empty;

            var hash = StreamUtilities.ComputeHash(_msiStream, out _);
            if (hash != null)
                calculatedChecksum = string.Join(string.Empty, hash.Select(b => $"{b:x2}"));

            // Check if calculatedChecksum matches what is in xcupdates.xml
            if (!_checksum.Equals(calculatedChecksum, StringComparison.InvariantCultureIgnoreCase))
                throw new Exception(Messages.UPDATE_CLIENT_INVALID_CHECKSUM);

            bool valid;
            try
            {
                // Check digital signature of .msi
                using (var basicSigner = X509Certificate.CreateFromSignedFile(OutputPathAndFileName))
                {
                    using (var cert = new X509Certificate2(basicSigner))
                        valid = cert.Verify();
                }
            }
            catch (Exception e)
            {
                throw new Exception(Messages.UPDATE_CLIENT_FAILED_CERTIFICATE_CHECK, e);
            }

            if (!valid)
                throw new Exception(Messages.UPDATE_CLIENT_INVALID_DIGITAL_CERTIFICATE);
        }

        public override void ReleaseDownloadedContent(bool deleteDownloadedContent = false)
        {
            _msiStream.Dispose();
            base.ReleaseDownloadedContent(deleteDownloadedContent);
        }
    }

    public class DownloadSourceAction : DownloadFileAction
    {
        public DownloadSourceAction(string sourceName, Uri uri, string outputFileName)
            : base(sourceName,
                  uri,
                  outputFileName,
                  string.Format(Messages.DOWNLOAD_CLIENT_SOURCE_ACTION_TITLE, sourceName),
                  false)
        {
            Description = string.Format(Messages.DOWNLOAD_CLIENT_SOURCE_ACTION_DESCRIPTION, sourceName);
        }
    }
}

