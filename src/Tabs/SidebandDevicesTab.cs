using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using UDFCore;

namespace UnifiedDDRFlasher
{
    public class SidebandDevicesTab : UserControl
    {
        #region Constants

        private const int READ_CHUNK_SIZE = 64;

        private const string TT_KIND = "Sideband device class. Counts show what the automatic detection found; RCD and CKD share the 0x58 address slot and the module type decides which one is fitted.";
        private const string TT_ADDRESS = "Bus address of the selected device, filled by the automatic detection.";
        private const string TT_DETECT = "Re-run the full detection: identify the module, then probe and verify every device class it can carry.";
        private const string TT_CHANNEL = "RCD subchannel the control words belong to (A or B).";
        private const string TT_PAGE = "RCD page mapped into the RW60-RW7F window. Page 3 holds the manufacturing IDs.";
        private const string TT_READ = "Read the device's register space into the hex viewer.";
        private const string TT_MORE_INFO = "Show the decoded register fields of the current dump: identity, configuration, thermal data and status bits.";
        private const string TT_OPEN_DUMP = "Load a register dump from a binary file and label it with the selected device class.";
        private const string TT_SAVE_DUMP = "Save the currently displayed register dump to a binary file.";
        private const string TT_READ_REG = "Read a single register by address (hex).";
        private const string TT_WRITE_REG = "Write a single byte to a register. Takes effect immediately on a live device.";

        #endregion

        #region Events

        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region Device kinds

        private enum DeviceKind
        {
            Rcd,
            Ckd,
            Ts0,
            Ts1,
            Hub
        }

        private static readonly string[] KindNames =
        {
            "RCD (registering clock driver)",
            "CKD (client clock driver)",
            "TS0 (thermal sensor 1)",
            "TS1 (thermal sensor 2)",
            "SPD hub registers"
        };

        private static readonly (byte start, byte end)[][] KindRanges =
        {
            new[] { ((byte)0x58, (byte)0x5F) },
            new[] { ((byte)0x58, (byte)0x5F), ((byte)0x20, (byte)0x27) },
            new[] { ((byte)0x10, (byte)0x17) },
            new[] { ((byte)0x30, (byte)0x37) },
            new[] { ((byte)0x50, (byte)0x57) },
        };

        private static readonly int[] KindDumpLength = { 128, 256, 256, 256, 128 };

        #endregion

        #region Private Fields

        private Func<UDFDevice> _deviceProvider;
        private UDFDevice Device => _deviceProvider?.Invoke();

        private HexEditorControl _hexEditor;
        private RichTextBox _responseLog;

        private Label _moduleLabel;
        private ComboBox _kindCombo;
        private ComboBox _addressCombo;
        private Button _detectButton;
        private Label _channelLabel;
        private ComboBox _channelCombo;
        private Label _pageLabel;
        private ComboBox _pageCombo;
        private Button _readButton;
        private Button _moreInfoButton;
        private Button _openDumpButton;
        private Button _saveDumpButton;

        private Label _deviceLabel;
        private Label _vendorLabel;
        private Label _revisionLabel;
        private Label _tempLabel;

        private TextBox _regAddressText;
        private TextBox _regValueText;
        private Button _readRegButton;
        private Button _writeRegButton;

        private byte[] _currentDump;
        private byte _currentAddress;
        private byte[] _rcdIdsWindow;
        private Dictionary<DeviceKind, List<byte>> _detected =
            Enum.GetValues(typeof(DeviceKind)).Cast<DeviceKind>().ToDictionary(k => k, k => new List<byte>());

        private bool _discoveryRunning;
        private bool _discoveryPending;
        private bool _suppressUiEvents;
        private string _lastScanSignature = "";

        private ToolTip _toolTip;

        #endregion

        #region Constructor

        public SidebandDevicesTab()
        {
            _toolTip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400, ReshowDelay = 200 };
            InitializeComponent();
            UpdateUIState(false);
        }

        #endregion

