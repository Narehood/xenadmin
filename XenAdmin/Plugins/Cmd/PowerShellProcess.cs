using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace XenAdmin.Plugins
{
    /// <summary>
    /// Owns a temporary plugin payload and launches a short PowerShell bootstrap.
    /// The child deletes the payload before running it; disposal also cleans up
    /// when launch fails or the child exits before reading the file.
    /// </summary>
    internal sealed class PowerShellProcess : Process
    {
        private readonly string payloadPath;
        private bool ownsPayload;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(typeof(PowerShellProcess));

        /// <summary>Stages UTF-8 command source without placing target data on the process command line.</summary>
        /// <param name="command">Trusted invocation source containing already serialized literal values.</param>
        /// <param name="window">Whether the plugin should have a visible window.</param>
        public PowerShellProcess(string command, bool window)
        {
            payloadPath = Path.Combine(Path.GetTempPath(), "XcpNgCenter-plugin-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    ownsPayload = true;
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(command);
                }
                string path = XenServerPowershellCmd.Literal(payloadPath);
                string bootstrap = $"$__xc_source=[IO.File]::ReadAllText({path},[Text.Encoding]::UTF8); [IO.File]::Delete({path}); & ([ScriptBlock]::Create($__xc_source));";
                StartInfo.FileName = PluginDescriptor.PowerShellExecutable;
                StartInfo.Arguments = "-NoLogo -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
                StartInfo.UseShellExecute = false;
                StartInfo.CreateNoWindow = !window;
                StartInfo.WindowStyle = window ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden;
            }
            catch { DeletePayload(); throw; }
        }

        /// <summary>Releases the process handle and any payload not already removed by the child.</summary>
        /// <param name="disposing">Whether managed resources are being released explicitly.</param>
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) DeletePayload(); }
        }

        /// <summary>Deletes only the file this instance successfully created; cleanup errors are logged.</summary>
        private void DeletePayload()
        {
            if (!ownsPayload) return;
            try { File.Delete(payloadPath); ownsPayload = false; }
            catch (IOException error) { log.Warn("Could not remove plugin payload.", error); }
            catch (UnauthorizedAccessException error) { log.Warn("Could not remove plugin payload.", error); }
        }
    }
}
