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
using System.Diagnostics;
using System.IO;
using System.Xml;
using XenAdmin.Actions.Updates;
using XenAdmin.Core;


namespace XenAdmin.Actions
{
    public class DownloadClientUpdatesXmlAction : DownloadUpdatesXmlAction
    {
        private const string ClientVersionsNode = "xencenterversions";

        private readonly bool _checkForXenCenter;

        public DownloadClientUpdatesXmlAction(bool checkForXenCenter, string userAgent, string xmlLocationUrl, bool suppressHistory)
            : base(userAgent, xmlLocationUrl, suppressHistory)
        {
            _checkForXenCenter = checkForXenCenter;
            Title = Description = string.Format(Messages.AVAILABLE_UPDATES_CHECKING, BrandManager.BrandConsole);
        }

        public List<ClientVersion> ClientVersions { get; } = new List<ClientVersion>();

        protected override void Run()
        {
            try
            {
                XmlDocument xdoc = FetchCheckForUpdatesXml();

                GetXenCenterVersions(xdoc);

                Description = Messages.COMPLETED;
            }
            catch (Exception e)
            {
                if (e is System.Net.Sockets.SocketException)
                {
                    Description = Messages.AVAILABLE_UPDATES_NETWORK_ERROR;
                }
                else if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    string errorText = e.Message.Trim();
                    errorText = System.Text.RegularExpressions.Regex.Replace(errorText, @"\r\n+", "");
                    Description = string.Format(Messages.AVAILABLE_UPDATES_ERROR, errorText);
                }
                else
                {
                    Description = Messages.AVAILABLE_UPDATES_INTERNAL_ERROR;
                }

                //if we had originally wanted it to be hidden, make it visible now so the error is shown
                if (SuppressHistory)
                    SuppressHistory = false;

                throw;
            }
        }

        private void GetXenCenterVersions(XmlDocument xdoc)
        {
            if (!_checkForXenCenter)
                return;

            foreach (XmlNode versions in xdoc.GetElementsByTagName(ClientVersionsNode))
            {
                foreach (XmlNode version in versions.ChildNodes)
                {
                    string versionLang = string.Empty;
                    string name = string.Empty;
                    bool latest = false;
                    bool latestCr = false;
                    string url = string.Empty;
                    string sourceUrl = string.Empty;
                    string timestamp = string.Empty;
                    string checksum = string.Empty;

                    foreach (XmlAttribute attrib in version.Attributes)
                    {
                        if (attrib.Name == "value")
                            versionLang = attrib.Value;
                        else if (attrib.Name == "name")
                            name = attrib.Value;
                        else if (attrib.Name == "latest")
                            latest = attrib.Value.ToUpperInvariant() == bool.TrueString.ToUpperInvariant();
                        else if (attrib.Name == "latestcr")
                            latestCr = attrib.Value.ToUpperInvariant() == bool.TrueString.ToUpperInvariant();
                        else if (attrib.Name == "url")
                            url = attrib.Value;
                        else if (attrib.Name == "sourceUrl")
                            sourceUrl = attrib.Value;
                        else if (attrib.Name == "timestamp")
                            timestamp = attrib.Value;
                        else if (attrib.Name == "checksum")
                            checksum = attrib.Value;
                    }

                    ClientVersions.Add(new ClientVersion(versionLang, name, latest, latestCr, url, timestamp, checksum, sourceUrl));
                }
            }
        }
    }


    public class DownloadCfuAction : DownloadUpdatesXmlAction
    {
        private const string XenServerVersionsNode = "serverversions";
        private const string PatchesNode = "patches";
        private const string ConflictingPatchesNode = "conflictingpatches";
        private const string RequiredPatchesNode = "requiredpatches";
        private const string ConflictingPatchNode = "conflictingpatch";
        private const string RequiredPatchNode = "requiredpatch";

        private readonly bool _checkForServerVersion;
        private readonly bool _checkForPatches;

        public DownloadCfuAction(bool checkForServerVersion, bool checkForPatches, string userAgent, string xmlLocationUrl, bool suppressHistory)
            : base(userAgent, xmlLocationUrl, suppressHistory)
        {
            _checkForServerVersion = checkForServerVersion;
            _checkForPatches = checkForPatches;
            Title = Description = string.Format(Messages.AVAILABLE_UPDATES_CHECKING, BrandManager.ProductBrand);
        }

        public List<XenServerVersion> XenServerVersions { get; } = new List<XenServerVersion>();
        public List<XenServerPatch> XenServerPatches { get; } = new List<XenServerPatch>();

        public List<XenServerVersion> XenServerVersionsForAutoCheck =>
            _checkForServerVersion ? XenServerVersions : new List<XenServerVersion>();

        protected override void Run()
        {
            try
            {
                XmlDocument xdoc = FetchCheckForUpdatesXml();

                Description = Messages.COMPLETED;
            }
            catch (Exception e)
            {
                if (e is System.Net.Sockets.SocketException)
                {
                    Description = Messages.AVAILABLE_UPDATES_NETWORK_ERROR;
                }
                else if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    string errorText = e.Message.Trim();
                    errorText = System.Text.RegularExpressions.Regex.Replace(errorText, @"\r\n+", "");
                    Description = string.Format(Messages.AVAILABLE_UPDATES_ERROR, errorText);
                }
                else
                {
                    Description = Messages.AVAILABLE_UPDATES_INTERNAL_ERROR;
                }

                //if we had originally wanted it to be hidden, make it visible now so the error is shown
                if (SuppressHistory)
                    SuppressHistory = false;

                throw;
            }
        }
    }


    public abstract class DownloadUpdatesXmlAction : AsyncAction
    {
        private readonly string _userAgent;
        private readonly string _checkForUpdatesUrl;

        protected DownloadUpdatesXmlAction(string userAgent, string xmlLocationUrl, bool suppressHistory)
            : base(null, string.Empty, string.Empty, suppressHistory)
        {
            Debug.Assert(!string.IsNullOrWhiteSpace(userAgent));
            _userAgent = userAgent;
            _checkForUpdatesUrl = xmlLocationUrl;
        }

        protected XmlDocument FetchCheckForUpdatesXml()
        {
            var checkForUpdatesXml = new XmlDocument();
            var uriBuilder = new UriBuilder(_checkForUpdatesUrl);

            var uri = uriBuilder.Uri;
            if (uri.IsFile)
            {
                checkForUpdatesXml.Load(_checkForUpdatesUrl);
            }
            else
            {
                var authToken = XenAdminConfigManager.Provider.GetClientUpdatesQueryParam();
                uriBuilder.Query = Helpers.AddAuthTokenToQueryString(authToken, uriBuilder.Query);

                var proxy = XenAdminConfigManager.Provider.GetProxyFromSettings(Connection, false);
                var bytes = HttpFileDownloader.DownloadBytes(uriBuilder.Uri, proxy, _userAgent, noCache: true);
                using (var stream = new MemoryStream(bytes))
                    checkForUpdatesXml.Load(stream);
            }

            return checkForUpdatesXml;
        }
    }
}
