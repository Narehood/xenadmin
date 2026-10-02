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
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Drawing;
using System.ComponentModel;
using System.Net;
using System.Threading;
using DotNetVnc;
using log4net;
using XenAdmin.Controls.ConsoleTab;
using XenAPI;
using XenAdmin.Core;
using System.IO;
using XenAdmin.Dialogs;
using System.Security.Cryptography;
using XenAdmin.Network;
using XenCenterLib;
using Console = XenAPI.Console;
using Message = System.Windows.Forms.Message;
using Timer = System.Threading.Timer;

namespace XenAdmin.ConsoleView
{
    public class XSVNCScreen : UserControl
    {
        private const int SHORT_RETRY_COUNT = 10;
        private const int SHORT_RETRY_SLEEP_TIME = 100;
        private const int RETRY_SLEEP_TIME = 5000;
        private const int RDP_POLL_INTERVAL = 30000;
        public const int RDPPort = 3389;
        private const int VNC_PORT = 5900;
        private const int CONSOLE_SIZE_OFFSET = 6;

        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

        private int _connectionRetries;

        private volatile bool _useVNC = true;

        private readonly bool _autoCaptureKeyboardAndMouse = true;

        private readonly Color _focusColor = SystemColors.MenuHighlight;

        /// <summary>
        /// May only be written on the event thread.  May be read off the event thread, to check whether
        /// the VNC source has been switched during connection.
        /// </summary>
        private volatile VNCGraphicsClient _vncClient;

        private RdpClient _rdpClient;

        private Timer _connectionPoller;

        private VM _sourceVm;
        private bool _sourceIsPv;

        private readonly object _hostedConsolesLock = new object();
        private List<XenRef<Console>> _hostedConsoles;

        private readonly CancellationTokenSource _portPollingCancellation = new CancellationTokenSource();
        private readonly CancellationToken _portPollingToken;
        private volatile bool _portsStopped;

        /// <summary>
        /// Xvnc will block us if we're too quick with the disconnect and reconnect that we do
        /// when polling for the VNC port.  To get around this, we keep the connection open between poll
        /// and proper connection.  pendingVNCConnection must be accessed only under the
        /// pendingVNCConnectionLock.  Work under this lock must be non-blocking, because it's used on
        /// Dispose.
        /// </summary>
        private Stream _pendingVNCConnection;

        private readonly object _pendingVNCConnectionLock = new object();

        internal EventHandler ResizeHandler;

        public event EventHandler UserCancelledAuth;
        public event EventHandler VncConnectionAttemptCancelled;
        public event Action<bool> GpuStatusChanged;
        public event Action<string> ConnectionNameChanged;

        public bool RdpVersionWarningNeeded => _rdpClient != null && _rdpClient.needsRdpVersionWarning;

        internal readonly VNCTabView ParentVNCTabView;

        [DefaultValue(false)] public bool UserWantsToSwitchProtocol { get; set; }

        private bool HasRDP => Source != null && Source.HasRDP();

        /// <summary>
        /// Whether we have tried to login without providing a password (covers the case where the user
        /// has configured VNC not to require a login password). If no password is saved, passwordless
        /// login is tried once.
        /// </summary>
        private bool _haveTriedLoginWithoutPassword;

        private bool _ignoreNextError;

        private Dictionary<string, string> _cachedNetworks;

        /// <summary>
        /// The last known VNC password for this VM.
        /// </summary>
        private char[] _vncPassword;

        internal ConsoleKeyHandler KeyHandler;

        internal string ElevatedUsername;
        internal string ElevatedPassword;

        internal XSVNCScreen(VM source, EventHandler resizeHandler, VNCTabView parent, string elevatedUsername,
            string elevatedPassword)
        {
            _portPollingToken = _portPollingCancellation.Token;
            ResizeHandler = resizeHandler;
            ParentVNCTabView = parent;
            Source = source;
            KeyHandler = ParentVNCTabView.KeyHandler;
            ElevatedUsername = elevatedUsername;
            ElevatedPassword = elevatedPassword;

#pragma warning disable 0219
            var _ = Handle;
#pragma warning restore 0219

            InitSubControl();

            //We're going to try and catch when the IP address changes for the VM, and re-scan for ports.
            if (source == null)
                return;
            Properties.Settings.Default.PropertyChanged += Default_PropertyChanged;
            var guestMetrics = Source.Connection.Resolve(Source.guest_metrics);
            if (guestMetrics == null)
                return;

            _cachedNetworks = guestMetrics.networks;

            guestMetrics.PropertyChanged += guestMetrics_PropertyChanged;
        }

