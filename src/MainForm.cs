using UDFCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnifiedDDRFlasher
{
    public class MainForm : Form
    {
        #region Constants

        private const string APP_VERSION = "v4.0.0";
        private const int BUS_MONITOR_INTERVAL_MS = 500;
        private const int DISCONNECT_CHECK_INTERVAL_MS = 2000;
        private const int BAUD_RATE = 115200;

        #endregion

        #region Fields

        private UDFDevice _spdDevice;
        private TabControl _mainTabControl;

        private SPDOperationsTab _spdTab;
        private PMICOperationsTab _pmicTab;
        private SidebandDevicesTab _sidebandTab;
        private bool _sidebandVisible;
        private FlasherConfigTab _configTab;

        private Panel _statusBar;
        private Label _errorLabel;
        private Panel _connectionIndicator;
        private Label _comPortLabel;
        private Label _connectionStatusLabel;

        private bool _isConnected;
        private string _currentPort = "";
        private int _errorCount;

        private CancellationTokenSource _connectionCts;
        private Task _busMonitorTask;
        private Task _disconnectCheckTask;

        private System.Windows.Forms.Timer _autoConnectTimer;

        #endregion

        #region Constructor

        public MainForm()
        {
#if DEBUG
            UDFCore.UDFDebug.Enabled = true;
#endif
            AppSettings.Load();
            InitializeCustomComponents();
            SetupEventHandlers();
            UpdateConnectionState(false);

            if (AppSettings.AutoConnect)
            {
                _autoConnectTimer = new System.Windows.Forms.Timer { Interval = 800 };
                _autoConnectTimer.Tick += OnAutoConnectTimerTick;
                _autoConnectTimer.Start();
            }
        }

        #endregion

        #region Initialization

        private void InitializeCustomComponents()
        {
            this.Text = $"Unified DDR Flasher {APP_VERSION}";
            this.Size = new Size(1600, 1200);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimumSize = new Size(1200, 750);
            this.StartPosition = FormStartPosition.CenterScreen;

            try
            {
                using (var iconStream = System.Reflection.Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("app.ico"))
                {
                    this.Icon = iconStream != null
                        ? new System.Drawing.Icon(iconStream)
                        : System.Drawing.SystemIcons.Application;
                }
            }
            catch { }

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

            _mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                Padding = new Point(10, 5)
            };

            _spdTab = new SPDOperationsTab();
            _pmicTab = new PMICOperationsTab();
            _sidebandTab = new SidebandDevicesTab();
            _configTab = new FlasherConfigTab();

            var spdPage = new TabPage("SPD Operations") { Padding = new Padding(3) };
            spdPage.Controls.Add(_spdTab);
            var pmicPage = new TabPage("PMIC Operations") { Padding = new Padding(3) };
            pmicPage.Controls.Add(_pmicTab);
            var sidebandPage = new TabPage("RCD / CKD / TS") { Padding = new Padding(3) };
            sidebandPage.Controls.Add(_sidebandTab);
            var configPage = new TabPage("Flasher Configuration") { Padding = new Padding(3) };
            configPage.Controls.Add(_configTab);

            _mainTabControl.TabPages.Add(spdPage);
            _mainTabControl.TabPages.Add(pmicPage);
            _sidebandVisible = AppSettings.ShowSidebandTab;
            if (_sidebandVisible)
                _mainTabControl.TabPages.Add(sidebandPage);
            _mainTabControl.TabPages.Add(configPage);
            _mainTabControl.SelectedTab = configPage;

            mainLayout.Controls.Add(_mainTabControl, 0, 0);
            mainLayout.Controls.Add(CreateBottomStatusBar(), 0, 1);
            this.Controls.Add(mainLayout);
        }

        private Panel CreateBottomStatusBar()
        {
            var statusBar = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(45, 45, 48),
                Padding = new Padding(8, 0, 8, 0)
            };

            _errorLabel = new Label
            {
                Text = "Errors: 0",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 4, 0, 0)
            };

            var rightPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false
            };

            _comPortLabel = new Label
            {
                Text = "Port: -",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(8, 4, 0, 0)
            };

            _connectionIndicator = new Panel
            {
                Width = 12,
                Height = 12,
                BackColor = Color.Red,
                Margin = new Padding(8, 6, 0, 0)
            };

            _connectionStatusLabel = new Label
            {
                Text = "Disconnected",
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 8.5F),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(8, 4, 5, 0)
            };

            rightPanel.Controls.Add(_comPortLabel);
            rightPanel.Controls.Add(_connectionIndicator);
            rightPanel.Controls.Add(_connectionStatusLabel);

            statusBar.Controls.Add(_errorLabel);
            statusBar.Controls.Add(rightPanel);
            _statusBar = statusBar;
            return statusBar;
        }

        private void SetupEventHandlers()
        {
            _configTab.ConnectionRequested += OnConnectRequested;
            _configTab.DisconnectionRequested += OnDisconnectRequested;
            _configTab.PortSettingsChanged += OnPortSettingsChanged;

            _spdTab.SetDeviceProvider(() => _spdDevice);
            _pmicTab.SetDeviceProvider(() => _spdDevice);
            _sidebandTab.SetDeviceProvider(() => _spdDevice);
            _configTab.SetDeviceProvider(() => _spdDevice);

            _spdTab.ErrorOccurred += OnErrorOccurred;
            _pmicTab.ErrorOccurred += OnErrorOccurred;
            _sidebandTab.ErrorOccurred += OnErrorOccurred;
            _configTab.ErrorOccurred += OnErrorOccurred;
        }

        #endregion

        #region Auto-Connect

        private void OnAutoConnectTimerTick(object sender, EventArgs e)
        {
            _autoConnectTimer.Stop();
            _autoConnectTimer.Dispose();
            _autoConnectTimer = null;

            string savedPort = AppSettings.LastPort;
            if (string.IsNullOrEmpty(savedPort)) return;

            string[] available = SerialPort.GetPortNames();
            if (Array.IndexOf(available, savedPort) < 0)
            {
                bool found = false;
                foreach (var p in available)
                    if (string.Equals(p, savedPort, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                if (!found)
                {
                    LogMessage($"[AutoConnect] Saved port {savedPort} not available - skipping.");
                    return;
                }
            }

            LogMessage($"[AutoConnect] Attempting connection to remembered port {savedPort}");
            _configTab.SelectPort(savedPort);
            OnConnectRequested(this, EventArgs.Empty);
        }

        #endregion

        #region Connection Management

        private async void OnConnectRequested(object sender, EventArgs e)
        {
            string portName = _configTab.GetSelectedPort();
            if (string.IsNullOrEmpty(portName) || portName == "No ports available")
            {
                _configTab.LogError("Select a COM port before connecting.");
                MessageBox.Show("Please select a valid COM port first.", "Connection Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            LogMessage($"Connecting to {portName} at {BAUD_RATE} baud");

            try
            {
                _spdDevice = await Task.Run(() => new UDFDevice(portName, BAUD_RATE)).ConfigureAwait(true);

                // identity check runs before any state setup, so a rejected device has nothing to unwind
                UDFDevice.DeviceIdentity ident;
                try
                {
                    ident = await Task.Run(() => _spdDevice.Identify()).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception identEx)
                {
                    ident = new UDFDevice.DeviceIdentity(false, 0x40,
                        $"check raised {identEx.GetType().Name}");
                }

                if (ident.Recognized)
                {
                    LogMessage($"Device check: ✓ {ident.Detail}");
                }
                else
                {
                    LogError($"Device check: ✗ {ident.Detail} (code 0x{ident.Code:X2})");
                }

                if (ident.ShouldRefuseConnect)
                {
                    LogError($"Device check failed - refusing to continue (code 0x{ident.Code:X2}).");
                    CleanupAfterFailedConnect();
                    MessageBox.Show(
                        "Device check failed:\n\n" + ident.Detail +
                        $" (code 0x{ident.Code:X2})" +
                        "\n\nThe connection has been closed.",
                        "Device check", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                uint version = await Task.Run(() => _spdDevice.GetVersion()).ConfigureAwait(true);
                string devName = await Task.Run(() => _spdDevice.GetDeviceName()).ConfigureAwait(true);

                _currentPort = portName;
                _isConnected = true;
                _errorCount = 0;
                UpdateErrorDisplay();

                AppSettings.LastPort = portName;
                AppSettings.Save();

                _spdDevice.AlertReceived += OnDeviceAlert;

                _spdTab.OnDeviceConnected(_spdDevice);
                _pmicTab.OnDeviceConnected(_spdDevice);
                if (_sidebandVisible) _sidebandTab.OnDeviceConnected(_spdDevice);
                _configTab.OnDeviceConnected(_spdDevice);

                StartBackgroundLoops();
                UpdateConnectionState(true);

                LogMessage($"Connected: {devName} (FW: 0x{version:X8}) on {portName}");
                _configTab.LogError($"Connected: {(string.IsNullOrEmpty(devName) ? "(unnamed)" : devName)} on {portName}");
            }
            catch (OperationCanceledException)
            {
                CleanupAfterFailedConnect();
            }
            catch (TimeoutException ex)
            {
                CleanupAfterFailedConnect();
                LogError($"Connection timeout: {ex.Message}");
                MessageBox.Show($"Connection timed out:\n\n{ex.Message}",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _configTab.LogError($"Connection timeout: {ex.Message}");
            }
            catch (Exception ex)
            {
                CleanupAfterFailedConnect();
                LogError($"Connection failed: {ex.Message}");
                MessageBox.Show(
                    $"Failed to connect:\n\n{ex.Message}\n\n" +
                    "Check:\n1. Device is powered\n2. Correct COM port selected\n3. Drivers installed\n4. No other software using the port",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _configTab.LogError($"Connection failed: {ex.Message}");
            }
        }

        private void CleanupAfterFailedConnect()
        {
            _isConnected = false;
            UpdateConnectionState(false);
            try { _spdDevice?.Dispose(); } catch { }
            _spdDevice = null;
        }
        private void StartBackgroundLoops()
        {
            _connectionCts = new CancellationTokenSource();
            var ct = _connectionCts.Token;
            _busMonitorTask = Task.Run(() => BusMonitorLoopAsync(ct));
            _disconnectCheckTask = Task.Run(() => DisconnectCheckLoopAsync(ct));
        }

        private async Task BusMonitorLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(BUS_MONITOR_INTERVAL_MS, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;

                    var dev = _spdDevice;
                    if (dev == null) continue;

                    var spdDevices = await dev.ScanBusAsync(ct).ConfigureAwait(false);
                    var pmicDevices = new List<byte>();
                    for (byte addr = 0x48; addr <= 0x4F; addr++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (await dev.ProbeAddressAsync(addr, ct).ConfigureAwait(false))
                                pmicDevices.Add(addr);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (IOException) { throw; }
                        catch (UnauthorizedAccessException) { throw; }
                        catch (InvalidOperationException) { throw; }
                        catch { }
                    }

                    var allDevices = new List<byte>(spdDevices);
                    allDevices.AddRange(pmicDevices);

                    PostToUi(() =>
                    {
                        _spdTab.UpdateDetectedDevices(allDevices);
                        _pmicTab.UpdateDetectedDevices(allDevices);
                        if (_sidebandVisible) _sidebandTab.UpdateDetectedDevices(allDevices);
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is InvalidOperationException)
                {
                    PostToUi(() => TriggerDisconnect("bus monitor: " + ex.Message));
                    break;
                }
                catch (TimeoutException ex)
                {
                    PostToUi(() => LogError($"Bus scan timeout: {ex.Message}"));
                }
                catch (Exception ex)
                {
                    PostToUi(() => LogError($"Bus scan error: {ex.Message}"));
                }
            }
        }

        private async Task DisconnectCheckLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DISCONNECT_CHECK_INTERVAL_MS, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested) break;

                    var dev = _spdDevice;
                    if (dev == null) continue;

                    // two disconnect signals: the port stops enumerating (fast), or a ping comes back false
                    string port = _currentPort;
                    if (!string.IsNullOrEmpty(port))
                    {
                        bool stillThere;
                        try
                        {
                            string[] names = SerialPort.GetPortNames();
                            stillThere = false;
                            foreach (var n in names)
                                if (string.Equals(n, port, StringComparison.OrdinalIgnoreCase))
                                { stillThere = true; break; }
                        }
                        catch
                        {
                            stillThere = true;
                        }
                        if (!stillThere)
                        {
                            PostToUi(() => TriggerDisconnect($"port {port} unplugged", isError: false));
                            break;
                        }
                    }

                    bool ok = await dev.PingAsync(ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        PostToUi(() => TriggerDisconnect("device not responding to ping"));
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is InvalidOperationException)
                {
                    PostToUi(() => TriggerDisconnect("disconnect check: " + ex.Message));
                    break;
                }
                catch (Exception ex)
                {
                    PostToUi(() => LogError($"Disconnect check failed: {ex.Message}"));
                    break;
                }
            }
        }

        private void TriggerDisconnect(string reason, bool isError = true)
        {
            if (!_isConnected) return;
            if (isError)
            {
                LogError($"Disconnected ({reason})");
            }
            else
            {
                LogMessage($"Disconnected ({reason})");
                _configTab.LogError($"Disconnected ({reason})");
            }
            OnDisconnectRequested(this, EventArgs.Empty);
        }

        private void OnDisconnectRequested(object sender, EventArgs e)
        {
            if (!_isConnected && _spdDevice == null) return;

            _isConnected = false;
            try
            {
                if (_connectionCts != null)
                {
                    try { _connectionCts.Cancel(); } catch { }
                }

                if (_spdDevice != null)
                {
                    _spdDevice.AlertReceived -= OnDeviceAlert;
                    try { _spdDevice.Dispose(); } catch (Exception ex) { LogMessage($"Dispose threw: {ex.Message}"); }
                    _spdDevice = null;
                }

                try
                {
                    if (_busMonitorTask != null)
                        _busMonitorTask.Wait(500);
                    if (_disconnectCheckTask != null)
                        _disconnectCheckTask.Wait(500);
                }
                catch { }

                try { _connectionCts?.Dispose(); } catch { }
                _connectionCts = null;
                _busMonitorTask = null;
                _disconnectCheckTask = null;

                _currentPort = "";

                _spdTab.OnDeviceDisconnected();
                _pmicTab.OnDeviceDisconnected();
                if (_sidebandVisible) _sidebandTab.OnDeviceDisconnected();
                _configTab.OnDeviceDisconnected();

                UpdateConnectionState(false);
                LogMessage("Device disconnected");
            }
            catch (Exception ex)
            {
                LogError($"Error during disconnect: {ex.Message}");
                _configTab.LogError($"Disconnect error: {ex.Message}");
            }
        }

        private void OnDeviceAlert(object sender, AlertEventArgs e)
        {
            PostToUi(() =>
            {
                LogMessage($"[ALERT] {e.AlertType}");
                if (e.AlertCode == 0x2B || e.AlertCode == 0x2D)
                {
                }
            });
        }

        private void UpdateConnectionState(bool connected)
        {
            if (InvokeRequired) { Invoke(new Action(() => UpdateConnectionState(connected))); return; }

            _isConnected = connected;
            _spdTab.Enabled = connected;
            _pmicTab.Enabled = connected;
            _sidebandTab.Enabled = connected;

            _connectionIndicator.BackColor = connected ? Color.LimeGreen : Color.Red;
            _comPortLabel.Text = connected ? $"Port: {_currentPort}" : "Port: -";

            if (_connectionStatusLabel != null)
            {
                _connectionStatusLabel.Text = connected ? "Connected" : "Disconnected";
                _connectionStatusLabel.ForeColor = connected ? Color.LimeGreen : Color.LightGray;
            }
            _configTab.SetConnectionState(connected);
        }

        private void OnPortSettingsChanged(object sender, EventArgs e)
        {
            if (!_isConnected) return;
            var result = MessageBox.Show(
                "Changing port settings requires reconnection. Disconnect now?",
                "Port Settings Changed", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
                OnDisconnectRequested(this, EventArgs.Empty);
        }

        #endregion

        #region UI helpers

        private void PostToUi(Action action)
        {
            if (this.IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        #endregion

        #region Error Handling

        private void OnErrorOccurred(object sender, string errorMessage)
        {
            LogError(errorMessage);
            _configTab.LogError(errorMessage);
        }

        private void LogError(string message)
        {
            _errorCount++;
            if (InvokeRequired) Invoke(new Action(UpdateErrorDisplay));
            else UpdateErrorDisplay();
            LogMessage($"[ERROR] {message}");
        }

        private void UpdateErrorDisplay()
        {
            if (_errorLabel == null) return;
            _errorLabel.Text = $"Errors: {_errorCount}";
            _errorLabel.ForeColor = _errorCount > 0 ? Color.Yellow : Color.White;
        }

        private void LogMessage(string message)
        {
            UDFCore.UDFDebug.Log("MainForm: " + message);
        }

        #endregion

        #region Form Events

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isConnected)
            {
                var result = MessageBox.Show(
                    "Device is still connected. Close anyway?",
                    "Confirm Exit", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.No) { e.Cancel = true; return; }
                OnDisconnectRequested(this, EventArgs.Empty);
            }

            if (_autoConnectTimer != null)
            {
                _autoConnectTimer.Stop();
                _autoConnectTimer.Dispose();
                _autoConnectTimer = null;
            }

            base.OnFormClosing(e);
        }

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt32() == DBT_DEVNODES_CHANGED)
            {
                try { _configTab?.OnDeviceChangeMessage(); }
                catch { }
            }
            base.WndProc(ref m);
        }

        #endregion
    }
}