        #region UI construction

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.Padding = new Padding(6);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 170F));

            _hexEditor = new HexEditorControl
            {
                Dock = DockStyle.Fill,
                ReadOnly = true
            };
            left.Controls.Add(_hexEditor, 0, 0);

            var logGroup = MakeStyledGroup("Response Log");
            logGroup.Margin = new Padding(3, 8, 3, 3);
            _responseLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                Font = new Font("Consolas", 8.5F),
                BackColor = Color.White
            };
            logGroup.Controls.Add(_responseLog);
            left.Controls.Add(logGroup, 0, 1);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 196F));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 126F));

            right.Controls.Add(BuildDeviceGroup(), 0, 0);
            right.Controls.Add(BuildReadButtonPanel(), 0, 1);
            right.Controls.Add(BuildInfoGroup(), 0, 2);
            right.Controls.Add(BuildRegisterGroup(), 0, 3);

            root.Controls.Add(left, 0, 0);
            root.Controls.Add(right, 1, 0);
            this.Controls.Add(root);
        }

        private GroupBox BuildDeviceGroup()
        {
            var group = MakeStyledGroup("Device");
            var grid = MakePlainGrid(3, 4);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 4; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(MakeLabel("Module:"), 0, 0);
            _moduleLabel = MakeValueLabel();
            _moduleLabel.Text = "not detected";
            grid.Controls.Add(_moduleLabel, 1, 0);
            grid.SetColumnSpan(_moduleLabel, 2);

            grid.Controls.Add(MakeLabel("Class:"), 0, 1);
            _kindCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _kindCombo.Items.AddRange(KindNames);
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectedIndexChanged += OnKindChanged;
            _toolTip.SetToolTip(_kindCombo, TT_KIND);
            grid.Controls.Add(_kindCombo, 1, 1);
            grid.SetColumnSpan(_kindCombo, 2);

            grid.Controls.Add(MakeLabel("Address:"), 0, 2);
            _addressCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            _addressCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_addressCombo.SelectedItem is string sel && sel.StartsWith("0x"))
                    _currentAddress = Convert.ToByte(sel.Substring(2, 2), 16);
            };
            _toolTip.SetToolTip(_addressCombo, TT_ADDRESS);
            grid.Controls.Add(_addressCombo, 1, 2);

            _detectButton = new Button { Text = "Detect All", AutoSize = true };
            _detectButton.Click += (s, e) => RunDiscoveryFlow(autoRead: true, verbose: true);
            _toolTip.SetToolTip(_detectButton, TT_DETECT);
            grid.Controls.Add(_detectButton, 2, 2);

            _channelLabel = MakeLabel("Channel:");
            grid.Controls.Add(_channelLabel, 0, 3);
            var rcdRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            _channelCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 64 };
            _channelCombo.Items.AddRange(new object[] { "A", "B" });
            _channelCombo.SelectedIndex = 0;
            _toolTip.SetToolTip(_channelCombo, TT_CHANNEL);
            rcdRow.Controls.Add(_channelCombo);
            _pageLabel = new Label { Text = "Page:", AutoSize = true, Padding = new Padding(12, 6, 0, 0) };
            rcdRow.Controls.Add(_pageLabel);
            _pageCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 104 };
            _pageCombo.Items.AddRange(new object[] { "0 (DFE)", "1 (DFE)", "2 (DFE)", "3 (IDs)", "4 (VHost)" });
            _pageCombo.SelectedIndex = 3;
            _toolTip.SetToolTip(_pageCombo, TT_PAGE);
            rcdRow.Controls.Add(_pageCombo);
            grid.Controls.Add(rcdRow, 1, 3);
            grid.SetColumnSpan(rcdRow, 2);

            group.Controls.Add(grid);
            return group;
        }

        private Panel BuildReadButtonPanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3, 8, 3, 4) };
            _readButton = new Button
            {
                Text = "Read Device",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold)
            };
            _readButton.FlatAppearance.BorderSize = 0;
            _readButton.Click += async (s, e) => await ReadSelectedDeviceAsync();
            _toolTip.SetToolTip(_readButton, TT_READ);
            panel.Controls.Add(_readButton);
            return panel;
        }

        private GroupBox BuildInfoGroup()
        {
            var group = MakeStyledGroup("Device Info");
            var grid = MakePlainGrid(2, 5);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 4; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            grid.Controls.Add(MakeLabel("Device:"), 0, 0);
            _deviceLabel = MakeValueLabel();
            grid.Controls.Add(_deviceLabel, 1, 0);

            grid.Controls.Add(MakeLabel("Vendor:"), 0, 1);
            _vendorLabel = MakeValueLabel();
            grid.Controls.Add(_vendorLabel, 1, 1);

            grid.Controls.Add(MakeLabel("Revision:"), 0, 2);
            _revisionLabel = MakeValueLabel();
            grid.Controls.Add(_revisionLabel, 1, 2);

            grid.Controls.Add(MakeLabel("Temperature:"), 0, 3);
            _tempLabel = MakeValueLabel();
            grid.Controls.Add(_tempLabel, 1, 3);

            _moreInfoButton = new Button
            {
                Text = "More Info...",
                Dock = DockStyle.Bottom,
                Height = 30
            };
            _moreInfoButton.Click += OnMoreInfoClicked;
            _toolTip.SetToolTip(_moreInfoButton, TT_MORE_INFO);
            var buttonHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            buttonHost.Controls.Add(_moreInfoButton);
            grid.Controls.Add(buttonHost, 0, 4);
            grid.SetColumnSpan(buttonHost, 2);

            group.Controls.Add(grid);
            return group;
        }

        private GroupBox BuildRegisterGroup()
        {
            var group = MakeStyledGroup("Register / Dump");
            var grid = MakePlainGrid(1, 2);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var regRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            regRow.Controls.Add(new Label { Text = "Reg 0x", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            _regAddressText = new TextBox { Width = 38, MaxLength = 2, CharacterCasing = CharacterCasing.Upper };
            regRow.Controls.Add(_regAddressText);
            regRow.Controls.Add(new Label { Text = "Value 0x", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            _regValueText = new TextBox { Width = 38, MaxLength = 2, CharacterCasing = CharacterCasing.Upper };
            regRow.Controls.Add(_regValueText);
            _readRegButton = new Button { Text = "Read", AutoSize = true };
            _readRegButton.Click += OnReadRegClicked;
            _toolTip.SetToolTip(_readRegButton, TT_READ_REG);
            regRow.Controls.Add(_readRegButton);
            _writeRegButton = new Button { Text = "Write", AutoSize = true };
            _writeRegButton.Click += OnWriteRegClicked;
            _toolTip.SetToolTip(_writeRegButton, TT_WRITE_REG);
            regRow.Controls.Add(_writeRegButton);
            grid.Controls.Add(regRow, 0, 0);

            var dumpRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
            _openDumpButton = new Button { Text = "Open Dump", AutoSize = true };
            _openDumpButton.Click += OnOpenDumpClicked;
            _toolTip.SetToolTip(_openDumpButton, TT_OPEN_DUMP);
            dumpRow.Controls.Add(_openDumpButton);
            _saveDumpButton = new Button { Text = "Save Dump", AutoSize = true };
            _saveDumpButton.Click += OnSaveDumpClicked;
            _toolTip.SetToolTip(_saveDumpButton, TT_SAVE_DUMP);
            dumpRow.Controls.Add(_saveDumpButton);
            grid.Controls.Add(dumpRow, 0, 1);

            group.Controls.Add(grid);
            return group;
        }

        private static GroupBox MakeStyledGroup(string title) => new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 78, 152),
            Padding = new Padding(8)
        };

        private static TableLayoutPanel MakePlainGrid(int cols, int rows) => new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = cols,
            RowCount = rows,
            Font = new Font("Segoe UI", 9F),
            ForeColor = SystemColors.ControlText
        };

        private static Label MakeLabel(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 6, 4, 0)
        };

        private static Label MakeValueLabel() => new Label
        {
            Text = "-",
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Padding = new Padding(0, 6, 0, 0)
        };

        #endregion

        #region Lifecycle plumbing

        public void LogResponse(string message, string level = "Info")
        {
            if (InvokeRequired) { Invoke(new Action(() => LogResponse(message, level))); return; }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _responseLog.SelectionColor = level switch
            {
                "Err" => Color.DarkRed,
                "Warn" => Color.OrangeRed,
                _ => Color.Black
            };
            _responseLog.SelectionBackColor = level == "Err"
                ? Color.FromArgb(255, 240, 240)
                : _responseLog.BackColor;

            _responseLog.AppendText($"[{timestamp}] {message}\n");
            _responseLog.ScrollToCaret();
        }

        public void SetDeviceProvider(Func<UDFDevice> deviceProvider) =>
            _deviceProvider = deviceProvider;

        public void OnDeviceConnected(UDFDevice device)
        {
            if (InvokeRequired) { Invoke(new Action(() => OnDeviceConnected(device))); return; }
            UpdateUIState(true);
            LogResponse("Device connected, detecting sideband devices…");
            RunDiscoveryFlow(autoRead: true, verbose: true);
        }

        public void OnDeviceDisconnected()
        {
            if (InvokeRequired) { Invoke(new Action(OnDeviceDisconnected)); return; }

            foreach (var list in _detected.Values) list.Clear();
            _addressCombo.Items.Clear();
            _currentDump = null;
            _rcdIdsWindow = null;
            _lastScanSignature = "";
            _hexEditor.ClearFieldHighlights();
            _hexEditor.SetData(Array.Empty<byte>());
            _moduleLabel.Text = "not detected";
            RebuildKindCombo();
            ResetInfoLabels();
            LogResponse("Device disconnected.");
            UpdateUIState(false);
        }

        public void UpdateDetectedDevices(List<byte> devices)
        {
            if (InvokeRequired) { Invoke(new Action(() => UpdateDetectedDevices(devices))); return; }
            if (Device == null) return;

            var relevant = devices
                .Where(a => KindRanges.SelectMany(k => k).Any(rng => a >= rng.start && a <= rng.end))
                .OrderBy(a => a);
            string signature = string.Join(",", relevant.Select(a => a.ToString("X2")));
            if (signature == _lastScanSignature) return;
            _lastScanSignature = signature;
            RunDiscoveryFlow(autoRead: false, verbose: false);
        }

        private void ResetInfoLabels()
        {
            _deviceLabel.Text = "-";
            _vendorLabel.Text = "-";
            _revisionLabel.Text = "-";
            _tempLabel.Text = "-";
        }

        private DeviceKind SelectedKind => (DeviceKind)Math.Max(0, _kindCombo.SelectedIndex);

        private void UpdateUIState(bool connected)
        {
            bool hasAddress = _addressCombo.Items.Count > 0;
            bool isRcd = SelectedKind == DeviceKind.Rcd;

            _detectButton.Enabled = connected && !_discoveryRunning;
            _readButton.Enabled = connected && hasAddress;
            _readRegButton.Enabled = connected && hasAddress;
            _writeRegButton.Enabled = connected && hasAddress;
            _saveDumpButton.Enabled = _currentDump != null;
            _moreInfoButton.Enabled = _currentDump != null;
            _openDumpButton.Enabled = true;

            _channelLabel.Visible = isRcd;
            _channelCombo.Visible = isRcd;
            _pageLabel.Visible = isRcd;
            _pageCombo.Visible = isRcd;
        }

        private void OnKindChanged(object sender, EventArgs e)
        {
            if (_suppressUiEvents) return;
            _currentDump = null;
            _rcdIdsWindow = null;
            _hexEditor.ClearFieldHighlights();
            _hexEditor.SetData(Array.Empty<byte>());
            ResetInfoLabels();
            RefreshAddressCombo();
        }

        private void RebuildKindCombo()
        {
            _suppressUiEvents = true;
            try
            {
                int keep = Math.Max(0, _kindCombo.SelectedIndex);
                _kindCombo.Items.Clear();
                foreach (DeviceKind k in Enum.GetValues(typeof(DeviceKind)))
                {
                    int n = _detected[k].Count;
                    _kindCombo.Items.Add(n > 0 ? $"{KindNames[(int)k]}  [{n}]" : KindNames[(int)k]);
                }
                _kindCombo.SelectedIndex = keep;
            }
            finally
            {
                _suppressUiEvents = false;
            }
        }

        private void RefreshAddressCombo()
        {
            var kind = SelectedKind;
            _addressCombo.Items.Clear();
            foreach (var a in _detected[kind])
                _addressCombo.Items.Add($"0x{a:X2}");
            if (_addressCombo.Items.Count > 0)
                _addressCombo.SelectedIndex = 0;
            UpdateUIState(Device != null);
        }

        #endregion

        #region Discovery

        private sealed class DiscoveryResult
        {
            public List<byte>[] Found;
            public List<byte> LegacyEeproms = new List<byte>();
            public List<string> ModuleSummaries = new List<string>();
            public List<string> Notes = new List<string>();
        }

        private void RunDiscoveryFlow(bool autoRead, bool verbose)
        {
            var device = Device;
            if (device == null) return;
            if (_discoveryRunning) { _discoveryPending = true; return; }
            _discoveryRunning = true;
            UpdateUIState(true);

            Task.Run(() =>
            {
                DiscoveryResult result = null;
                string error = null;
                try { result = RunDiscovery(device); }
                catch (Exception ex) { error = ex.Message; }

                if (IsDisposed) return;
                BeginInvoke(new Action(() =>
                {
                    _discoveryRunning = false;
                    if (error != null)
                    {
                        LogResponse($"✗ Detection failed: {error}", "Err");
                    }
                    else
                    {
                        ApplyDiscovery(result, autoRead, verbose);
                    }
                    UpdateUIState(Device != null);
                    if (_discoveryPending)
                    {
                        _discoveryPending = false;
                        RunDiscoveryFlow(autoRead: false, verbose: false);
                    }
                }));
            });
        }

        private DiscoveryResult RunDiscovery(UDFDevice device)
        {
            var r = new DiscoveryResult
            {
                Found = Enumerable.Range(0, 5).Select(_ => new List<byte>()).ToArray()
            };

            for (byte a = 0x50; a <= 0x57; a++)
            {
                bool ack;
                try { ack = device.ProbeAddress(a); }
                catch { continue; }
                if (!ack) continue;

                var id = TryReadBytes(device, a, 0x00, 2);
                if (!IsHubId(id))
                {
                    TryReadSpd(device, a, 0, 1);
                    id = TryReadBytes(device, a, 0x00, 2);
                }
                if (IsHubId(id))
                    r.Found[(int)DeviceKind.Hub].Add(a);
                else
                    r.LegacyEeproms.Add(a);
            }

            foreach (byte hub in r.Found[(int)DeviceKind.Hub])
            {
                int hid = hub - 0x50;
                byte gen = 0, type = 0;
                var spd = TryReadSpd(device, hub, 2, 2);
                if (spd != null) { gen = spd[0]; type = spd[1]; }

                string genName = GenerationName(gen);
                string typeName = ModuleTypeName(type);
                r.ModuleSummaries.Add($"{genName ?? "unknown"} {typeName} (hub 0x{hub:X2})");

                bool ddr5Family = gen == 0x12 || gen == 0x14 || gen == 0x13 || gen == 0x15;
                if (!ddr5Family)
                {
                    r.Notes.Add($"Hub 0x{hub:X2} carries an unreadable or unknown SPD; skipping its local devices.");
                    continue;
                }

                byte clockAddr = (byte)(0x58 + hid);
                int nib = type & 0x0F;
                if (nib == 0x1 || nib == 0x4 || nib == 0x7)
                {
                    if (SafeProbe(device, clockAddr)) r.Found[(int)DeviceKind.Rcd].Add(clockAddr);
                }
                else if (nib == 0x5 || nib == 0x6)
                {
                    if (SafeProbe(device, clockAddr)) r.Found[(int)DeviceKind.Ckd].Add(clockAddr);
                }
                else if (nib == 0x8 || nib == 0x9)
                {
                    if (SafeProbe(device, clockAddr)) r.Found[(int)DeviceKind.Ckd].Add(clockAddr);
                    byte alt = (byte)(0x20 + hid);
                    if (SafeProbe(device, alt)) r.Found[(int)DeviceKind.Ckd].Add(alt);
                }
                else if (nib == 0x2 || nib == 0x3 || nib == 0xB)
                {
                }
                else
                {
                    if (SafeProbe(device, clockAddr))
                    {
                        r.Found[(int)DeviceKind.Rcd].Add(clockAddr);
                        r.Found[(int)DeviceKind.Ckd].Add(clockAddr);
                        r.Notes.Add($"Module type 0x{type:X2} is unknown; 0x{clockAddr:X2} listed as both RCD and CKD.");
                    }
                }

                byte ts0 = (byte)(0x10 + hid);
                byte ts1 = (byte)(0x30 + hid);
                if (SafeProbe(device, ts0) && IsThermalSensor(device, ts0))
                    r.Found[(int)DeviceKind.Ts0].Add(ts0);
                if (SafeProbe(device, ts1) && IsThermalSensor(device, ts1))
                    r.Found[(int)DeviceKind.Ts1].Add(ts1);
            }

            if (r.Found[(int)DeviceKind.Hub].Count == 0 && r.LegacyEeproms.Count > 0)
            {
                byte eeprom = r.LegacyEeproms[0];
                var spd = TryReadSpd(device, eeprom, 2, 1);
                string genName = spd != null ? GenerationName(spd[0]) : null;
                r.ModuleSummaries.Add($"{genName ?? "Legacy"} module, SPD EEPROM at 0x{eeprom:X2} (no hub)");
                r.Notes.Add("Legacy module: clock driver and thermal sensor ranges are not probed, the SPD write protect controls live there.");
            }

            return r;
        }

        private static bool SafeProbe(UDFDevice device, byte address)
        {
            try { return device.ProbeAddress(address); }
            catch { return false; }
        }

        private static byte[] TryReadBytes(UDFDevice device, byte address, byte offset, byte length)
        {
            try
            {
                var b = device.ReadRawBytes(address, offset, length);
                return (b != null && b.Length == length) ? b : null;
            }
            catch { return null; }
        }

        private static byte[] TryReadSpd(UDFDevice device, byte address, ushort offset, byte length)
        {
            try
            {
                var b = device.ReadSPD(address, offset, length);
                return (b != null && b.Length == length) ? b : null;
            }
            catch { return null; }
        }

        private static bool IsHubId(byte[] id) =>
            id != null && id[0] == 0x51 && (id[1] == 0x18 || id[1] == 0x08);

        private static bool IsThermalSensor(UDFDevice device, byte address)
        {
            var id = TryReadBytes(device, address, 0x00, 2);
            return id != null
                && (id[0] == 0x51 || id[0] == 0x52)
                && (id[1] == 0x10 || id[1] == 0x11);
        }

        private static string GenerationName(byte gen) => gen switch
        {
            0x12 => "DDR5",
            0x14 => "DDR5",
            0x13 => "LPDDR5",
            0x15 => "LPDDR5",
            0x0C => "DDR4",
            0x0E => "DDR4",
            0x0B => "DDR3",
            0x00 => null,
            _ => $"unknown (0x{gen:X2})"
        };

        private static string ModuleTypeName(byte type) => (type & 0x0F) switch
        {
            0x1 => "RDIMM",
            0x2 => "UDIMM",
            0x3 => "SODIMM",
            0x4 => "LRDIMM",
            0x5 => "CUDIMM",
            0x6 => "CSODIMM",
            0x7 => "MRDIMM",
            0x8 => "CAMM2",
            0x9 => "SOCAMM2",
            0xA => "DDIMM",
            0xB => "soldered",
            _ => $"type 0x{type:X2}"
        };

        private void ApplyDiscovery(DiscoveryResult r, bool autoRead, bool verbose)
        {
            bool changed = false;
            foreach (DeviceKind k in Enum.GetValues(typeof(DeviceKind)))
            {
                if (!r.Found[(int)k].SequenceEqual(_detected[k]))
                {
                    _detected[k] = r.Found[(int)k];
                    changed = true;
                }
            }

            _moduleLabel.Text = r.ModuleSummaries.Count > 0
                ? string.Join("; ", r.ModuleSummaries)
                : "no module detected";

            if (!changed && !verbose) return;

            if (verbose)
            {
                foreach (var s in r.ModuleSummaries) LogResponse($"Module: {s}");
                if (r.ModuleSummaries.Count == 0) LogResponse("No module detected.", "Warn");
                foreach (DeviceKind k in Enum.GetValues(typeof(DeviceKind)))
                {
                    var list = _detected[k];
                    if (list.Count > 0)
                        LogResponse($"✓ {KindNames[(int)k]}: {string.Join(", ", list.Select(a => $"0x{a:X2}"))}");
                }
                foreach (var n in r.Notes) LogResponse(n, "Warn");
            }

            RebuildKindCombo();

            if (_detected[SelectedKind].Count == 0)
            {
                foreach (var k in new[] { DeviceKind.Rcd, DeviceKind.Ckd, DeviceKind.Hub, DeviceKind.Ts0, DeviceKind.Ts1 })
                {
                    if (_detected[k].Count > 0)
                    {
                        _suppressUiEvents = true;
                        _kindCombo.SelectedIndex = (int)k;
                        _suppressUiEvents = false;
                        _currentDump = null;
                        _rcdIdsWindow = null;
                        _hexEditor.ClearFieldHighlights();
                        _hexEditor.SetData(Array.Empty<byte>());
                        ResetInfoLabels();
                        break;
                    }
                }
            }
            RefreshAddressCombo();

            if (autoRead && _addressCombo.Items.Count > 0)
            {
                var ignored = ReadSelectedDeviceAsync();
            }
        }

        #endregion

        #region Read device

        private async Task ReadSelectedDeviceAsync()
        {
            var device = Device;
            if (device == null || _addressCombo.SelectedIndex < 0) return;

            var kind = SelectedKind;
            byte address = _currentAddress;
            byte channel = (byte)Math.Max(0, _channelCombo.SelectedIndex);
            byte page = (byte)Math.Max(0, _pageCombo.SelectedIndex);
            int length = KindDumpLength[(int)kind];

            SetBusy(true);
            try
            {
                LogResponse($"Reading {length} bytes from 0x{address:X2}…");
                byte[] idsWindow = null;
                var dump = await Task.Run(() =>
                {
                    var d = ReadDeviceDump(device, kind, address, channel, page, length);
                    if (d != null && kind == DeviceKind.Rcd)
                        idsWindow = FetchRcdIdsWindow(device, address, channel, page, d);
                    return d;
                }).ConfigureAwait(true);
                if (IsDisposed) return;

                if (dump == null)
                {
                    LogResponse("✗ Read failed; the device stopped answering.", "Err");
                    ErrorOccurred?.Invoke(this, "Sideband device read failed.");
                    return;
                }

                _currentDump = dump;
                _rcdIdsWindow = idsWindow;
                DisplayDump(kind, page, dump);
                UpdateInfoLabels(kind, dump);
                LogResponse("✓ Read complete");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private byte[] ReadDeviceDump(UDFDevice device, DeviceKind kind, byte address, byte channel, byte page, int length)
        {
            var dump = new byte[length];
            if (kind == DeviceKind.Rcd)
            {
                for (int reg = 0; reg < length; reg += 4)
                {
                    byte regPage = reg >= 0x60 ? page : (byte)0;
                    var dword = device.RcdReadDword(address, channel, regPage, (byte)reg);
                    if (dword == null) return null;
                    Array.Copy(dword, 0, dump, reg, 4);
                }
                return dump;
            }

            if (kind == DeviceKind.Hub)
                NormalizeHubPagePointer(device, address);

            bool partial = false;
            for (int offset = 0; offset < length; offset += READ_CHUNK_SIZE)
            {
                var chunk = device.ReadRawBytes(address, (byte)offset, READ_CHUNK_SIZE);
                if (chunk == null || chunk.Length != READ_CHUNK_SIZE)
                {
                    for (int i = 0; i < READ_CHUNK_SIZE; i++) dump[offset + i] = 0xFF;
                    partial = true;
                    continue;
                }
                Array.Copy(chunk, 0, dump, offset, READ_CHUNK_SIZE);
            }
            if (partial)
                LogResponse("Some register ranges did not answer; gaps shown as FF.", "Warn");
            return dump;
        }

        private void NormalizeHubPagePointer(UDFDevice device, byte address)
        {
            try
            {
                var pp = device.ReadRawBytes(address, 0x0B, 1);
                if (pp == null || pp.Length != 1 || pp[0] == 0) return;
                if (device.WriteI2CDevice(address, 0x0B, 0x00))
                    LogResponse($"Hub page pointer was {pp[0] & 0x07}, reset to 0 for the register view.");
            }
            catch { }
        }

        private byte[] FetchRcdIdsWindow(UDFDevice device, byte address, byte channel, byte page, byte[] dump)
        {
            var ids = new byte[16];
            if (page == 3)
            {
                Array.Copy(dump, 0x60, ids, 0, 16);
                return ids;
            }
            for (int i = 0; i < 16; i += 4)
            {
                var dword = device.RcdReadDword(address, channel, 3, (byte)(0x60 + i));
                if (dword == null) return null;
                Array.Copy(dword, 0, ids, i, 4);
            }
            return ids;
        }

        private void DisplayDump(DeviceKind kind, byte page, byte[] dump)
        {
            _hexEditor.SetData(dump);
            ApplyHexFieldHighlights(kind, page, dump);
            UpdateUIState(Device != null);
        }

        private void ApplyHexFieldHighlights(DeviceKind kind, byte page, byte[] data)
        {
            _hexEditor.ClearFieldHighlights();
            if (data == null || data.Length < 4) return;

            Color[] palette = FieldMaps.HighlightPalette;
            int colorIdx = 0;

            void Add(int start, int len, string label)
            {
                if (start < 0 || start >= data.Length) return;
                if (start + len > data.Length) len = data.Length - start;
                if (len < 1) return;
                Color c = palette[colorIdx % palette.Length];
                colorIdx++;
                _hexEditor.AddFieldHighlight(start, len, c, label);
            }

            switch (kind)
            {
                case DeviceKind.Rcd: FieldMaps.AddRcdFields(page, Add); break;
                case DeviceKind.Ckd: FieldMaps.AddCkdFields(Add); break;
                case DeviceKind.Ts0:
                case DeviceKind.Ts1: FieldMaps.AddTsFields(Add); break;
                case DeviceKind.Hub: FieldMaps.AddSpd5HubFields(Add); break;
            }
        }

        #endregion

        #region Info labels

        private void UpdateInfoLabels(DeviceKind kind, byte[] dump)
        {
            switch (kind)
            {
                case DeviceKind.Hub:
                    {
                        _deviceLabel.Text = HubName(dump[0x00], dump[0x01]);
                        _vendorLabel.Text = SPDParsedFields.LookupManufacturer(dump[0x03], dump[0x04]);
                        _revisionLabel.Text = RevisionText(dump[0x02]);
                        bool hasTs = (dump[0x05] & 0x02) != 0;
                        _tempLabel.Text = hasTs ? FormatThermalPair(dump[0x31], dump[0x32]) : "sensor not fitted";
                        break;
                    }
                case DeviceKind.Ts0:
                case DeviceKind.Ts1:
                    {
                        _deviceLabel.Text = ThermalSensorName(dump[0x00], dump[0x01]);
                        _vendorLabel.Text = SPDParsedFields.LookupManufacturer(dump[0x03], dump[0x04]);
                        _revisionLabel.Text = RevisionText(dump[0x02]);
                        _tempLabel.Text = FormatThermalPair(dump[0x31], dump[0x32]);
                        break;
                    }
                case DeviceKind.Ckd:
                    {
                        int devId = (dump[0x4D] << 8) | dump[0x4C];
                        _deviceLabel.Text = $"CKD, ID 0x{devId:X4}";
                        _vendorLabel.Text = SPDParsedFields.LookupManufacturer(dump[0x4A], dump[0x4B]);
                        _revisionLabel.Text = $"0x{dump[0x4E]:X2}";
                        _tempLabel.Text = "n/a";
                        break;
                    }
                case DeviceKind.Rcd:
                    {
                        if (_rcdIdsWindow == null) { ResetInfoLabels(); _tempLabel.Text = "n/a"; break; }
                        int devId = (_rcdIdsWindow[0x0D] << 8) | _rcdIdsWindow[0x0C];
                        _deviceLabel.Text = $"RCD, ID 0x{devId:X4}";
                        _vendorLabel.Text = SPDParsedFields.LookupManufacturer(_rcdIdsWindow[0x0A], _rcdIdsWindow[0x0B]);
                        _revisionLabel.Text = $"0x{_rcdIdsWindow[0x0E]:X2}";
                        _tempLabel.Text = "n/a";
                        break;
                    }
            }
        }

        private static string HubName(byte msb, byte lsb)
        {
            if (msb != 0x51) return $"0x{(msb << 8) | lsb:X4}";
            return lsb == 0x08 ? "SPD5108 hub" : lsb == 0x18 ? "SPD5118 hub" : $"hub 0x51{lsb:X2}";
        }

        private static string ThermalSensorName(byte msb, byte lsb)
        {
            string family = msb == 0x52 ? "TS521x" : msb == 0x51 ? "TS511x" : $"0x{msb:X2}xx";
            string grade = lsb == 0x11 ? "grade A" : lsb == 0x10 ? "grade B" : $"type 0x{lsb:X2}";
            return $"{family}, {grade}";
        }

        private static string RevisionText(byte mr2) =>
            $"{(mr2 >> 4) & 0x03}.{mr2 & 0x0F}";

        private static string FormatThermalPair(byte low, byte high)
        {
            int raw = ((high & 0x1F) << 6) | (low >> 2);
            if ((raw & 0x400) != 0) raw -= 0x800;
            return $"{raw * 0.25:0.00} °C";
        }

        #endregion

        #region More info

        private void OnMoreInfoClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                MessageBox.Show("No register dump loaded. Read a device first.", "Device Info",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var kind = SelectedKind;
            string caption = $"{KindNames[(int)kind]} at 0x{_currentAddress:X2}";
            using (var dlg = new SidebandInfoDialog(caption, BuildInfoRows(kind, _currentDump)))
            {
                dlg.ShowDialog(this);
            }
        }

        private List<(string Name, string Value)> BuildInfoRows(DeviceKind kind, byte[] d)
        {
            var rows = new List<(string, string)>();
            void H(string title) => rows.Add((null, title));
            void R(string n, string v) => rows.Add((n, v));

            switch (kind)
            {
                case DeviceKind.Hub:
                    {
                        H("Identity");
                        R("Device", HubName(d[0x00], d[0x01]));
                        R("Revision", RevisionText(d[0x02]));
                        R("Vendor", SPDParsedFields.LookupManufacturer(d[0x03], d[0x04]));
                        H("Capability");
                        R("Hub function", (d[0x05] & 0x01) != 0 ? "supported" : "not supported");
                        R("Temperature sensor", (d[0x05] & 0x02) != 0 ? "fitted" : "not fitted");
                        R("Write recovery capability", $"0x{d[0x06]:X2}");
                        H("Configuration");
                        R("Legacy mode / NVM page (MR11)", $"0x{d[0x0B]:X2} (page {d[0x0B] & 0x07})");
                        R("IO config (MR14)", $"0x{d[0x0E]:X2}");
                        R("Device config (MR18)", $"0x{d[0x12]:X2}");
                        R("Protected NVM blocks", ProtectedBlocksText(d[0x0C], d[0x0D]));
                        AppendThermalRows(rows, d, includeSensor: (d[0x05] & 0x02) != 0);
                        H("Status");
                        R("Device status (MR48)", DeviceStatusText(d[0x30]));
                        R("Temp status (MR51)", TempStatusText(d[0x33]));
                        R("Errors (MR52)", HubErrorText(d[0x34]));
                        break;
                    }
                case DeviceKind.Ts0:
                case DeviceKind.Ts1:
                    {
                        H("Identity");
                        R("Device", ThermalSensorName(d[0x00], d[0x01]));
                        R("Revision", RevisionText(d[0x02]));
                        R("Vendor", SPDParsedFields.LookupManufacturer(d[0x03], d[0x04]));
                        R("HID (MR7)", $"{d[0x07] & 0x07}");
                        if (d[0x00] == 0x52)
                            R("Serial number", string.Join(" ", d.Skip(0x50).Take(5).Select(b => b.ToString("X2"))));
                        H("Configuration");
                        R("Device config (MR18)", $"0x{d[0x12]:X2}");
                        R("Sensor config (MR26)", $"0x{d[0x1A]:X2}");
                        R("Interrupt config (MR27)", $"0x{d[0x1B]:X2}");
                        AppendThermalRows(rows, d, includeSensor: true);
                        H("Status");
                        R("Device status (MR48)", DeviceStatusText(d[0x30]));
                        R("Temp status (MR51)", TempStatusText(d[0x33]));
                        R("Errors (MR52)", BusErrorText(d[0x34]));
                        break;
                    }
                case DeviceKind.Ckd:
                    {
                        H("Identity");
                        R("Device ID", $"0x{(d[0x4D] << 8) | d[0x4C]:X4}");
                        R("Revision", $"0x{d[0x4E]:X2}");
                        R("Vendor", SPDParsedFields.LookupManufacturer(d[0x4A], d[0x4B]));
                        R("Date code", string.Join(" ", d.Skip(0x40).Take(3).Select(b => b.ToString("X2"))));
                        R("Unit code", string.Join(" ", d.Skip(0x43).Take(7).Select(b => b.ToString("X2"))));
                        H("Configuration (RW00)");
                        R("PLL mode", (d[0x00] & 0x03) switch
                        {
                            0 => "bypass",
                            1 => "single PLL",
                            2 => "dual PLL",
                            _ => "reserved"
                        });
                        R("Input clock termination", ((d[0x00] >> 2) & 0x03) switch
                        {
                            0 => "80 Ohm",
                            1 => "60 Ohm",
                            2 => "120 Ohm",
                            _ => "disabled"
                        });
                        R("QCK0_A output", (d[0x00] & 0x10) != 0 ? "disabled" : "enabled");
                        R("QCK1_A output", (d[0x00] & 0x20) != 0 ? "disabled" : "enabled");
                        R("QCK0_B output", (d[0x00] & 0x40) != 0 ? "disabled" : "enabled");
                        R("QCK1_B output", (d[0x00] & 0x80) != 0 ? "disabled" : "enabled");
                        H("Output controls");
                        R("Output delay enable (RW01)", $"0x{d[0x01]:X2}");
                        R("QCK driver (RW02)", $"0x{d[0x02]:X2}");
                        R("QCK slew rate (RW03)", $"0x{d[0x03]:X2}");
                        R("QCK0-QCK3 delay (RW04-07)", string.Join(" ", d.Skip(0x04).Take(4).Select(b => b.ToString("X2"))));
                        H("Status");
                        R("Bus errors (RW28)", BusErrorText(d[0x28]));
                        break;
                    }
                case DeviceKind.Rcd:
                    {
                        H("Identity (page 3)");
                        if (_rcdIdsWindow != null)
                        {
                            R("Device ID", $"0x{(_rcdIdsWindow[0x0D] << 8) | _rcdIdsWindow[0x0C]:X4}");
                            R("Revision", $"0x{_rcdIdsWindow[0x0E]:X2}");
                            R("Vendor", SPDParsedFields.LookupManufacturer(_rcdIdsWindow[0x0A], _rcdIdsWindow[0x0B]));
                            R("Date code", string.Join(" ", _rcdIdsWindow.Take(3).Select(b => b.ToString("X2"))));
                            R("Unit code", string.Join(" ", _rcdIdsWindow.Skip(3).Take(7).Select(b => b.ToString("X2"))));
                        }
                        else
                        {
                            R("Identity", "not available (file dump or read failure)");
                        }
                        H("Key control words");
                        R("Channel shown", _channelCombo.SelectedIndex == 1 ? "B" : "A");
                        R("Global features (RW00)", $"0x{d[0x00]:X2}");
                        R("Operating speed (RW05/RW06)", $"0x{d[0x05]:X2} / 0x{d[0x06]:X2}");
                        R("Clock enables (RW08)", $"0x{d[0x08]:X2}");
                        R("Output enables (RW09)", $"0x{d[0x09]:X2}");
                        R("Sideband control (RW25)", $"0x{d[0x25]:X2}");
                        H("Status");
                        R("Bus errors (RW28)", BusErrorText(d[0x28]));
                        break;
                    }
            }
            return rows;
        }

        private void AppendThermalRows(List<(string, string)> rows, byte[] d, bool includeSensor)
        {
            rows.Add((null, "Thermal"));
            if (!includeSensor)
            {
                rows.Add(("Sensor", "not fitted"));
                return;
            }
            rows.Add(("Current temperature", FormatThermalPair(d[0x31], d[0x32])));
            rows.Add(("High limit", FormatThermalPair(d[0x1C], d[0x1D])));
            rows.Add(("Low limit", FormatThermalPair(d[0x1E], d[0x1F])));
            rows.Add(("Critical high limit", FormatThermalPair(d[0x20], d[0x21])));
            rows.Add(("Critical low limit", FormatThermalPair(d[0x22], d[0x23])));
        }

        private static string ProtectedBlocksText(byte mr12, byte mr13)
        {
            var blocks = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                if ((mr12 & (1 << i)) != 0) blocks.Add(i);
                if ((mr13 & (1 << i)) != 0) blocks.Add(i + 8);
            }
            return blocks.Count == 0 ? "none" : string.Join(", ", blocks);
        }

        private static string DeviceStatusText(byte mr48)
        {
            var bits = new List<string>();
            if ((mr48 & 0x80) != 0) bits.Add("interrupt pending");
            if ((mr48 & 0x08) != 0) bits.Add("internal write in progress");
            if ((mr48 & 0x04) != 0) bits.Add("write protect override allowed");
            return bits.Count == 0 ? $"idle (0x{mr48:X2})" : $"{string.Join(", ", bits)} (0x{mr48:X2})";
        }

        private static string TempStatusText(byte mr51)
        {
            var bits = new List<string>();
            if ((mr51 & 0x01) != 0) bits.Add("above high limit");
            if ((mr51 & 0x02) != 0) bits.Add("below low limit");
            if ((mr51 & 0x04) != 0) bits.Add("above critical high");
            if ((mr51 & 0x08) != 0) bits.Add("below critical low");
            return bits.Count == 0 ? "in range" : string.Join(", ", bits);
        }

        private static string HubErrorText(byte mr52)
        {
            var bits = new List<string>();
            if ((mr52 & 0x80) != 0) bits.Add("access while busy");
            if ((mr52 & 0x40) != 0) bits.Add("write to protected NVM block");
            if ((mr52 & 0x20) != 0) bits.Add("write to protection registers");
            if ((mr52 & 0x02) != 0) bits.Add("PEC error");
            if ((mr52 & 0x01) != 0) bits.Add("parity error");
            return bits.Count == 0 ? "none" : string.Join(", ", bits);
        }

        private static string BusErrorText(byte reg)
        {
            var bits = new List<string>();
            if ((reg & 0x01) != 0) bits.Add("parity error");
            if ((reg & 0x02) != 0) bits.Add("PEC error");
            if ((reg & 0x80) != 0) bits.Add("interrupt pending");
            return bits.Count == 0 ? "none" : string.Join(", ", bits);
        }

        #endregion

        #region Single register access

        private bool TryParseHexByte(TextBox box, string what, out byte value)
        {
            value = 0;
            try
            {
                value = Convert.ToByte(box.Text.Trim(), 16);
                return true;
            }
            catch
            {
                LogResponse($"Invalid {what} '{box.Text}'; expected hex 00-FF.", "Warn");
                return false;
            }
        }

        private async void OnReadRegClicked(object sender, EventArgs e)
        {
            var device = Device;
            if (device == null) return;
            if (!TryParseHexByte(_regAddressText, "register", out byte reg)) return;

            var kind = SelectedKind;
            byte address = _currentAddress;
            byte channel = (byte)Math.Max(0, _channelCombo.SelectedIndex);
            byte page = (byte)Math.Max(0, _pageCombo.SelectedIndex);

            SetBusy(true);
            try
            {
                byte? value = await Task.Run(() =>
                {
                    if (kind == DeviceKind.Rcd)
                    {
                        byte regPage = reg >= 0x60 ? page : (byte)0;
                        var dword = device.RcdReadDword(address, channel, regPage, reg);
                        return dword == null ? (byte?)null : dword[reg & 0x03];
                    }
                    if (kind == DeviceKind.Hub)
                        NormalizeHubPagePointer(device, address);
                    var b = device.ReadRawBytes(address, reg, 1);
                    return (b != null && b.Length == 1) ? (byte?)b[0] : null;
                }).ConfigureAwait(true);
                if (IsDisposed) return;

                if (value == null)
                {
                    LogResponse($"✗ Read of reg 0x{reg:X2} failed", "Err");
                }
                else
                {
                    _regValueText.Text = $"{value:X2}";
                    LogResponse($"Reg 0x{reg:X2} = 0x{value:X2}");
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void OnWriteRegClicked(object sender, EventArgs e)
        {
            var device = Device;
            if (device == null) return;
            if (!TryParseHexByte(_regAddressText, "register", out byte reg)) return;
            if (!TryParseHexByte(_regValueText, "value", out byte value)) return;

            var kind = SelectedKind;
            byte address = _currentAddress;
            byte channel = (byte)Math.Max(0, _channelCombo.SelectedIndex);
            byte page = (byte)Math.Max(0, _pageCombo.SelectedIndex);

            SetBusy(true);
            try
            {
                bool ok = await Task.Run(() =>
                {
                    if (kind == DeviceKind.Rcd)
                    {
                        byte regPage = reg >= 0x60 ? page : (byte)0;
                        return device.RcdWriteRegister(address, channel, regPage, reg, value);
                    }
                    return device.WriteI2CDevice(address, reg, value);
                }).ConfigureAwait(true);
                if (IsDisposed) return;

                if (ok)
                {
                    LogResponse($"✓ Reg 0x{reg:X2} = 0x{value:X2} written");
                    if (_currentDump != null && reg < _currentDump.Length)
                    {
                        _currentDump[reg] = value;
                        DisplayDump(kind, page, _currentDump);
                    }
                }
                else
                {
                    LogResponse($"✗ Write to reg 0x{reg:X2} failed", "Err");
                    ErrorOccurred?.Invoke(this, "Sideband register write failed.");
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        #endregion

        #region Dump files

        private void OnOpenDumpClicked(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog { Filter = "Binary dump (*.bin)|*.bin|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var kind = SelectedKind;
                    int length = KindDumpLength[(int)kind];
                    var raw = File.ReadAllBytes(dlg.FileName);
                    var dump = new byte[length];
                    Array.Copy(raw, dump, Math.Min(raw.Length, length));
                    _currentDump = dump;
                    _rcdIdsWindow = null;
                    DisplayDump(kind, (byte)Math.Max(0, _pageCombo.SelectedIndex), dump);
                    UpdateInfoLabels(kind, dump);
                    LogResponse($"Loaded {Path.GetFileName(dlg.FileName)} ({raw.Length} bytes) as {KindNames[(int)kind]}");
                }
                catch (Exception ex)
                {
                    LogResponse($"✗ Could not load file: {ex.Message}", "Err");
                }
            }
        }

        private void OnSaveDumpClicked(object sender, EventArgs e)
        {
            if (_currentDump == null) return;
            using (var dlg = new SaveFileDialog { Filter = "Binary dump (*.bin)|*.bin", FileName = $"sideband_0x{_currentAddress:X2}.bin" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllBytes(dlg.FileName, _currentDump);
                    LogResponse($"Saved {Path.GetFileName(dlg.FileName)}");
                }
                catch (Exception ex)
                {
                    LogResponse($"✗ Could not save file: {ex.Message}", "Err");
                }
            }
        }

        #endregion

        #region Helpers

        private void SetBusy(bool busy)
        {
            if (IsDisposed) return;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            bool ready = !busy && _addressCombo.Items.Count > 0 && Device != null;
            _readButton.Enabled = ready;
            _readRegButton.Enabled = ready;
            _writeRegButton.Enabled = ready;
            _detectButton.Enabled = !busy && Device != null && !_discoveryRunning;
            if (!busy) UpdateUIState(Device != null);
        }

        #endregion
    }

    internal class SidebandInfoDialog : Form
    {
        public SidebandInfoDialog(string caption, List<(string Name, string Value)> rows)
        {
            Text = "Sideband Device Info";
            Size = new Size(560, 620);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(420, 360);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                Padding = new Padding(8)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            var viewer = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                Font = new Font("Consolas", 9.5F),
                DetectUrls = false,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both
            };

            var buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 6, 0, 0)
            };
            var closeButton = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
            buttonRow.Controls.Add(closeButton);

            layout.Controls.Add(viewer, 0, 0);
            layout.Controls.Add(buttonRow, 0, 1);
            Controls.Add(layout);
            CancelButton = closeButton;

            viewer.SelectionFont = new Font("Consolas", 11F, FontStyle.Bold);
            viewer.AppendText($"{caption}\n");
            foreach (var (name, value) in rows)
            {
                if (name == null)
                {
                    viewer.SelectionFont = new Font("Consolas", 10F, FontStyle.Bold);
                    viewer.AppendText($"\n{value}\n");
                }
                else
                {
                    viewer.SelectionFont = new Font("Consolas", 9.5F);
                    viewer.AppendText($"  {name.PadRight(30)}: {value}\n");
                }
            }
            viewer.SelectionStart = 0;
            viewer.ScrollToCaret();
        }
    }
}