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

using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

using XenAdmin.Core;
using XenAdmin.Dialogs.Network;


namespace XenAdmin.Network
{
    internal class SSL
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(SSL));

        private static readonly object CertificateValidationLock = new object();

        /// <summary>
        /// Certificate policy for XCP-ng / XenServer connections.
        /// Fresh XCP-ng installs use self-signed certificates, so we do NOT require a public CA.
        /// Instead we use trust-on-first-use (TOFU): pin the cert hash per hostname after the
        /// user accepts (or after silent accept when warnings are disabled), then require that
        /// same pin on later connections. Never blindly accept an arbitrary cert with no pin.
        /// </summary>
        internal static bool ValidateServerCertificate(
              object sender,
              X509Certificate certificate,
              X509Chain chain,
              SslPolicyErrors sslPolicyErrors)
        {
            if (certificate == null)
                return false;
            lock (CertificateValidationLock)
            {
                bool AcceptCertificate = false;
                string hostname = string.Empty;
                if (sender is HttpWebRequest webreq)
                    hostname = webreq.Address?.Host ?? string.Empty;
                else if (sender is string host)
                    hostname = host;

                if (string.IsNullOrEmpty(hostname))
                    return false;

                foreach (KeyValuePair<string, string> kvp in Settings.KnownServers)
                {
                    if (!string.Equals(kvp.Key, hostname, System.StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.Equals(kvp.Value, certificate.GetCertHashString(), System.StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    else if (!Properties.Settings.Default.WarnChangedCertificate && Registry.SSLCertificateTypes == SSLCertificateTypes.None)
                    {
                        Settings.ReplaceCertificate(kvp.Key, certificate.GetCertHashString());
                        log.Debug("Updating cert silently");
                        return true;
                    }
                    else
                    {
                        if (Program.MainWindow == null)
                            return false;
                        Program.Invoke(Program.MainWindow, () =>
                        {
                            using (var dialog = new CertificateChangedDialog(certificate, hostname))
                                AcceptCertificate = dialog.ShowDialog(Program.MainWindow) == DialogResult.OK;
                        });

                        if (AcceptCertificate)
                            log.Debug("Updating cert after confirmation");
                        else
                            log.Debug("User rejected changed cert");
                        return AcceptCertificate;
                    }
                }

                // OS trust applies only when this hostname has no existing pin.
                if (sslPolicyErrors == SslPolicyErrors.None)
                    return true;

                // First sight of this host (typical for self-signed XCP-ng): pin after
                // optional warning. Default settings silently pin; Security options can require a prompt.
                if (!Properties.Settings.Default.WarnUnrecognizedCertificate && Registry.SSLCertificateTypes != SSLCertificateTypes.All)
                {
                    Settings.AddCertificate(certificate.GetCertHashString(), hostname);
                    log.Debug("Adding new cert silently (TOFU pin for unrecognized/self-signed certificate)");
                    return true;
                }

                if (Program.MainWindow == null)
                    return false;

                Program.Invoke(Program.MainWindow, () =>
                {
                    using (var dialog = new UnknownCertificateDialog(certificate, hostname))
                        AcceptCertificate = dialog.ShowDialog(Program.MainWindow) == DialogResult.OK;
                });

                if (AcceptCertificate)
                    log.Debug("Adding cert after confirmation");
                else
                    log.Debug("User rejected new cert");
                return AcceptCertificate;
            }
        }

    }

    public static class X509Ext
    {
        public static bool VerifyInAllStores(this X509Certificate2 certificate2)
        {
            try
            {
                X509Chain chain = new X509Chain(true);
                if (chain.Build(certificate2))
                    return true;
                return certificate2.Verify();
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }
}
