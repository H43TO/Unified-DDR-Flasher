using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using UDFCore;

namespace UnifiedDDRFlasher
{
    public class FlasherConfigTab : UserControl
    {
        #region Constants

        private const int DEFAULT_BAUD_RATE = 115200;
        private const int PORT_REFRESH_INTERVAL_MS = 2000;

        #endregion

        #region Events

        public event EventHandler ConnectionRequested;
        public event EventHandler DisconnectionRequested;
        public event EventHandler PortSettingsChanged;
        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region Private Fields

        private Func<UDFDevice> _deviceProvider;
        private UDFDevice Device => _deviceProvider?.Invoke();
        private bool _isConnected = false;

        private ComboBox _portCombo;
        private Button _connectButton;
        private Button _disconnectButton;
        private Button _advancedButton;
        private Label _firmwareLabel;
        private Label _deviceNameLabel;
        private Label _statusValueLabel;
        private Label _portValueLabel;

        private RichTextBox _errorLogText;

        private System.Windows.Forms.Timer _portRefreshTimer;

        private string[] _lastKnownPorts = Array.Empty<string>();

        private ToolTip _toolTip;

        #endregion

        #region Constructor

        public FlasherConfigTab()
        {
            _toolTip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400 };
            InitializeComponent();

            _portRefreshTimer = new System.Windows.Forms.Timer { Interval = PORT_REFRESH_INTERVAL_MS };
            _portRefreshTimer.Tick += OnPortRefreshTick;
            _portRefreshTimer.Start();