        private void Default_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "EnableRDPPolling")
                Program.Invoke(this, StartPolling);
        }

        private void UnregisterEventListeners()
        {
            if (Source == null)
                return;

            Source.PropertyChanged -= VM_PropertyChanged;

            var guestMetrics = Source.Connection.Resolve(Source.guest_metrics);
            if (guestMetrics == null)
                return;

            guestMetrics.PropertyChanged -= guestMetrics_PropertyChanged;
        }

        private void guestMetrics_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (Source == null)
                return;

            if (e.PropertyName == "networks")
            {
                var newNetworks = (sender as VM_guest_metrics).networks;
                if (!EquateDictionary(newNetworks, _cachedNetworks))
                {
                    Log.InfoFormat("Detected IP address change in vm {0}, repolling for VNC/RDP...", Source.Name());

                    _cachedNetworks = newNetworks;

                    Program.Invoke(this, StartPolling);
                }
            }
        }

        private static bool EquateDictionary<T, TS>(Dictionary<T, TS> d1, Dictionary<T, TS> d2)
            where TS : IEquatable<TS>
        {
            if (d1.Count != d2.Count)
                return false;

            foreach (var key in d1.Keys)
            {
                if (!d2.ContainsKey(key) || !d2[key].Equals(d1[key]))
                    return false;
            }

            return true;
        }

        private bool _wasPaused = true;

        public void Pause()
        {
            if (RemoteConsole != null)
            {
                _wasPaused = true;
                RemoteConsole.Pause();
            }
        }

        public void Unpause()
        {
            if (RemoteConsole != null)
            {
                _wasPaused = false;
                RemoteConsole.UnPause();
            }
        }

        public Size DesktopSize => RemoteConsole?.DesktopSize ?? Size.Empty;

        /// <summary>
        /// Nothrow guarantee.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            UnregisterEventListeners();

            if (disposing)
            {
                _portsStopped = true;
                if (!_portPollingToken.IsCancellationRequested) _portPollingCancellation.Cancel();
                _portPollingCancellation.Dispose();
                if (_connectionPoller != null)
                {
                    _connectionPoller.Change(Timeout.Infinite, Timeout.Infinite);
                    _connectionPoller.Dispose();
                    _connectionPoller = null;
                }

                if (RemoteConsole != null)
                {
                    RemoteConsole.DisconnectAndDispose();
                    RemoteConsole = null;
                }

                Log.DebugFormat("Set Pending Vnc connection to null");
                SetPendingVNCConnection(null);
            }

            base.Dispose(disposing);
        }

        public MethodInvoker OnDetectRDP = null;
        public MethodInvoker OnDetectVNC = null;
        public string RdpIp;
        public string VncIp;

        private void PollRDPPort(object sender)
        {
            if (HasRDP)
            {
                if (OnDetectRDP != null)
                    Program.Invoke(this, OnDetectRDP);
            }
            else
            {
                RdpIp = null;
                var openIp = PollPort(RDPPort, false);

                if (openIp == null)
                    return;
                RdpIp = openIp;
                if (OnDetectRDP != null)
                    Program.Invoke(this, OnDetectRDP);
            }
        }

        private void PollVNCPort(object sender)
        {
            VncIp = null;
            var openIp = PollPort(VNC_PORT, true);

            if (openIp == null)
                return;
            VncIp = openIp;

            _connectionPoller?.Change(Timeout.Infinite, Timeout.Infinite);

            if (OnDetectVNC != null)
                Program.Invoke(this, OnDetectVNC);
        }

        /// <summary>
        /// scan each ip address (from the guest agent) for an open port
        /// </summary>
        public string PollPort(int port, bool vnc)
        {
            try
            {
                if (Source == null || _portsStopped)
                    return null;

                var vm = Source;

                var guestMetricsRef = vm.guest_metrics;
                if (guestMetricsRef == null || Helper.IsNullOrEmptyOpaqueRef(guestMetricsRef.opaque_ref))
                    return null;

                var metrics = vm.Connection.Resolve(guestMetricsRef);
                var networks = metrics?.networks;

                if (networks == null)
                    return null;

                var ipAddresses = new List<string>();
                var ipv6Addresses = new List<string>();
                var ipAddressesForNetworksWithoutPifs = new List<string>();
                var ipv6AddressesForNetworksWithoutPifs = new List<string>();

                foreach (var vif in vm.Connection.ResolveAll(vm.VIFs))
                {
                    var network = vif.Connection.Resolve(vif.network);
                    var host = vm.Connection.Resolve(vm.resident_on);
                    var pif = Helpers.FindPIF(network, host);
                    foreach (var networkInfo in networks.Where(n => n.Key.StartsWith($"{vif.device}/ip")))
                    {
                        if (networkInfo.Key.EndsWith("ip") || networkInfo.Key.Contains("ipv4")) // IPv4 address
                        {
                            if (pif == null)
                                ipAddressesForNetworksWithoutPifs.Add(networkInfo.Value);
                            else if (pif.LinkStatus() == PIF.LinkState.Connected)
                                ipAddresses.Add(networkInfo.Value);
                        }
                        else
                        {
                            if (networkInfo.Key.Contains("ipv6")) // IPv6 address, enclose in square brackets
                            {
                                if (pif == null)
                                    ipv6AddressesForNetworksWithoutPifs.Add($"[{networkInfo.Value}]");
                                else if (pif.LinkStatus() == PIF.LinkState.Connected)
                                    ipv6Addresses.Add($"[{networkInfo.Value}]");
                            }
                        }
                    }
                }

                ipAddresses = ipAddresses.Distinct().ToList();

                ipAddresses.AddRange(ipv6Addresses); // make sure IPv4 addresses are scanned first (CA-102755)
                // add IP addresses for networks without PIFs
                ipAddresses.AddRange(ipAddressesForNetworksWithoutPifs);
                ipAddresses.AddRange(ipv6AddressesForNetworksWithoutPifs);


                foreach (var ipAddress in ipAddresses)
                {
                    try
                    {
                        Log.DebugFormat("Poll port {0}:{1}", ipAddress, port);
                        var s = ConnectGuest(ipAddress, port, vm.Connection, _portPollingToken);
                        if (vnc)
                        {
                            Log.DebugFormat("Connected. Set Pending Vnc connection {0}:{1}", ipAddress, port);
                            SetPendingVNCConnection(s);
                        }
                        else
                        {
                            s.Close();
                        }

                        return ipAddress;
                    }
                    catch (OperationCanceledException) when (_portPollingToken.IsCancellationRequested) { return null; }
                    catch (Exception exn)
                    {
                        Log.Debug(exn);
                    }
                }
            }
            catch (WebException)
            {
                // xapi has gone away.
            }
            catch (IOException)
            {
                // xapi has gone away.
            }
            catch (Failure exn)
            {
                switch (exn.ErrorDescription[0])
                {
                    case Failure.HANDLE_INVALID:
                        // HANDLE_INVALID is fine -- the guest metrics are not there yet.
                        break;
                    case Failure.SESSION_INVALID:
                    {
                        // Guest address discovery reads the connection's cache.
                        // A console-owned session is cleaned up by its client.
                        break;
                    }
                    default:
                        Log.Warn("Exception while polling VM for port " + port + ".", exn);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Warn("Exception while polling VM for port " + port + ".", e);
            }

            return null;
        }

        /// <summary>
        /// Exchange the current value of pendingVNCConnection with the given one, and close the old
        /// connection, if any.
        /// Nothrow guarantee.
        /// </summary>
        /// <param name="s">May be null</param>
        private void SetPendingVNCConnection(Stream s)
        {
            Stream oldPending;
            lock (_pendingVNCConnectionLock)
            {
                oldPending = _pendingVNCConnection;
                _pendingVNCConnection = _portsStopped ? null : s;
            }
            if (_portsStopped) s?.Dispose();

            if (oldPending != null)
            {
                try
                {
                    oldPending.Close();
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        private bool _scaling;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Scaling
        {
            get
            {
                Program.AssertOnEventThread();
                return _scaling;
            }

            set
            {
                Program.AssertOnEventThread();
                _scaling = value;
                if (RemoteConsole != null)
                    RemoteConsole.Scaling = value;
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public IRemoteConsole RemoteConsole
        {
            get => _vncClient != null ? (IRemoteConsole)_vncClient : _rdpClient;
            set
            {
                if (_vncClient != null)
                    _vncClient = (VNCGraphicsClient)value;
                else if (_rdpClient != null)
                    _rdpClient = (RdpClient)value;
            }
        }

        /// <summary>
        /// Creates the actual VNC or RDP client control.
        /// </summary>
        private void InitSubControl()
        {
            Program.AssertOnEventThread();

            //When switch to RDP from VNC, if RDP IP is empty, do not try to switch.
            if (string.IsNullOrEmpty(RdpIp) && !UseVNC && RemoteConsole != null)
                return;

            var wasFocused = false;
            Controls.Clear();
            //console size with some offset to accomodate focus rectangle
            var currentConsoleSize = new Size(Size.Width - CONSOLE_SIZE_OFFSET, Size.Height - CONSOLE_SIZE_OFFSET);

            if (RemoteConsole != null)
            {
                var preventResetConsole = false;
                wasFocused = RemoteConsole.ConsoleControl != null && RemoteConsole.ConsoleControl.Focused;
                if (RemoteConsole is RdpClient client && client.IsAttemptingConnection)
                {
                    preventResetConsole = true;
                }

                if (!preventResetConsole)
                {
                    RemoteConsole.DisconnectAndDispose();
                    RemoteConsole = null;
                }

                _vncPassword = null;
            }

            // Reset
            _haveTriedLoginWithoutPassword = false;

            if (UseVNC || string.IsNullOrEmpty(RdpIp))
            {
                AutoScroll = false;
                AutoScrollMinSize = new Size(0, 0);

                _vncClient = new VNCGraphicsClient(this);

                _vncClient.UseSource = UseSource;
                _vncClient.DesktopResized += ResizeHandler;
                _vncClient.Resize += ResizeHandler;
                _vncClient.ErrorOccurred += ErrorHandler;
                _vncClient.ConnectionSuccess += ConnectionSuccess;
                _vncClient.Dock = DockStyle.Fill;
            }
            else
            {
                if (_rdpClient == null)
                {
                    if (ParentForm is FullScreenForm form)
                        currentConsoleSize = form.GetContentSize();
                    AutoScroll = true;
                    AutoScrollMinSize = _oldSize;
                    _rdpClient = new RdpClient(this, currentConsoleSize, ResizeHandler);

                    _rdpClient.OnDisconnected += ParentVNCTabView.RdpDisconnectedHandler;
                }
            }

            if (RemoteConsole?.ConsoleControl != null)
            {
                RemoteConsole.KeyHandler = KeyHandler;
                RemoteConsole.SendScanCodes = !_sourceIsPv;
                RemoteConsole.Scaling = Scaling;
                RemoteConsole.DisplayBorder = _displayFocusRectangle;
                SetKeyboardAndMouseCapture(_autoCaptureKeyboardAndMouse);
                if (_wasPaused)
                    RemoteConsole.Pause();
                else
                    RemoteConsole.UnPause();
                ConnectToRemoteConsole();

                if (wasFocused)
                    RemoteConsole.Activate();
            }

            GpuStatusChanged?.Invoke(MustConnectRemoteDesktop());
        }

        internal bool MustConnectRemoteDesktop()
        {
            return (UseVNC || string.IsNullOrEmpty(RdpIp)) &&
                   Source.HasGPUPassthrough() && Source.power_state == vm_power_state.Running;
        }
        
        private void SetKeyboardAndMouseCapture(bool value)
        {
            if (RemoteConsole?.ConsoleControl != null)
                RemoteConsole.ConsoleControl.TabStop = value;
        }

        private void ConnectToRemoteConsole()
        {
            if (_vncClient != null)
                ThreadPool.QueueUserWorkItem(Connect, new KeyValuePair<VNCGraphicsClient, Exception>(_vncClient, null));
            else if (_rdpClient != null)
                _rdpClient.Connect(RdpIp);
        }

        private void ConnectionSuccess(object sender, EventArgs e)
        {
            _connectionRetries = 0;
            _errorMessage = null;
            Invalidate();
            if (AutoSwitchRDPLater)
            {
                if (OnDetectRDP != null)
                    Program.Invoke(this, OnDetectRDP);
                AutoSwitchRDPLater = false;
            }

            if (ParentVNCTabView.IsRDPControlEnabled())
                ParentVNCTabView.EnableToggleVNCButton();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal bool AutoSwitchRDPLater { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal bool UseVNC
        {
            get => _useVNC;
            set
            {
                if (value != _useVNC)
                {
                    _connectionRetries = 0;
                    _useVNC = value;
                    _scaling = false;
                    InitSubControl();
                    // Check if we have really switched. If not, change useVNC back (CA-102755)
                    var switched = true;
                    if (_useVNC) // we wanted VNC
                    {
                        if (_vncClient == null && _rdpClient != null) // it is actually RDP
                            switched = false;
                    }
                    else // we wanted RDP
                    {
                        if (_rdpClient == null && _vncClient != null) // it is actually VNC
                            switched = false;
                    }

                    if (!switched)
                    {
                        _useVNC = !_useVNC;
                    }
                }
            }
        }

        private volatile bool _useSource = true;

        /// <summary>
        /// Indicates whether to use the source or the detected vncIP
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool UseSource
        {
            get => _useSource;
            set
            {
                if (value != _useSource)
                {
                    _useSource = value;
                    _connectionRetries = 0;
                    InitSubControl();
                }
            }
        }

        private VM Source
        {
            get => _sourceVm;
            set
            {
                if (_connectionPoller != null)
                {
                    _connectionPoller.Change(Timeout.Infinite, Timeout.Infinite);
                    _connectionPoller = null;
                }

                if (_sourceVm != null)
                {
                    _sourceVm.PropertyChanged -= VM_PropertyChanged;
                    _sourceVm = null;
                }

                _sourceVm = value;

                if (value != null)
                {
                    value.PropertyChanged += VM_PropertyChanged;

                    _sourceIsPv = !value.IsHVM();

                    StartPolling();

                    lock (_hostedConsolesLock)
                    {
                        _hostedConsoles = Source.consoles;
                    }

                    VM_PropertyChanged(value, new PropertyChangedEventArgs("consoles"));
                }
            }
        }

        public string ConnectionName
        {
            get
            {
                if (Source == null)
                    return null;

                if (Source.IsControlDomainZero(out var host))
                    return string.Format(Messages.CONSOLE_HOST, host.Name());

                if (Source.IsSrDriverDomain(out var sr))
                    return string.Format(Messages.CONSOLE_SR_DRIVER_DOMAIN, sr.Name());

                return Source.Name();
            }
        }

        private bool InDefaultConsole()
        {
            // Windows VMs: 
            // - UseVNC indicates whether to use the default desktop (true) or the remote desktop (false); 
            // - UseSource is true by default and not used
            // Linux VMs: 
            // - UseVNC is true by default and not used; 
            // - UseSource indicates whether to use the text console (true) or the graphical vnc console (false); 

            return UseVNC && UseSource;
        }

        private void StartPolling()
        {
            //Disable the button first, but only if in text/default console (to allow user to return to the text console - ref. CA-70314)
            if (InDefaultConsole())
            {
                ParentVNCTabView.DisableToggleVNCButton();
            }

            if (ParentVNCTabView.IsRDPControlEnabled())
                return;

            if (InDefaultConsole())
            {
                ParentVNCTabView.DisableToggleVNCButton();
            }

            if (Source == null || Source.IsControlDomainZero(out _))
                return;

            _connectionPoller?.Dispose();

            //Start the polling again
            _connectionPoller = !Source.IsHVM()
                ? new Timer(PollVNCPort, null, RETRY_SLEEP_TIME, RDP_POLL_INTERVAL)
                : new Timer(PollRDPPort, null, RETRY_SLEEP_TIME, RDP_POLL_INTERVAL);
        }

        private void VM_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var vm = (VM)sender;

            if (vm.uuid != Source.uuid)
                return;

            //We only want to reconnect the consoles when they change 
            //if the vm is running and if we are using vncTerm / qemu
            if (e.PropertyName == "consoles" && vm.power_state == vm_power_state.Running && UseSource)
            {
                ConnectNewHostedConsole();
            }
            //If the consoles change under us then refresh hostedConsoles
            else if (e.PropertyName == "consoles" && vm.power_state == vm_power_state.Running && !UseSource)
            {
                lock (_hostedConsolesLock)
                {
                    _hostedConsoles = Source.consoles;
                }
            }
            //Or if the VM legitimately turns on
            else if (e.PropertyName == "power_state" && vm.power_state == vm_power_state.Running)
            {
                ParentVNCTabView.VMPowerOn();
                ConnectNewHostedConsole();
                _connectionPoller?.Change(RETRY_SLEEP_TIME, RDP_POLL_INTERVAL);
            }
            else if (e.PropertyName == "power_state" &&
                     (vm.power_state == vm_power_state.Halted || vm.power_state == vm_power_state.Suspended))
            {
                ParentVNCTabView.VMPowerOff();
                _connectionPoller?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else if (e.PropertyName == "domid")
            {
                // Reboot / start / shutdown
                Program.Invoke(this, StartPolling);
            }

            if (e.PropertyName == "power_state" || e.PropertyName == "VGPUs")
            {
                Program.Invoke(this, () => { GpuStatusChanged?.Invoke(MustConnectRemoteDesktop()); });
            }

            if (e.PropertyName == "name_label" && ConnectionNameChanged != null)
                ConnectionNameChanged(ConnectionName);
        }

        internal void ImmediatelyPollForConsole()
        {
            _connectionPoller?.Change(0, RDP_POLL_INTERVAL);
        }

        private void ConnectNewHostedConsole()
        {
            Program.AssertOnEventThread();

            lock (_hostedConsolesLock)
            {
                _hostedConsoles = Source.consoles;
            }

            if (UseVNC && _vncClient != null && ConnectionSuperseded())
            {
                InitSubControl();
            }
        }

        /// <summary>
        /// A connection is superseded if it's connected to a console that's no longer being
        /// advertised by the server and there's a replacement that _is_ being advertised, or
        /// if its not connected at all.
        /// 
        /// The server will leave the console open until we close, so that the user may see any crash messages.
        /// For this reason, we need to close down ourselves when we see that the console has
        /// been replaced by a newer one (i.e. after a reboot).
        /// </summary>
        private bool ConnectionSuperseded()
        {
            return !_vncClient.Connected || ConsoleSuperseded((Console)_vncClient.Console);
        }

        private bool ConsoleSuperseded(Console oldConsole)
        {
            if (oldConsole == null)
                return true;

            List<Console> consoles;
            lock (_hostedConsolesLock)
            {
                consoles = Source.Connection.ResolveAll(_hostedConsoles);
            }

            var goodConsole = false;
            foreach (var console in consoles)
            {
                if (console.opaque_ref == oldConsole.opaque_ref &&
                    console.location == oldConsole.location)
                    return false;
                else if (console.protocol == console_protocol.rfb)
                    goodConsole = true;
            }

            return goodConsole;
        }

        /// <summary>
        /// CA-11201: GUI logs are being massively spammed. Prevent "INTERNAL_ERROR Host has disappeared"
        /// appearing more than once.
        /// </summary>
        private bool _suppressHostGoneMessage;

        private void Connect(object o)
        {
            if (Program.RunInAutomatedTestMode)
                return;

            Program.AssertOffEventThread();

            var kvp = (KeyValuePair<VNCGraphicsClient, Exception>)o;

            var v = kvp.Key;
            var error = kvp.Value;
            if (v.Terminated || v.ConnectionToken.IsCancellationRequested) return;

            try
            {
                if (UseSource)
                {
                    List<Console> consoles;
                    lock (_hostedConsolesLock)
                    {
                        consoles = _sourceVm.Connection.ResolveAll(_hostedConsoles);
                    }

                    foreach (var console in consoles)
                    {
                        if (_vncClient != v)
                        {
                            // We've been replaced.  Give up.
                            return;
                        }

                        if (console.protocol == console_protocol.rfb)
                        {
                            try
                            {
                                ConnectHostedConsole(v, console);
                                return;
                            }
                            catch (Exception exn)
                            {
                                var failure = exn as Failure;
                                var isHostGoneMessage = failure != null
                                                        && failure.ErrorDescription.Count == 2
                                                        && failure.ErrorDescription[0] == Failure.INTERNAL_ERROR
                                                        && failure.ErrorDescription[1] ==
                                                        string.Format(Messages.HOST_GONE, BrandManager.BrandConsole);

                                if (isHostGoneMessage)
                                {
                                    if (!_suppressHostGoneMessage)
                                    {
                                        Log.Debug(exn, exn);
                                        _suppressHostGoneMessage = true;
                                    }
                                }
                                else
                                {
                                    Log.Debug(exn, exn);
                                    _suppressHostGoneMessage = false;
                                }
                            }
                        }
                    }

                    Log.Debug("Did not find any hosted consoles");
                    _ = SleepAndRetryConnection(v);
                }
                else
                {
                    if (VncIp == null)
                    {
                        Log.DebugFormat("vncIP is null. Abort VNC connection attempt");
                        OnVncConnectionAttemptCancelled();
                        return;
                    }

                    _vncPassword = Settings.GetVNCPassword(_sourceVm.uuid);
                    if (_vncPassword == null)
                    {
                        var lifecycleOperationInProgress =
                            _sourceVm.current_operations.Values.Any(VM.is_lifecycle_operation);
                        if (_haveTriedLoginWithoutPassword && !lifecycleOperationInProgress)
                        {
                            Program.Invoke(this, delegate { PromptForPassword(_ignoreNextError ? null : error); });
                            _ignoreNextError = false;
                            if (_vncPassword == null)
                            {
                                Log.Debug("User cancelled VNC password prompt: aborting connection attempt");
                                OnUserCancelledAuth();
                                return;
                            }
                        }
                        else
                        {
                            Log.Debug("Attempting passwordless VNC login");
                            _vncPassword = Array.Empty<char>();
                            _ignoreNextError = true;
                            _haveTriedLoginWithoutPassword = true;
                        }
                    }

                    Stream s;
                    lock (_pendingVNCConnectionLock)
                    {
                        s = _pendingVNCConnection;
                        Log.DebugFormat("Using pending VNC connection");
                        _pendingVNCConnection = null;
                    }

                    var pending = s;
                    OpenConsoleTransport(v, startup => pending ?? HTTP.ConnectStream(
                        new Uri($"http://{VncIp}:{VNC_PORT}/"),
                        XenAdminConfigManager.Provider.GetProxyFromSettings(_sourceVm.Connection), true, 0, startup.Token), null,
                        pending);

                    // store the empty vnc password after a successful passwordless login
                    if (_haveTriedLoginWithoutPassword && _vncPassword.Length == 0)
                        Program.Invoke(this, () => Settings.SetVNCPassword(_sourceVm.uuid, _vncPassword));
                }
            }
            catch (OperationCanceledException) when (v.ConnectionToken.IsCancellationRequested) { }
            catch (Exception exn)
            {
                Log.Warn(exn, exn);
                _ = SleepAndRetryConnection(v);
            }
        }

        private void PromptForPassword(Exception error)
        {
            Program.AssertOnEventThread();

            // Prompt for password
            var f = new VNCPasswordDialog(error, _sourceVm);
            try
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    // Store password for next time
                    _vncPassword = f.Password;
                    Settings.SetVNCPassword(_sourceVm.uuid, _vncPassword);
                }
                else
                {
                    // User cancelled
                }
            }
            finally
            {
                f.Dispose();
            }
        }

        private void OnUserCancelledAuth()
        {
            Program.Invoke(this, delegate
            {
                Log.Debug("User cancelled during VNC authentication");
                UserCancelledAuth?.Invoke(this, null);
            });
        }

        private void OnVncConnectionAttemptCancelled()
        {
            Program.Invoke(this, delegate
            {
                Log.Debug("Cancelled VNC connection attempt");
                VncConnectionAttemptCancelled?.Invoke(this, null);
            });
        }

        private static Stream ConnectGuest(string ipAddress, int port, IXenConnection connection, CancellationToken token)
        {
            var uriString = $"http://{ipAddress}:{port}/";
            Log.DebugFormat("Trying to connect to: {0}", uriString);
            using var startup = new ConsoleStartupGuard(token, TimeSpan.FromMilliseconds(
                Math.Clamp(XenAdminConfigManager.Provider.ConnectionTimeout, 1000, 3600000)));
            var stream = HTTP.ConnectStream(new Uri(uriString),
                XenAdminConfigManager.Provider.GetProxyFromSettings(connection), true, 0, startup.Token);
            startup.AttachTransport(stream);
            if (startup.Complete()) return stream;
            stream.Dispose();
            token.ThrowIfCancellationRequested();
            throw new IOException(startup.FailureMessage);
        }

        private void ConnectHostedConsole(VNCGraphicsClient v, Console console)
        {
            Program.AssertOffEventThread();

            var host = console.Connection.Resolve(Source.resident_on);
            if (host == null)
            {
                throw new Failure(Failure.INTERNAL_ERROR, string.Format(Messages.HOST_GONE, BrandManager.BrandConsole));
            }

            OpenConsoleTransport(v, startup =>
            {
                // use the elevated credentials, if provided, for connecting to the console (CA-91132)
                var session = (string.IsNullOrEmpty(ElevatedUsername) || string.IsNullOrEmpty(ElevatedPassword))
                    ? console.Connection.DuplicateSession()
                    : console.Connection is XenConnection connection
                        ? connection.ElevatedSession(ElevatedUsername, ElevatedPassword, startup.Token)
                        : console.Connection.ElevatedSession(ElevatedUsername, ElevatedPassword);
                v.OwnConsoleSession(startup, session);
                startup.Token.ThrowIfCancellationRequested();
                return HTTP.HttpConnectStream(new Uri(console.location),
                    XenAdminConfigManager.Provider.GetProxyFromSettings(console.Connection), session.opaque_ref, 0, startup.Token);
            }, console);
        }

        private void OpenConsoleTransport(VNCGraphicsClient v, Func<ConsoleStartupGuard, Stream> open,
            Console console, Stream pending = null)
        {
            var startup = v.BeginConnectionAttempt(TimeSpan.FromMilliseconds(
                Math.Clamp(XenAdminConfigManager.Provider.ConnectionTimeout, 1000, 3600000)));
            if (startup == null)
            {
                pending?.Dispose();
                return;
            }
            Stream stream = null;
            var adopted = false;
            try
            {
                stream = open(startup);
                startup.AttachTransport(stream);
                startup.Token.ThrowIfCancellationRequested();
                adopted = InvokeConnection(v, stream, console, startup);
            }
            catch (Exception error) when (startup.Token.IsCancellationRequested)
            {
                if (v.ConnectionToken.IsCancellationRequested) throw new OperationCanceledException(v.ConnectionToken);
                throw new IOException(startup.FailureMessage, error);
            }
            finally
            {
                if (!adopted)
                {
                    stream?.Dispose();
                    v.EndConnectionAttempt(startup);
                }
            }
        }

        private bool InvokeConnection(VNCGraphicsClient v, Stream stream, Console console, ConsoleStartupGuard startup)
        {
            var adopted = false;
            Program.Invoke(this, delegate()
            {
                // This is the last chance that we have to make sure that we've not already
                // connected this VNCGraphicsClient.  Now that we are back on the event thread,
                // we're guaranteed that no-one will beat us to the v.connect() call.  We
                // hand over responsibility for closing the stream at that point, so we have to
                // close it ourselves if the client is already connected.
                if (_vncClient != v || v.Connected || v.Terminated || startup.Token.IsCancellationRequested)
                {
                    stream.Close();
                }
                else
                {
                    v.SendScanCodes = UseSource && !_sourceIsPv;
                    v.SourceVM = _sourceVm;
                    v.Console = console;
                    v.UseQemuExtKeyEncoding = _sourceVm != null && Helpers.InvernessOrGreater(_sourceVm.Connection);
                    v.Connect(stream, _vncPassword);
                    adopted = true;
                }
            });
            return adopted;
        }

        private void RetryConnection(VNCGraphicsClient v, Exception exn)
        {
            if (_vncClient == v && !v.Terminated && Source.power_state == vm_power_state.Running)
            {
                ThreadPool.QueueUserWorkItem(Connect, new KeyValuePair<VNCGraphicsClient, Exception>(v, exn));
            }
        }

        public void SendCAD()
        {
            Program.AssertOnEventThread();

            RemoteConsole?.SendCAD();
        }

        private string _errorMessage;

        private void ErrorHandler(object sender, Exception exn)
        {
            Program.AssertOffEventThread();

            if (Disposing || IsDisposed)
                return;

            Program.Invoke(this, delegate()
            {
                var v = (VNCGraphicsClient)sender;
                if (_vncClient != v || v.Terminated || v.Connected) return;

                if (exn is VNCAuthenticationException || exn is CryptographicException)
                {
                    Log.Debug(exn, exn);

                    // Clear the stored VNC password for this server.
                    Settings.SetVNCPassword(_sourceVm.uuid, null);
                    RetryConnection(v, exn);
                }
                else if (exn is IOException || exn is Failure)
                {
                    Log.Debug(exn, exn);
                    _errorMessage = exn.Message;
                    Invalidate();
                    SleepAndRetryConnection_(v);
                }
                else
                {
                    Log.Warn(exn, exn);
                    _errorMessage = exn.Message;
                }
            });
        }

        private void SleepAndRetryConnection_(IDisposable v)
        {
            _ = SleepAndRetryConnection(v);
        }

        private async System.Threading.Tasks.Task SleepAndRetryConnection(object o)
        {
            var v = (VNCGraphicsClient)o;
            var retries = Interlocked.Increment(ref _connectionRetries);
            try
            {
                await System.Threading.Tasks.Task.Delay(retries < SHORT_RETRY_COUNT ? SHORT_RETRY_SLEEP_TIME : RETRY_SLEEP_TIME,
                    v.ConnectionToken).ConfigureAwait(false);
                RetryConnection(v, null);
            }
            catch (OperationCanceledException) when (v.ConnectionToken.IsCancellationRequested) { }
            catch (Exception error) { Log.Debug("Console retry stopped", error); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_errorMessage != null)
            {
                var size = e.Graphics.MeasureString(_errorMessage, Font);
                e.Graphics.DrawString(_errorMessage, Font, Brushes.Black,
                    ((Width - size.Width) / 2), ((Height - size.Height) / 2));
            }

            // draw focus rectangle
            if (DisplayFocusRectangle && ContainsFocus && RemoteConsole != null)
            {
                var focusRect = Rectangle.Inflate(RemoteConsole.ConsoleBounds, VNCGraphicsClient.BORDER_PADDING / 2,
                    VNCGraphicsClient.BORDER_PADDING / 2);
                using (var pen = new Pen(_focusColor, VNCGraphicsClient.BORDER_WIDTH))
                {
                    if (Focused)
                        pen.DashStyle = DashStyle.Dash;
                    e.Graphics.DrawRectangle(pen, focusRect);
                }
            }
        }

        // Save this for when we init a new vncClient.
        private bool _displayFocusRectangle = true;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool DisplayFocusRectangle
        {
            get => _displayFocusRectangle;
            set
            {
                _displayFocusRectangle = value;
                if (RemoteConsole != null)
                {
                    RemoteConsole.DisplayBorder = _displayFocusRectangle;
                }
            }
        }

        internal Image Snapshot()
        {
            return RemoteConsole?.Snapshot();
        }

        internal void RefreshScreen()
        {
            Program.AssertOnEventThread();
            RemoteConsole?.ConsoleControl?.Refresh();
            Invalidate();
            Update();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Program.AssertOnEventThread();
            base.OnGotFocus(e);

            RefreshScreen();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Program.AssertOnEventThread();
            base.OnLostFocus(e);

            _pressedKeys = new Set<Keys>();

            // reset tab stop
            SetKeyboardAndMouseCapture(_autoCaptureKeyboardAndMouse);

            RefreshScreen();
        }

        protected override void OnEnter(EventArgs e)
        {
            Program.AssertOnEventThread();
            base.OnEnter(e);

            CaptureKeyboardAndMouse();
            RefreshScreen();
        }

        protected override void OnLeave(EventArgs e)
        {
            Program.AssertOnEventThread();
            base.OnLeave(e);

            // reset tab stop
            SetKeyboardAndMouseCapture(_autoCaptureKeyboardAndMouse);

            RefreshScreen();
        }

        internal void UncaptureKeyboardAndMouse()
        {
            if (_autoCaptureKeyboardAndMouse)
            {
                SetKeyboardAndMouseCapture(false);
            }

            ActiveControl = null;

            EnableMenuShortcuts();
        }

        internal void CaptureKeyboardAndMouse()
        {
            if (RemoteConsole != null)
            {
                RemoteConsole.Activate();
                if (_autoCaptureKeyboardAndMouse)
                {
                    SetKeyboardAndMouseCapture(true);
                }

                Unpause();
            }

            DisableMenuShortcuts();
        }

        public static void DisableMenuShortcuts()
        {
            Program.MainWindow.MenuShortcutsEnabled = false;
        }

        public static void EnableMenuShortcuts()
        {
            Program.MainWindow.MenuShortcutsEnabled = true;
        }

        private Set<Keys> _pressedKeys = new Set<Keys>();
        private bool _modifierKeyPressedAlone;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const int WM_KEYDOWN = 0x100;
            const int WM_SYSKEYDOWN = 0x104;

            var down = ((msg.Msg == WM_KEYDOWN) || (msg.Msg == WM_SYSKEYDOWN));

            var key = keyData;

            if ((key & Keys.Control) == Keys.Control)
                key = key & ~Keys.Control;

            if ((key & Keys.Alt) == Keys.Alt)
                key = key & ~Keys.Alt;

            if ((key & Keys.Shift) == Keys.Shift)
                key = key & ~Keys.Shift;

            // use TranslateKeyMessage to identify if Left or Right modifier keys have been pressed/released
            var extKey = ConsoleKeyHandler.TranslateKeyMessage(msg);

            return KeySym(down, key, extKey);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            e.Handled = KeySym(false, e.KeyCode, e.KeyCode);
        }

        private bool KeySym(bool pressed, Keys key, Keys extendedKey)
        {
            if (!pressed && _pressedKeys.Count == 0) // we received key-up, but not key-down - ignore
                return true;

            if (KeyHandler.handleExtras(pressed, _pressedKeys, KeyHandler.ExtraKeys, extendedKey,
                    KeyHandler.ModifierKeys, ref _modifierKeyPressedAlone))
            {
                Focus();
                return true;
            }

            // on keyup, try to remove extended keys (i.e. LControlKey, LControlKey, RShiftKey, LShiftKey, RMenu, LMenu)
            // we need to do this here, because we cannot otherwise distinguish between Left and Right modifier keys on KeyUp
            if (!pressed)
            {
                var extendedKeys = ConsoleKeyHandler.GetExtendedKeys(key);
                foreach (var k in extendedKeys)
                {
                    _pressedKeys.Remove(k);
                }
            }

            if (key == Keys.Tab || (key == (Keys.Tab | Keys.Shift)))
                return false;

            return false;
        }

        private Size _oldSize;

        public void UpdateRDPResolution(bool fullscreen = false)
        {
            if (_rdpClient == null || _oldSize.Equals(Size))
                return;

            //no offsets in fullscreen mode because there is no need to accomodate focus border 
            if (fullscreen)
                _rdpClient.UpdateDisplay(Size.Width, Size.Height, new Point(0, 0));
            else
                _rdpClient.UpdateDisplay(Size.Width - CONSOLE_SIZE_OFFSET, Size.Height - CONSOLE_SIZE_OFFSET,
                    new Point(3, 3));
            _oldSize = new Size(Size.Width, Size.Height);
            Refresh();
        }
    }
}