            LoadPorts();
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.BackColor = Color.FromArgb(240, 240, 240);

            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));

            TableLayoutPanel leftCol = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2
            };
            leftCol.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            leftCol.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            leftCol.Controls.Add(CreateErrorLogGroup(), 0, 0);
            leftCol.Controls.Add(CreateConnectionGroup(), 0, 1);
            mainLayout.Controls.Add(leftCol, 0, 0);

            mainLayout.Controls.Add(CreateInfoPanel(), 1, 0);

            this.Controls.Add(mainLayout);
        }

        private GroupBox CreateErrorLogGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Event Log",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            _errorLogText = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 8.5F),
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250),
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            group.Controls.Add(_errorLogText);
            return group;
        }

        private GroupBox CreateConnectionGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "Connection",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Padding = new Padding(12),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 9,
                Padding = new Padding(6)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var portLabel = new Label
            {
                Text = "COM Port  (auto-refreshed)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = Color.Gray
            };
            layout.Controls.Add(portLabel, 0, 0);

            _portCombo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10.5F)
            };
            _portCombo.SelectedIndexChanged += (s, e) => PortSettingsChanged?.Invoke(this, EventArgs.Empty);
            layout.Controls.Add(_portCombo, 0, 1);

            TableLayoutPanel buttonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Margin = new Padding(0, 4, 0, 0)
            };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));

            _connectButton = new Button
            {
                Text = "Connect",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 4, 0)
            };
            _connectButton.FlatAppearance.BorderSize = 0;
            _connectButton.Click += (s, e) => ConnectionRequested?.Invoke(this, EventArgs.Empty);
            _toolTip.SetToolTip(_connectButton, "Connect to the selected COM port.");

            _advancedButton = new Button
            {
                Text = "Advanced\u2026",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0)
            };
            _advancedButton.FlatAppearance.BorderColor = Color.Silver;
            _advancedButton.Click += OnAdvancedClicked;
            _toolTip.SetToolTip(_advancedButton, "Device name, factory reset, auto-connect, I2C speed, pin control.");

            buttonRow.Controls.Add(_connectButton, 0, 0);
            buttonRow.Controls.Add(_advancedButton, 1, 0);
            layout.Controls.Add(buttonRow, 0, 2);

            _disconnectButton = new Button
            {
                Text = "Disconnect",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F),
                BackColor = Color.FromArgb(200, 200, 200),
                ForeColor = Color.Black,
                FlatStyle = FlatStyle.Flat,
                Enabled = false,
                Margin = new Padding(0, 4, 0, 0)
            };
            _disconnectButton.FlatAppearance.BorderSize = 0;
            _disconnectButton.Click += (s, e) => DisconnectionRequested?.Invoke(this, EventArgs.Empty);
            layout.Controls.Add(_disconnectButton, 0, 3);

            layout.Controls.Add(new Panel(), 0, 4);

            _statusValueLabel = new Label
            {
                Text = "\u25CF  Disconnected",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(170, 40, 40)
            };
            layout.Controls.Add(_statusValueLabel, 0, 5);

            _portValueLabel = MakeInfoLabel("Port:      -");
            layout.Controls.Add(_portValueLabel, 0, 6);

            _firmwareLabel = MakeInfoLabel("Firmware:  -");
            layout.Controls.Add(_firmwareLabel, 0, 7);

            _deviceNameLabel = MakeInfoLabel("Device:    -");
            _deviceNameLabel.TextAlign = ContentAlignment.TopLeft;
            layout.Controls.Add(_deviceNameLabel, 0, 8);

            group.Controls.Add(layout);
            return group;
        }

        private static Label MakeInfoLabel(string text) => new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(70, 70, 70)
        };

        private GroupBox CreateInfoPanel()
        {
            var group = new GroupBox
            {
                Text = "Getting Started Guide",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Padding = new Padding(10),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var rtb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(240, 240, 240),
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.75F),
                ForeColor = Color.FromArgb(40, 40, 40),
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            bool firstHeading = true;
            void H(string text)
            {
                if (!firstHeading) rtb.AppendText("\n");
                firstHeading = false;
                rtb.SelectionBullet = false;
                rtb.SelectionIndent = 0;
                rtb.SelectionFont = new Font("Segoe UI", 11.5F, FontStyle.Bold);
                rtb.SelectionColor = Color.FromArgb(0, 78, 152);
                rtb.AppendText(text + "\n");
            }
            void B(string text)
            {
                rtb.SelectionBullet = false;
                rtb.SelectionIndent = 0;
                rtb.SelectionFont = new Font("Segoe UI", 9.75F, FontStyle.Regular);
                rtb.SelectionColor = Color.FromArgb(40, 40, 40);
                rtb.AppendText(text + "\n");
            }
            void Li(string text)
            {
                rtb.SelectionIndent = 12;
                rtb.SelectionBullet = true;
                rtb.SelectionFont = new Font("Segoe UI", 9.75F, FontStyle.Regular);
                rtb.SelectionColor = Color.FromArgb(40, 40, 40);
                rtb.AppendText(text + "\n");
                rtb.SelectionBullet = false;
                rtb.SelectionIndent = 0;
            }
            void Cmd(string text)
            {
                rtb.SelectionBullet = false;
                rtb.SelectionIndent = 16;
                rtb.SelectionFont = new Font("Consolas", 9.5F);
                rtb.SelectionColor = Color.FromArgb(60, 60, 60);
                rtb.AppendText(text + "\n");
                rtb.SelectionIndent = 0;
            }

            H("\u2460  Plug in and pick the port");
            B("Plug the programmer into a USB port. Windows sorts the driver out on its own, and the board shows up as a COM port in the dropdown on the left within a second or two.");
            B("Pick it and hit Connect. The status line under the buttons turns green, and the firmware version and device name fill in right below it.");

            H("\u2461  Read a module");
            B("Head to the SPD Operations tab. Every module the programmer found is in the address dropdown, usually 0x50 to 0x57. Pick one, click Read SPD, and the hex viewer fills with the raw bytes.");
            B("The Module Info box gives you the quick take: generation, SPD size, speed grade, capacity, width, and whether the CRC checks out.");
            B("Want the whole story? Click \"Parsed Fields...\" for every timing in nCK and ps, the CAS list, the manufacturer block, and a Recalc & Fix CRC button.");

            H("\u2462  Save, edit, write");
            Li("Save Dump writes the loaded bytes to a .bin file.");
            Li("Open Dump loads a .bin from disk. Works offline, no device needed.");
            Li("Verify reads the module again and diffs it against what is in memory.");
            Li("Write All pushes the dump to the module. Write protection is handled for you: RSWP is cleared on DDR5 and HV is applied on DDR4.");
            B("Edited timings by hand and the CRC went red? Open Parsed Fields, hit Recalc & Fix CRC, then Write All. Nothing touches the module until you write.");

            H("\u2463  PMIC work (DDR5 only)");
            B("PMICs at 0x48 to 0x4F are picked up automatically when you connect. The PMIC tab streams live rail voltages and currents once a second, dumps the full register space, and burns vendor MTP blocks when you tell it to.");
            B("Burning is permanent, so there is always a confirmation first. Unlock uses the standard password unless you have saved your own for that PMIC type.");

            H("\u2464  Advanced settings");
            B("The Advanced button (enabled while connected) covers the rest: a custom device name stored on the programmer, the I2C bus speed, auto-connect on launch, factory reset, and manual control over every board pin.");
            B("Rule of thumb for speed: DDR5 wants 1 MHz, DDR4 is happiest at 400 kHz. Drop a notch if a worn socket starts throwing read errors.");

            H("\u2465  Script it");
            B("Everything the GUI does works from the command line too. Run the exe with arguments and there is no window at all, just results on stdout and proper exit codes (0 ok, 1 device, 2 I2C, 3 verify, 4 CRC, 5 write, 6 usage).");
            Cmd("UDFFlasher.exe ping --auto-detect");
            Cmd("UDFFlasher.exe spd read 0x50 --port COM4 --out m.bin");
            Cmd("UDFFlasher.exe spd verify 0x50 --in golden.bin");

            H("If something's off");
            Li("No ports listed: check the cable (charge-only cables are sneaky) and give the driver a few seconds.");
            Li("Connect fails: replug, try another cable or another USB port.");
            Li("SPD reads error out: drop the I2C speed one notch and reseat the module.");
            Li("CRC FAIL on a fresh read: XMP and EXPO vendors sometimes bend the CRC rules. If the timings look sane, the data is probably fine.");
            Li("No PMIC found: check the VIN_CTRL pin; some sticks need PMIC_CTRL toggled before a scan.");

            rtb.SelectionStart = 0;
            group.Controls.Add(rtb);
            return group;
        }

        #endregion

        #region Public Methods

        public void LogError(string message)
        {
            if (InvokeRequired) { Invoke(new Action(() => LogError(message))); return; }

            string ts = DateTime.Now.ToString("HH:mm:ss");
            _errorLogText.SelectionColor = message.Contains("[ERROR]") ? Color.DarkRed : Color.Black;
            _errorLogText.AppendText($"[{ts}] {message}\n");
            _errorLogText.ScrollToCaret();
        }

        public void SetDeviceProvider(Func<UDFDevice> deviceProvider) =>
            _deviceProvider = deviceProvider;

        public string GetSelectedPort() => _portCombo.SelectedItem?.ToString() ?? "";
        public int GetBaudRate() => DEFAULT_BAUD_RATE;

        public void SelectPort(string portName)
        {
            if (InvokeRequired) { Invoke(new Action(() => SelectPort(portName))); return; }
            int idx = _portCombo.FindStringExact(portName);
            if (idx >= 0) _portCombo.SelectedIndex = idx;
        }

        public void RefreshPorts() => LoadPorts();

        public void SetConnectionState(bool connected)
        {
            _isConnected = connected;
            if (InvokeRequired) { Invoke(new Action(() => SetConnectionState(connected))); return; }

            _portCombo.Enabled = !connected;
            _connectButton.Enabled = !connected;
            _disconnectButton.Enabled = connected;

            if (connected)
            {
                _disconnectButton.BackColor = Color.FromArgb(192, 0, 0);
                _disconnectButton.ForeColor = Color.White;
            }
            else
            {
                _disconnectButton.BackColor = Color.FromArgb(200, 200, 200);
                _disconnectButton.ForeColor = Color.Black;
            }
        }

        public void OnDeviceConnected(UDFDevice device)
        {
            if (device == null) return;
            try
            {
                uint version = device.GetVersion();
                string readable = $"{version / 10000}-{(version / 100) % 100:D2}-{version % 100:D2}";
                _firmwareLabel.Text = $"Firmware:  {readable}";

                string devName = device.GetDeviceName();
                _deviceNameLabel.Text = $"Device:    {(string.IsNullOrEmpty(devName) ? "(unnamed)" : devName)}";

                _statusValueLabel.Text = "\u25CF  Connected";
                _statusValueLabel.ForeColor = Color.FromArgb(30, 130, 50);
                _portValueLabel.Text = $"Port:      {GetSelectedPort()}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading device info:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ErrorOccurred?.Invoke(this, $"Device info read failed: {ex.Message}");
            }
        }

        public void OnDeviceDisconnected()
        {
            _firmwareLabel.Text = "Firmware:  -";
            _deviceNameLabel.Text = "Device:    -";
            _statusValueLabel.Text = "\u25CF  Disconnected";
            _statusValueLabel.ForeColor = Color.FromArgb(170, 40, 40);
            _portValueLabel.Text = "Port:      -";
        }

        #endregion

        #region Event Handlers

        private void OnPortRefreshTick(object sender, EventArgs e)
        {
            if (_isConnected) return;
            RefreshIfPortsChanged();
        }

        public void OnDeviceChangeMessage()
        {
            if (IsDisposed || _isConnected) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshIfPortsChanged)); return; }
            RefreshIfPortsChanged();
        }

        private void RefreshIfPortsChanged()
        {
            string[] current = EnumeratePortsFromRegistry();
            if (!string.Join(",", current).Equals(string.Join(",", _lastKnownPorts)))
            {
                _lastKnownPorts = current;
                LoadPorts();
            }
        }

        private static string[] EnumeratePortsFromRegistry()
        {
            var ports = new List<string>();
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM"))
                {
                    if (key != null)
                    {
                        foreach (string valueName in key.GetValueNames())
                        {
                            if (key.GetValue(valueName) is string portName &&
                                !string.IsNullOrWhiteSpace(portName))
                            {
                                ports.Add(portName);
                            }
                        }
                    }
                }
            }
            catch
            {
                try { ports.AddRange(SerialPort.GetPortNames()); } catch { }
            }
            ports.Sort((a, b) =>
            {
                int an = ParseComNumber(a), bn = ParseComNumber(b);
                if (an >= 0 && bn >= 0) return an.CompareTo(bn);
                return string.CompareOrdinal(a, b);
            });
            return ports.ToArray();
        }

        private static int ParseComNumber(string portName)
        {
            if (string.IsNullOrEmpty(portName) || !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                return -1;
            return int.TryParse(portName.Substring(3), out int n) ? n : -1;
        }

        private void LoadPorts()
        {
            if (InvokeRequired) { Invoke(new Action(LoadPorts)); return; }

            if (_portCombo.DroppedDown) return;

            string current = _portCombo.SelectedItem?.ToString();
            _portCombo.Items.Clear();

            string[] ports = EnumeratePortsFromRegistry();
            if (ports.Length > 0)
            {
                _portCombo.Items.AddRange(ports);
                if (!string.IsNullOrEmpty(current) && Array.IndexOf(ports, current) >= 0)
                    _portCombo.SelectedItem = current;
                else
                    _portCombo.SelectedIndex = 0;
            }
            else
            {
                _portCombo.Items.Add("No ports available");
                _portCombo.SelectedIndex = 0;
            }
        }

        private void OnAdvancedClicked(object sender, EventArgs e)
        {
            using var dlg = new AdvancedConfigDialog(Device, _deviceProvider,
                DisconnectionRequested, ConnectionRequested, this);
            dlg.ShowDialog(this);

            if (Device != null)
            {
                try { _deviceNameLabel.Text = $"Device:    {Device.GetDeviceName()}"; }
                catch { }
            }
        }

        #endregion
    }


    internal sealed class AdvancedConfigDialog : Form
    {
        private readonly UDFDevice _device;
        private readonly Func<UDFDevice> _provider;
        private readonly EventHandler _disconnectHandler;
        private readonly EventHandler _connectHandler;
        private readonly FlasherConfigTab _parent;

        private TextBox _nameText;
        private ComboBox _i2cSpeedCombo;
        private CheckBox _autoConnectCheck;

        private Label[] _pinStateLabels;
        private System.Windows.Forms.Timer _pinRefreshTimer;
        private const int AUTO_CONNECT_DELAY_MS = 500;

        private static readonly string[] I2CSpeedLabels =
        {
            "100 kHz  (Standard – safest)",
            "400 kHz  (Fast)",
            "1 MHz    (Fast-Plus – DDR5 recommended)"
        };

        private static readonly (string name, byte id, string tip)[] PinDefs =
        {
            ("HV Switch",     UDFDevice.PIN_HV_SWITCH,      "Opto-coupler that enables ~9 V HV programming for DDR3/4."),
            ("SA1 Switch",    UDFDevice.PIN_SA1_SWITCH,     "Selects between two SPD devices on some adapters."),
            ("Dev Status",    UDFDevice.PIN_DEV_STATUS,     "Status LED on the programmer board."),
            ("HV Converter",  UDFDevice.PIN_HV_CONVERTER,  "Enable pin of the HV boost converter (DDR3/4 WP clearing)."),
            ("DDR5 VIN Ctrl", UDFDevice.PIN_DDR5_VIN_CTRL, "DDR5 power-supply enable. Must be high for DDR5 access."),
            ("PMIC CTRL",     UDFDevice.PIN_PMIC_CTRL,     "PWR_EN line of the PMIC. Forces all phases active."),
            ("PMIC FLAG",     UDFDevice.PIN_PMIC_FLAG,     "PMIC PWR_GOOD input (read-only)."),
            ("RFU1",          UDFDevice.PIN_RFU1,          "Reserved for future use."),
            ("RFU2",          UDFDevice.PIN_RFU2,          "Reserved for future use."),
        };

        public AdvancedConfigDialog(UDFDevice device, Func<UDFDevice> provider,
            EventHandler disconnectHandler, EventHandler connectHandler, FlasherConfigTab parent)
        {
            _device = device;
            _provider = provider;
            _disconnectHandler = disconnectHandler;
            _connectHandler = connectHandler;
            _parent = parent;

            Text = "Advanced Configuration";
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(600, 740);
            MinimumSize = new Size(560, 640);
            BackColor = Color.FromArgb(240, 240, 240);

            BuildUI();

            _pinRefreshTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _pinRefreshTimer.Tick += (s, e) => RefreshPinStates();
            if (_device != null) _pinRefreshTimer.Start();

            this.FormClosed += (s, e) => _pinRefreshTimer.Stop();
        }

        private void BuildUI()
        {
            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 5,
                ColumnCount = 1,
                Padding = new Padding(14)
            };
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            GroupBox nameGroup = new GroupBox
            {
                Text = "Device Name",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var nameFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
            _nameText = new TextBox
            {
                Width = 200,
                MaxLength = 16,
                Font = new Font("Segoe UI", 9F),
                Text = (_device != null) ? TryGetName() : "",
                Enabled = _device != null
            };
            var setNameBtn = new Button
            {
                Text = "Set Name",
                Width = 90,
                Height = 26,
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(6, 0, 0, 0),
                Enabled = _device != null
            };
            setNameBtn.Click += OnSetNameClicked;
            nameFlow.Controls.Add(_nameText);
            nameFlow.Controls.Add(setNameBtn);
            nameGroup.Controls.Add(nameFlow);
            main.Controls.Add(nameGroup, 0, 0);

            GroupBox miscGroup = new GroupBox
            {
                Text = "Programmer Options",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var miscLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                RowCount = 2,
                Padding = new Padding(2)
            };
            miscLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            miscLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var miscFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };

            var factoryBtn = new Button
            {
                Text = "⚠ Factory Reset",
                Width = 140,
                Height = 28,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(192, 0, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 8, 0),
                Enabled = _device != null
            };
            factoryBtn.FlatAppearance.BorderSize = 0;
            factoryBtn.Click += OnFactoryResetClicked;

            miscFlow.Controls.Add(factoryBtn);
            miscLayout.Controls.Add(miscFlow, 0, 0);

            _autoConnectCheck = new CheckBox
            {
                Text = "Auto-Connect to last used port on startup",
                Font = new Font("Segoe UI", 9F),
                AutoSize = true,
                Checked = AppSettings.AutoConnect,
                Margin = new Padding(2, 4, 0, 0)
            };
            _autoConnectCheck.CheckedChanged += (s, e) =>
            {
                AppSettings.AutoConnect = _autoConnectCheck.Checked;
                AppSettings.Save();
            };
            miscLayout.Controls.Add(_autoConnectCheck, 0, 1);

            miscGroup.Controls.Add(miscLayout);
            main.Controls.Add(miscGroup, 0, 1);

            GroupBox i2cGroup = new GroupBox
            {
                Text = "I2C Bus Speed",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var i2cFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };

            _i2cSpeedCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F),
                Width = 300,
                Enabled = _device != null
            };
            _i2cSpeedCombo.Items.AddRange(I2CSpeedLabels);

            if (_device != null)
            {
                try
                {
                    byte mode = _device.GetI2CClockMode();
                    _i2cSpeedCombo.SelectedIndex = Math.Min(mode, (byte)2);
                }
                catch { _i2cSpeedCombo.SelectedIndex = 0; }
            }
            else
            {
                _i2cSpeedCombo.SelectedIndex = 0;
            }

            var applyI2cBtn = new Button
            {
                Text = "Apply",
                Width = 80,
                Height = 26,
                Font = new Font("Segoe UI", 8.5F),
                Margin = new Padding(8, 0, 0, 0),
                Enabled = _device != null
            };
            applyI2cBtn.Click += OnApplyI2CClicked;

            i2cFlow.Controls.Add(_i2cSpeedCombo);
            i2cFlow.Controls.Add(applyI2cBtn);
            i2cGroup.Controls.Add(i2cFlow);
            main.Controls.Add(i2cGroup, 0, 2);

            GroupBox pinGroup = new GroupBox
            {
                Text = "Manual Pin Control  (live readout)",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            var pinTable = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = PinDefs.Length + 1,
                ColumnCount = 4,
                AutoScroll = true
            };
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54F));
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            pinTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < PinDefs.Length; i++)
                pinTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            pinTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _pinStateLabels = new Label[PinDefs.Length];

            var tt = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400 };

            for (int i = 0; i < PinDefs.Length; i++)
            {
                var (pinName, pinId, tip) = PinDefs[i];
                int capturedI = i;
                byte capturedPin = pinId;

                var lbl = new Label
                {
                    Text = pinName,
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 8.5F),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(2, 0, 0, 0)
                };
                tt.SetToolTip(lbl, tip);

                _pinStateLabels[i] = new Label
                {
                    Text = _device != null ? ReadPin(pinId) : "-",
                    Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 8.5F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.DimGray
                };

                var hiBtn = new Button
                {
                    Text = "Set HIGH",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 8F),
                    Height = 24,
                    Margin = new Padding(1),
                    Enabled = _device != null && pinId != UDFDevice.PIN_PMIC_FLAG
                };
                hiBtn.Click += (s, e) =>
                {
                    _device?.SetPin(capturedPin, 0x01);
                    _pinStateLabels[capturedI].Text = ReadPin(capturedPin);
                };

                var loBtn = new Button
                {
                    Text = "Set LOW",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 8F),
                    Height = 24,
                    Margin = new Padding(1),
                    Enabled = _device != null && pinId != UDFDevice.PIN_PMIC_FLAG
                };
                loBtn.Click += (s, e) =>
                {
                    _device?.SetPin(capturedPin, 0x00);
                    _pinStateLabels[capturedI].Text = ReadPin(capturedPin);
                };

                pinTable.Controls.Add(lbl, 0, i);
                pinTable.Controls.Add(_pinStateLabels[i], 1, i);
                pinTable.Controls.Add(hiBtn, 2, i);
                pinTable.Controls.Add(loBtn, 3, i);
            }

            var resetAllBtn = new Button
            {
                Text = "Reset All Pins to Default",
                Dock = DockStyle.Bottom,
                Font = new Font("Segoe UI", 9F),
                Height = 28,
                Enabled = _device != null
            };
            resetAllBtn.Click += (s, e) =>
            {
                _device?.ResetPins();
                RefreshPinStates();
            };

            var pinPanelWrapper = new Panel { Dock = DockStyle.Fill };
            pinPanelWrapper.Controls.Add(pinTable);
            pinPanelWrapper.Controls.Add(resetAllBtn);
            pinGroup.Controls.Add(pinPanelWrapper);
            main.Controls.Add(pinGroup, 0, 3);

            var closeFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0)
            };
            var closeBtn = new Button
            {
                Text = "Close",
                Width = 90,
                Height = 30,
                Font = new Font("Segoe UI", 9F)
            };
            closeBtn.Click += (s, e) => Close();
            closeFlow.Controls.Add(closeBtn);
            main.Controls.Add(closeFlow, 0, 4);

            this.Controls.Add(main);
        }

        private string TryGetName()
        {
            try { return _device.GetDeviceName(); }
            catch { return ""; }
        }

        private string ReadPin(byte pin)
        {
            try { return _device.GetPin(pin) == 1 ? "HIGH" : "LOW"; }
            catch { return "-"; }
        }

        private void RefreshPinStates()
        {
            if (_device == null || _pinStateLabels == null) return;
            if (InvokeRequired) { Invoke(new Action(RefreshPinStates)); return; }

            for (int i = 0; i < PinDefs.Length; i++)
            {
                try
                {
                    byte state = _device.GetPin(PinDefs[i].id);
                    _pinStateLabels[i].Text = state == 1 ? "HIGH" : "LOW";
                    _pinStateLabels[i].ForeColor = state == 1 ? Color.DarkGreen : Color.DarkRed;
                }
                catch
                {
                    _pinStateLabels[i].Text = "-";
                    _pinStateLabels[i].ForeColor = Color.Gray;
                }
            }
        }

        private void OnApplyI2CClicked(object sender, EventArgs e)
        {
            if (_device == null) return;
            try
            {
                byte mode = (byte)Math.Max(0, Math.Min(2, _i2cSpeedCombo.SelectedIndex));
                if (_device.SetI2CClockMode(mode))
                    MessageBox.Show($"I2C clock set to {I2CSpeedLabels[mode]}.", "Applied",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show($"Failed to set I2C clock.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting I2C clock:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnSetNameClicked(object sender, EventArgs e)
        {
            if (_device == null) return;
            string name = _nameText.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Enter a device name (1–16 characters).", "Invalid",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                bool ok = _device.SetDeviceName(name);
                MessageBox.Show(ok ? "Device name updated." : "Failed to set device name.",
                    ok ? "Success" : "Error", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnFactoryResetClicked(object sender, EventArgs e)
        {
            if (_device == null) return;
            var r = MessageBox.Show(
                "⚠ WARNING ⚠\n\nThis will erase ALL device settings:\n" +
                "• Device name\n• I2C clock preference\n• All stored configuration\n\n" +
                "This CANNOT be undone. Continue?",
                "Factory Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (r != DialogResult.Yes) return;

            try
            {
                if (_device.FactoryReset())
                {
                    MessageBox.Show("Factory reset complete. Reconnect the device.", "Done",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    _disconnectHandler?.Invoke(_parent, EventArgs.Empty);
                    Close();
                }
                else
                {
                    MessageBox.Show("Factory reset failed.", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}