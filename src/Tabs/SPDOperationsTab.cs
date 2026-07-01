using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using UDFCore;

namespace UnifiedDDRFlasher
{
    public class SPDOperationsTab : UserControl
    {
        #region Constants

        private const int READ_RETRIES = 3;
        private const int WRITE_RETRIES = 3;
        private const int PAGE_SWITCH_DELAY_MS = 10;
        private const int WRITE_DELAY_MS = 20;
        private const int READ_CHUNK_SIZE = 64;

        #endregion

        #region Events

        public event EventHandler<string> ErrorOccurred;

        #endregion

        #region Private Fields

        private Func<UDFDevice> _deviceProvider;
        private UDFDevice Device => _deviceProvider?.Invoke();

        private static bool IsDisconnectException(Exception ex) =>
            ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ObjectDisposedException
            || ex is InvalidOperationException;

        private HexEditorControl _hexEditor;
        private ProgressBar _writeProgress;
        private Button _openDumpButton;
        private Button _saveDumpButton;
        private ComboBox _moduleAddressCombo;
        private Label _detectedGenLabel;
        private Label _spdSizeLabel;
        private Label _snMfgLabel;
        private Label _mfgTimeLabel;
        private Label _speedGradeLabel;
        private Label _capacityLabel;
        private Label _busWidthLabel;
        private Label _xmpExpoLabel;
        private Label _crcStatusLabel;
        private TableLayoutPanel _writeProtectionPanel;
        private Button _readButton;
        private Button _verifyButton;
        private Button _writeAllButton;
        private Button _parsedFieldsButton;

        private byte[] _currentDump;
        private byte _currentAddress = 0x50;
        private ModuleInfo _currentModuleInfo;
        private List<byte> _detectedAddresses = new List<byte>();

        private ToolTip _toolTip;

        #endregion

        #region Constructor

        public SPDOperationsTab()
        {
            _toolTip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 400 };
            InitializeComponent();
            UpdateUIState(false);
        }

        #endregion

        #region Initialization

        private void InitializeComponent()
        {
            this.Dock = DockStyle.Fill;
            this.BackColor = Color.FromArgb(240, 240, 240);

            var topSplit = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(10)
            };
            topSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57F));
            topSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43F));
            topSplit.Controls.Add(CreateHexEditorPanel(), 0, 0);
            topSplit.Controls.Add(CreateControlPanel(), 1, 0);

            this.Controls.Add(topSplit);
        }

        private Panel CreateHexEditorPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            _writeProgress = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Visible = false,
                Style = ProgressBarStyle.Continuous
            };
            layout.Controls.Add(_writeProgress, 0, 0);

            _hexEditor = new HexEditorControl
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            layout.Controls.Add(_hexEditor, 0, 1);

            panel.Controls.Add(layout);
            return panel;
        }

        private Panel CreateControlPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(5)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 72F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28F));

            Panel addressPanel = new Panel { Dock = DockStyle.Fill };
            Label addressLabel = new Label
            {
                Text = "I2C Address:",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.BottomLeft
            };
            _moduleAddressCombo = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Consolas", 9.5F),
                Height = 28
            };
            _moduleAddressCombo.SelectedIndexChanged += OnModuleAddressChanged;
            addressPanel.Controls.Add(_moduleAddressCombo);
            addressPanel.Controls.Add(addressLabel);
            layout.Controls.Add(addressPanel, 0, 0);

            GroupBox moduleInfoGroup = new GroupBox
            {
                Text = "Module Info",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel infoLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 11,
                Padding = new Padding(5)
            };
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            infoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label MakeRowLabel(string text) => new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(3),
                AutoEllipsis = true
            };

            _detectedGenLabel = MakeRowLabel("Gen: -");
            infoLayout.Controls.Add(_detectedGenLabel, 0, 0);

            _detectedGenLabel.Cursor = Cursors.Hand;
            _detectedGenLabel.MouseUp += OnDetectedGenLabelMouseUp;

            _spdSizeLabel = MakeRowLabel("Size: -");
            infoLayout.Controls.Add(_spdSizeLabel, 0, 1);

            _snMfgLabel = MakeRowLabel("P/N | Mfg: -");
            infoLayout.Controls.Add(_snMfgLabel, 0, 2);

            _mfgTimeLabel = MakeRowLabel("Mfg time: -");
            infoLayout.Controls.Add(_mfgTimeLabel, 0, 3);

            _speedGradeLabel = MakeRowLabel("Speed: -");
            infoLayout.Controls.Add(_speedGradeLabel, 0, 4);

            _capacityLabel = MakeRowLabel("Capacity: -");
            infoLayout.Controls.Add(_capacityLabel, 0, 5);

            _busWidthLabel = MakeRowLabel("DRAM: -");
            infoLayout.Controls.Add(_busWidthLabel, 0, 6);

            _xmpExpoLabel = MakeRowLabel("XMP/EXPO: -");
            infoLayout.Controls.Add(_xmpExpoLabel, 0, 7);

            _crcStatusLabel = MakeRowLabel("CRC: -");
            infoLayout.Controls.Add(_crcStatusLabel, 0, 8);

            _parsedFieldsButton = new Button
            {
                Text = "More SPD Info...",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(3),
                Enabled = false
            };
            _parsedFieldsButton.Click += OnParsedFieldsClicked;
            _toolTip.SetToolTip(_parsedFieldsButton,
                "Display the full decoded SPD field dump (timings, CAS latencies, CRCs, manufacturing).");
            infoLayout.Controls.Add(_parsedFieldsButton, 0, 9);

            Panel wpContainer = new Panel { Dock = DockStyle.Fill };
            _writeProtectionPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 70,
                MaximumSize = new Size(560, 70),
                ColumnCount = 8,
                RowCount = 2,
                Margin = new Padding(0)
            };
            for (int c = 0; c < 8; c++)
                _writeProtectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 8F));
            _writeProtectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            _writeProtectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            Label wpLabel = new Label
            {
                Text = "Write Protection (click block to toggle):",
                Dock = DockStyle.Top,
                Height = 20,
                Font = new Font("Segoe UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
            wpContainer.Controls.Add(_writeProtectionPanel);
            wpContainer.Controls.Add(wpLabel);
            infoLayout.Controls.Add(wpContainer, 0, 10);

            moduleInfoGroup.Controls.Add(infoLayout);
            layout.Controls.Add(moduleInfoGroup, 0, 1);

            GroupBox spdDataGroup = new GroupBox
            {
                Text = "SPD Data",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8),
                ForeColor = Color.FromArgb(0, 78, 152)
            };

            TableLayoutPanel dataLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                Padding = new Padding(5)
            };
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 37F));
            dataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 30F));

            TableLayoutPanel openSaveLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2
            };
            openSaveLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            openSaveLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _openDumpButton = new Button
            {
                Text = "Open Dump",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _openDumpButton.Click += OnOpenDumpClicked;
            _toolTip.SetToolTip(_openDumpButton, "Load an SPD dump from a binary file.");

            _saveDumpButton = new Button
            {
                Text = "Save Dump",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _saveDumpButton.Click += OnSaveDumpClicked;
            _toolTip.SetToolTip(_saveDumpButton, "Save the current SPD data to a binary file.");

            openSaveLayout.Controls.Add(_openDumpButton, 0, 0);
            openSaveLayout.Controls.Add(_saveDumpButton, 1, 0);
            dataLayout.Controls.Add(openSaveLayout, 0, 0);

            _readButton = new Button
            {
                Text = "Read SPD",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3),
                Height = 35
            };
            _readButton.FlatAppearance.BorderSize = 0;
            _readButton.Click += OnReadClicked;
            _toolTip.SetToolTip(_readButton, "Read the full SPD contents from the selected module.");
            dataLayout.Controls.Add(_readButton, 0, 1);

            TableLayoutPanel verifyWriteLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2
            };
            verifyWriteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            verifyWriteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            _verifyButton = new Button
            {
                Text = "Verify",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _verifyButton.Click += OnVerifyClicked;
            _toolTip.SetToolTip(_verifyButton, "Compare the loaded dump against the module's current SPD content.");

            _writeAllButton = new Button
            {
                Text = "Write All",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.FromArgb(192, 0, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(3),
                Height = 32,
                Enabled = false
            };
            _writeAllButton.FlatAppearance.BorderSize = 0;
            _writeAllButton.Click += OnWriteAllClicked;
            _toolTip.SetToolTip(_writeAllButton, "Write the loaded dump to the selected SPD module. RSWP is cleared automatically for DDR4/5.");

            verifyWriteLayout.Controls.Add(_verifyButton, 0, 0);
            verifyWriteLayout.Controls.Add(_writeAllButton, 1, 0);
            dataLayout.Controls.Add(verifyWriteLayout, 0, 2);

            spdDataGroup.Controls.Add(dataLayout);
            layout.Controls.Add(spdDataGroup, 0, 2);

            panel.Controls.Add(layout);
            return panel;
        }

        #endregion

        #region Public Methods

        public void SetDeviceProvider(Func<UDFDevice> deviceProvider) =>
            _deviceProvider = deviceProvider;

        public void OnDeviceConnected(UDFDevice device)
        {
            if (InvokeRequired) { Invoke(new Action(() => OnDeviceConnected(device))); return; }
            UpdateUIState(true);
            AutoDetectModules();
        }

        public void OnDeviceDisconnected()
        {
            if (InvokeRequired) { Invoke(new Action(OnDeviceDisconnected)); return; }

            _detectedGenLabel.Text = "Gen: -";
            _spdSizeLabel.Text = "Size: -";
            ResetModuleSummary();
            _writeProtectionPanel.Controls.Clear();
            _moduleAddressCombo.Items.Clear();
            _currentModuleInfo = null;
            _currentDump = null;
            _hexEditor.ClearFieldHighlights();
            _hexEditor.SetData(Array.Empty<byte>());
            UpdateUIState(false);
        }

        public List<byte> DetectedSpdAddresses => new List<byte>(_detectedAddresses);

        public void UpdateDetectedDevices(List<byte> devices)
        {
            if (InvokeRequired) { Invoke(new Action(() => UpdateDetectedDevices(devices))); return; }

            var spdDevices = devices.Where(a => a >= 0x50 && a <= 0x57).ToList();

            if (!spdDevices.SequenceEqual(_detectedAddresses))
            {
                _detectedAddresses = new List<byte>(spdDevices);
                AutoDetectModules();
            }
        }

        #endregion

        #region Auto Detection

        private void AutoDetectModules()
        {
            if (Device == null) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                _moduleAddressCombo.Items.Clear();
                _detectedAddresses.Clear();

                var devices = Device.ScanBus();
                var spdDevices = devices.Where(a => a >= 0x50 && a <= 0x57).ToList();

                if (spdDevices.Count == 0)
                {
                    _moduleAddressCombo.Items.Add("No modules detected");
                    _moduleAddressCombo.SelectedIndex = 0;
                    _moduleAddressCombo.Enabled = false;
                    _detectedGenLabel.Text = "Gen: No DIMM";
                    _spdSizeLabel.Text = "Size: -";
                    ResetModuleSummary();
                    _writeProtectionPanel.Controls.Clear();
                    _writeProtectionPanel.ColumnStyles.Clear();
                    _writeProtectionPanel.RowStyles.Clear();
                    _writeProtectionPanel.ColumnCount = 1;
                    _writeProtectionPanel.RowCount = 1;
                    _writeProtectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                    _writeProtectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                    _writeProtectionPanel.Controls.Add(new Label
                    {
                        Text = "No DIMM detected",
                        Dock = DockStyle.Fill,
                        Font = new Font("Segoe UI", 10F),
                        ForeColor = Color.Gray,
                        TextAlign = ContentAlignment.MiddleLeft
                    });
                    _currentModuleInfo = null;
                    UpdateUIState(true);
                    return;
                }

                _detectedAddresses = spdDevices;
                _moduleAddressCombo.Enabled = true;

                foreach (byte addr in spdDevices)
                {
                    try
                    {
                        var info = Device.DetectModule(addr);
                        _moduleAddressCombo.Items.Add($"0x{addr:X2} - {info.Type}");
                    }
                    catch
                    {
                        _moduleAddressCombo.Items.Add($"0x{addr:X2} - Unknown");
                    }
                }

                _moduleAddressCombo.SelectedIndex = 0;

                if (_detectedAddresses.Count > 0)
                {
                    _currentAddress = _detectedAddresses[0];
                    DetectCurrentModule();
                }

                UpdateUIState(true);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Auto-detect failed: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        #endregion

        #region Event Handlers

        private void OnModuleAddressChanged(object sender, EventArgs e)
        {
            if (_moduleAddressCombo.SelectedIndex < 0 || _detectedAddresses.Count == 0) return;
            if (_moduleAddressCombo.SelectedIndex >= _detectedAddresses.Count) return;

            _currentAddress = _detectedAddresses[_moduleAddressCombo.SelectedIndex];
            DetectCurrentModule();
            UpdateUIState(true);
        }

        private void DetectCurrentModule()
        {
            if (Device == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                _currentModuleInfo = Device.DetectModule(_currentAddress);
                string suffix = "";
                if (_currentModuleInfo.GenerationOverridden) suffix += " (forced)";
                if (_currentModuleInfo.DetectedViaFallback) suffix += " (via fallback)";
                _detectedGenLabel.Text = $"Gen: {_currentModuleInfo.Type}{suffix}";
                _spdSizeLabel.Text = $"Size: {_currentModuleInfo.Size} bytes";
                UpdateWriteProtectionDisplay();
            }
            catch (Exception ex)
            {
                _detectedGenLabel.Text = "Gen: Error";
                _spdSizeLabel.Text = "Size: -";
                ErrorOccurred?.Invoke(this, $"Module detection failed: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OnDetectedGenLabelMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            bool ctrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            bool shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            if (!ctrl || !shift) return;

            ShowGenerationOverrideDialog();
        }

        private void ShowGenerationOverrideDialog()
        {
            if (Device == null) return;

            using (var dlg = new Form())
            {
                dlg.Text = "Force Generation (advanced)";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new System.Drawing.Size(360, 240);

                var info = new Label
                {
                    Text =
                        $"Override module type at address 0x{_currentAddress:X2}.\r\n\r\n" +
                        "Auto-detect bypassed when an override is active.\r\n" +
                        "Wrong overrides produce garbage reads - by design.",
                    Location = new System.Drawing.Point(12, 10),
                    Size = new System.Drawing.Size(336, 64),
                    AutoSize = false,
                };
                dlg.Controls.Add(info);

                var combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new System.Drawing.Point(12, 84),
                    Size = new System.Drawing.Size(336, 24),
                };
                combo.Items.AddRange(new object[]
                {
                    "Auto-detect (clear override)",
                    "Force DDR5",
                    "Force DDR4",
                    "Force DDR3 / Other",
                });
                if (Device.TryGetGenerationOverride(_currentAddress, out var current))
                {
                    switch (current)
                    {
                        case ModuleType.DDR5: combo.SelectedIndex = 1; break;
                        case ModuleType.DDR4: combo.SelectedIndex = 2; break;
                        case ModuleType.DDR3_Or_Other: combo.SelectedIndex = 3; break;
                        default: combo.SelectedIndex = 0; break;
                    }
                }
                else
                {
                    combo.SelectedIndex = 0;
                }
                dlg.Controls.Add(combo);

                var applyAllCheck = new CheckBox
                {
                    Text = "Apply to all addresses 0x50–0x57",
                    Location = new System.Drawing.Point(12, 120),
                    Size = new System.Drawing.Size(336, 24),
                    Checked = false,
                };
                dlg.Controls.Add(applyAllCheck);

                var ok = new Button
                {
                    Text = "Apply",
                    DialogResult = DialogResult.OK,
                    Location = new System.Drawing.Point(180, 196),
                    Size = new System.Drawing.Size(80, 28),
                };
                var cancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new System.Drawing.Point(268, 196),
                    Size = new System.Drawing.Size(80, 28),
                };
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;

                Action<byte> apply = (addr) =>
                {
                    switch (combo.SelectedIndex)
                    {
                        case 0: Device.ClearGenerationOverride(addr); break;
                        case 1: Device.SetGenerationOverride(addr, ModuleType.DDR5); break;
                        case 2: Device.SetGenerationOverride(addr, ModuleType.DDR4); break;
                        case 3: Device.SetGenerationOverride(addr, ModuleType.DDR3_Or_Other); break;
                    }
                };

                if (applyAllCheck.Checked)
                    for (byte a = 0x50; a <= 0x57; a++) apply(a);
                else
                    apply(_currentAddress);

                DetectCurrentModule();
            }
        }

        private void UpdateWriteProtectionDisplay()
        {
            _writeProtectionPanel.Controls.Clear();
            _writeProtectionPanel.ColumnStyles.Clear();
            _writeProtectionPanel.RowStyles.Clear();

            if (_currentModuleInfo?.Type != ModuleType.DDR5)
            {
                _writeProtectionPanel.ColumnCount = 1;
                _writeProtectionPanel.RowCount = 1;
                _writeProtectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                _writeProtectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                _writeProtectionPanel.Controls.Add(new Label
                {
                    Text = "Hardware WP cleared automatically on write",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 9F),
                    ForeColor = Color.DimGray,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = true
                });
                return;
            }

            _writeProtectionPanel.ColumnCount = 8;
            _writeProtectionPanel.RowCount = 2;
            for (int c = 0; c < 8; c++)
                _writeProtectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 8F));
            _writeProtectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            _writeProtectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            try
            {
                for (byte block = 0; block < 16; block++)
                {
                    byte capturedBlock = block;
                    bool isProtected = Device.GetRSWP(_currentAddress, block);

                    var blockLabel = new Label
                    {
                        Text = block.ToString(),
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleCenter,
                        BorderStyle = BorderStyle.FixedSingle,
                        BackColor = isProtected ? Color.LightCoral : Color.LightGreen,
                        Font = new Font("Segoe UI", 9F),
                        Margin = new Padding(1),
                        Cursor = Cursors.Hand
                    };
                    blockLabel.Click += (s, e) => OnWriteProtectionBlockClicked(capturedBlock);
                    _toolTip.SetToolTip(blockLabel, isProtected
                        ? $"Block {block}: Protected (click to toggle)"
                        : $"Block {block}: Writable (click to toggle)");

                    _writeProtectionPanel.Controls.Add(blockLabel, block % 8, block / 8);
                }
            }
            catch (Exception ex)
            {
                _writeProtectionPanel.ColumnCount = 1;
                _writeProtectionPanel.RowCount = 1;
                _writeProtectionPanel.ColumnStyles.Clear();
                _writeProtectionPanel.RowStyles.Clear();
                _writeProtectionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                _writeProtectionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                _writeProtectionPanel.Controls.Add(new Label
                {
                    Text = "Read error",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 10F),
                    ForeColor = Color.Red,
                    TextAlign = ContentAlignment.MiddleLeft
                });
                ErrorOccurred?.Invoke(this, $"Failed to read WP status: {ex.Message}");
            }
        }

        private void OnWriteProtectionBlockClicked(byte block)
        {
            if (_currentModuleInfo?.Type != ModuleType.DDR5) return;

            var result = MessageBox.Show(
                $"Toggle write protection for block {block}?",
                "Write Protection",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            try
            {
                bool current = Device.GetRSWP(_currentAddress, block);
                if (current)
                    Device.ClearRSWP(_currentAddress);
                else
                    Device.SetRSWP(_currentAddress, block);

                UpdateWriteProtectionDisplay();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"WP toggle failed: {ex.Message}");
            }
        }

        private void OnReadClicked(object sender, EventArgs e)
        {
            if (Device == null)
            {
                ErrorOccurred?.Invoke(this, "Device not connected");
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _readButton.Enabled = false;

                _currentDump = ReadEntireSPDWithRetry(_currentAddress);

                if (_currentDump == null || _currentDump.Length == 0)
                {
                    ErrorOccurred?.Invoke(this, "Failed to read SPD - no data received");
                    MessageBox.Show("Failed to read SPD. Check connections and try again.",
                        "Read Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                DisplayHexDump(_currentDump);
                UpdateModuleSummary();
                _saveDumpButton.Enabled = true;
                _verifyButton.Enabled = true;
                _writeAllButton.Enabled = true;
                _parsedFieldsButton.Enabled = true;
            }
            catch (Exception ex) when (IsDisconnectException(ex))
            {
                ErrorOccurred?.Invoke(this, $"Read aborted (device disconnected): {ex.Message}");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Read failed: {ex.Message}");
                MessageBox.Show($"Read failed:\n\n{ex.Message}\n\nCheck connections and device compatibility.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                _readButton.Enabled = true;
            }
        }

        private byte[] ReadEntireSPDWithRetry(byte address)
        {
            try
            {
                var data = ReadEntireSPDOnce(address);
                if (data != null && data.Length > 0)
                    return data;
                UDFDebug.Log($"Read 0x{address:X2}: no data; rebooting bus and retrying once");
            }
            catch (Exception ex) when (!IsDisconnectException(ex))
            {
                UDFDebug.Log($"Read 0x{address:X2} failed ({ex.Message}); rebooting bus and retrying once");
            }

            try { Device.InvalidateModuleCache(address); } catch { }
            try { Device.BusReset(); }
            catch (Exception rex) { UDFDebug.Log($"BusReset during read recovery failed: {rex.Message}"); }
            System.Threading.Thread.Sleep(150);

            return ReadEntireSPDOnce(address);
        }

        private byte[] ReadEntireSPDOnce(byte address)
        {
            var info = Device.DetectModule(address);

            if (info.Type == ModuleType.DDR5)
                return ReadDDR5SPD(address, info.Size);
            else
                return ReadStandardSPD(address, info.Size);
        }

        private byte[] ReadDDR5SPD(byte address, int totalSize)
        {
            var allData = new List<byte>();

            int pagesNeeded = (totalSize + 127) / 128;

            for (byte page = 0; page < pagesNeeded; page++)
            {
                try
                {
                    int bytesRemainingTotal = totalSize - allData.Count;
                    if (bytesRemainingTotal <= 0) break;
                    int bytesThisPage = Math.Min(128, bytesRemainingTotal);

                    int bytesReadThisPage = 0;
                    while (bytesReadThisPage < bytesThisPage)
                    {
                        byte chunkSize = (byte)Math.Min(READ_CHUNK_SIZE, bytesThisPage - bytesReadThisPage);
                        ushort linearOffset = (ushort)(page * 128 + bytesReadThisPage);

                        byte[] chunk = ReadWithRetry(address, linearOffset, chunkSize, READ_RETRIES);
                        if (chunk == null || chunk.Length != chunkSize)
                            throw new Exception($"Failed to read page {page} at offset 0x{linearOffset:X3}");

                        allData.AddRange(chunk);
                        bytesReadThisPage += chunkSize;
                    }
                }
                catch (Exception ex)
                {
                    if (page == 0)
                    {
                        ErrorOccurred?.Invoke(this, $"DDR5 page {page} read error: {ex.Message}");
                        return null;
                    }
                    ErrorOccurred?.Invoke(this,
                        $"DDR5 read stopped after page {page - 1}: page {page} not readable " +
                        $"({ex.Message}). Returning {allData.Count}/{totalSize} bytes; " +
                        "remainder padded with 0x00. This is normal for some clone DIMMs " +
                        "with partial NVM population.");
                    int padBytes = totalSize - allData.Count;
                    if (padBytes > 0) allData.AddRange(new byte[padBytes]);
                    break;
                }
            }

            if (allData.Count > totalSize)
                allData.RemoveRange(totalSize, allData.Count - totalSize);

            return allData.ToArray();
        }

        private byte[] ReadStandardSPD(byte address, int size)
        {
            var allData = new List<byte>();

            for (ushort offset = 0; offset < size; offset += READ_CHUNK_SIZE)
            {
                byte chunkSize = (byte)Math.Min(READ_CHUNK_SIZE, size - offset);
                byte[] chunk = ReadWithRetry(address, offset, chunkSize, READ_RETRIES);
                if (chunk == null || chunk.Length != chunkSize)
                    throw new Exception($"Failed to read at offset 0x{offset:X3}");

                allData.AddRange(chunk);
            }

            return allData.ToArray();
        }

        private byte[] ReadWithRetry(byte address, ushort offset, byte length, int maxRetries)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    byte[] data = Device.ReadSPD(address, offset, length);
                    if (data != null && data.Length == length) return data;
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
                catch (Exception)
                {
                    if (attempt == maxRetries - 1) throw;
                    System.Threading.Thread.Sleep(100 * (attempt + 1));
                }
            }
            return null;
        }

        private void OnVerifyClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentDump == null)
            {
                ErrorOccurred?.Invoke(this, "No dump loaded to verify");
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _verifyButton.Enabled = false;

                byte[] readData = ReadEntireSPDWithRetry(_currentAddress);
                if (readData == null)
                {
                    ErrorOccurred?.Invoke(this, "Failed to read SPD for verification");
                    return;
                }

                int minLen = Math.Min(readData.Length, _currentDump.Length);
                var diffOffsets = new List<int>();

                for (int i = 0; i < minLen; i++)
                    if (readData[i] != _currentDump[i])
                        diffOffsets.Add(i);

                if (diffOffsets.Count == 0)
                {
                    MessageBox.Show("Verification PASSED!\nSPD matches the loaded dump.", "Verify",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    string diffList = diffOffsets.Count > 10
                        ? $"{diffOffsets.Count} bytes differ (first 10: {string.Join(", ", diffOffsets.Take(10).Select(o => $"0x{o:X3}"))})"
                        : $"Differences at: {string.Join(", ", diffOffsets.Select(o => $"0x{o:X3}"))}";

                    ErrorOccurred?.Invoke(this, $"Verification failed: {diffList}");
                    MessageBox.Show($"Verification FAILED!\n{diffOffsets.Count} byte(s) differ.\n\n{diffList}",
                        "Verify Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Verification error: {ex.Message}");
            }
            finally
            {
                Cursor = Cursors.Default;
                _verifyButton.Enabled = true;
            }
        }

        private void OnWriteAllClicked(object sender, EventArgs e)
        {
            if (Device == null || _currentDump == null)
            {
                ErrorOccurred?.Invoke(this, "No dump loaded");
                return;
            }

            var result = MessageBox.Show(
                $"⚠ WARNING ⚠\n\nThis will OVERWRITE the entire SPD at 0x{_currentAddress:X2}!\n\n" +
                $"Size: {_currentDump.Length} bytes\n\nThis cannot be undone. Continue?",
                "Confirm Write All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            try
            {
                Cursor = Cursors.WaitCursor;
                _writeAllButton.Enabled = false;

                bool crcRecomputed = false;
                byte[] fixedDump = SPDParsedFields.RecalcAndFixCrc(_currentDump);
                if (fixedDump != null && fixedDump.Length == _currentDump.Length)
                {
                    for (int k = 0; k < fixedDump.Length; k++)
                    {
                        if (fixedDump[k] != _currentDump[k]) { crcRecomputed = true; break; }
                    }
                    if (crcRecomputed)
                    {
                        _currentDump = fixedDump;
                        _hexEditor.SetData(_currentDump);
                    }
                }

                _writeProgress.Value = 0;
                _writeProgress.Maximum = _currentDump.Length;
                _writeProgress.Visible = true;

                if (_currentModuleInfo?.Type == ModuleType.DDR5)
                    Device.ClearRSWP(_currentAddress);
                else if (_currentModuleInfo?.Type == ModuleType.DDR4)
                {
                    try { Device.ClearRSWP(_currentAddress); } catch { }
                }

                int totalBytes = _currentDump.Length;
                int bytesWritten = 0;
                int errors = 0;
                string protectionNote = "";
                bool protectionChecked = false;

                for (ushort offset = 0; offset < totalBytes; offset += 16)
                {
                    int chunkSize = Math.Min(16, totalBytes - offset);
                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(_currentDump, offset, chunk, 0, chunkSize);

                    if (_currentModuleInfo?.Type == ModuleType.DDR4 && offset == 256)
                        System.Threading.Thread.Sleep(20);

                    bool writeSuccess = false;
                    for (int retry = 0; retry < WRITE_RETRIES; retry++)
                    {
                        try
                        {
                            writeSuccess = Device.WriteSPDPage(_currentAddress, offset, chunk);
                            if (writeSuccess) break;
                            System.Threading.Thread.Sleep(50 * (retry + 1));
                        }
                        catch
                        {
                            System.Threading.Thread.Sleep(100 * (retry + 1));
                        }
                    }

                    if (!writeSuccess)
                    {
                        errors++;
                        ErrorOccurred?.Invoke(this, $"Write failed at 0x{offset:X3}");

                        if (!protectionChecked)
                        {
                            protectionChecked = true;
                            try
                            {
                                byte blk = (byte)((_currentModuleInfo?.Type == ModuleType.DDR5)
                                    ? offset / 64 : offset / 128);
                                if (Device.GetRSWP(_currentAddress, blk))
                                    protectionNote =
                                        $"\n\nBlock {blk} is still RSWP write-protected. Clearing it " +
                                        "needs the programmer's high-voltage supply, so writes to that " +
                                        "block will keep failing until protection is removed.";
                            }
                            catch { }
                        }

                        for (int i = 0; i < chunkSize; i++)
                        {
                            try
                            {
                                if (Device.WriteSPDByte(_currentAddress, (ushort)(offset + i), chunk[i]))
                                    errors--;
                            }
                            catch { }
                        }

                        if (errors > 3)
                            throw new Exception($"Multiple write failures; stopping at 0x{offset:X3}.{protectionNote}");
                    }

                    bytesWritten += chunkSize;
                    _writeProgress.Value = Math.Min(bytesWritten, _writeProgress.Maximum);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(WRITE_DELAY_MS);
                }

                string msg = errors == 0
                    ? $"Successfully wrote {totalBytes} bytes to SPD!" +
                          (crcRecomputed ? "\n\nThe SPD checksum was stale and was recomputed before writing." : "")
                    : $"Write completed with {errors} error(s). Verify is recommended.{protectionNote}";
                MessageBox.Show(msg, errors == 0 ? "Write Complete" : "Write Complete with Errors",
                    MessageBoxButtons.OK, errors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex) when (IsDisconnectException(ex))
            {
                ErrorOccurred?.Invoke(this, $"Write aborted (device disconnected): {ex.Message}");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Write failed: {ex.Message}");
                MessageBox.Show($"Write failed:\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                _writeProgress.Visible = false;
                _writeProgress.Value = 0;
                _writeAllButton.Enabled = true;
            }
        }

        private void OnOpenDumpClicked(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*",
                Title = "Open SPD Dump"
            };

            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                byte[] loaded = File.ReadAllBytes(dialog.FileName);

                if (_currentModuleInfo != null && _currentModuleInfo.Size > 0 &&
                    loaded.Length != _currentModuleInfo.Size)
                {
                    string warn = $"Loaded dump is {loaded.Length} bytes; SPD is {_currentModuleInfo.Size} bytes.\n\n" +
                                  "Writing this dump may cause issues. Continue?";
                    if (MessageBox.Show(warn, "Size Mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
                        return;
                }

                _currentDump = loaded;
                DisplayHexDump(_currentDump);
                UpdateModuleSummary();
                _saveDumpButton.Enabled = true;

                bool hasModule = _currentModuleInfo != null && _detectedAddresses.Count > 0;
                _verifyButton.Enabled = hasModule;
                _writeAllButton.Enabled = hasModule;
                _parsedFieldsButton.Enabled = true;

                ErrorOccurred?.Invoke(this, $"[INFO] Loaded {loaded.Length} bytes from {dialog.FileName}");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to load file: {ex.Message}");
            }
        }

        private void OnSaveDumpClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                ErrorOccurred?.Invoke(this, "No dump data to save");
                return;
            }

            string snPart = SafeSerialForFilename(_currentDump);
            if (string.IsNullOrEmpty(snPart)) snPart = $"0x{_currentAddress:X2}";
            using var dialog = new SaveFileDialog
            {
                Filter = "Binary Files (*.bin)|*.bin|All Files (*.*)|*.*",
                FileName = $"{snPart}_{DateTime.Now:yyyyMMdd_HHmmss}.bin"
            };

            if (dialog.ShowDialog() != DialogResult.OK) return;

            try
            {
                File.WriteAllBytes(dialog.FileName, _currentDump);
                MessageBox.Show($"Saved {_currentDump.Length} bytes.", "File Saved",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to save file: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        private void DisplayHexDump(byte[] data)
        {
            _hexEditor.SetData(data ?? Array.Empty<byte>());
            ApplyHexFieldHighlights(data);
        }

        private void ApplyHexFieldHighlights(byte[] data)
        {
            _hexEditor.ClearFieldHighlights();
            if (data == null || data.Length < 4) return;

            Color[] palette = FieldMaps.HighlightPalette;
            int colorIdx = 0;

            void Add(int start, int len, string label)
            {
                if (start < 0 || len < 1 || start >= data.Length) return;
                if (start + len > data.Length) len = data.Length - start;
                if (len < 1) return;
                Color c = palette[colorIdx % palette.Length];
                colorIdx++;
                _hexEditor.AddFieldHighlight(start, len, c, label);
            }

            FieldMaps.AddSpdFields(data, Add);
        }

        private void UpdateUIState(bool connected)
        {
            bool hasModule = connected && _detectedAddresses.Count > 0;
            bool hasDump = _currentDump != null;

            _openDumpButton.Enabled = hasModule;
            _saveDumpButton.Enabled = hasDump;
            _readButton.Enabled = hasModule;
            _verifyButton.Enabled = hasModule && hasDump;
            _writeAllButton.Enabled = hasModule && hasDump;
            _parsedFieldsButton.Enabled = hasDump;
            _moduleAddressCombo.Enabled = hasModule;
        }

        private void OnParsedFieldsClicked(object sender, EventArgs e)
        {
            if (_currentDump == null)
            {
                MessageBox.Show("No SPD dump loaded.", "Parsed Fields",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new ParsedFieldsDialog(_currentDump))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.PatchedDump != null)
                {
                    _currentDump = dlg.PatchedDump;
                    DisplayHexDump(_currentDump);
                    UpdateModuleSummary();
                    ErrorOccurred?.Invoke(this,
                        "[INFO] CRC patched in memory; click 'Write All' to push to module.");
                }
            }
        }

        private void UpdateModuleSummary()
        {
            if (_currentDump == null || _currentDump.Length < 32)
            {
                ResetModuleSummary();
                return;
            }

            var sum = SPDParsedFields.Summarize(_currentDump);

            _spdSizeLabel.Text = $"Size: {Dash(sum.SpdSize)}";

            string pn = Dash(sum.PartNumber);
            string mfg = Dash(sum.ModuleMfg);
            _snMfgLabel.Text = $"P/N | Mfg: {pn} | {mfg}";
            _toolTip.SetToolTip(_snMfgLabel,
                $"Part number: {pn}\nModule manufacturer: {mfg}\nSerial: {Dash(sum.SerialNumber)}");

            _mfgTimeLabel.Text = $"Mfg time: {Dash(sum.MfgTime)}";

            string speed = Dash(sum.Speed);
            string prim = Dash(sum.Primaries);
            _speedGradeLabel.Text = $"Speed: {speed} | {prim}";

            _capacityLabel.Text = $"Capacity: {Dash(sum.Capacity)}";

            string dram = sum.DramDensity;
            if (!string.IsNullOrEmpty(sum.DramMfg))
                dram = string.IsNullOrEmpty(dram) ? sum.DramMfg : $"{sum.DramMfg} {dram}";
            _busWidthLabel.Text = $"DRAM: {Dash(dram)}";

            if (sum.Profiles.Count == 0)
            {
                _xmpExpoLabel.Text = "XMP/EXPO: none";
                _toolTip.SetToolTip(_xmpExpoLabel, "No XMP or EXPO overclock profiles found.");
            }
            else
            {
                var p0 = sum.Profiles[0];
                _xmpExpoLabel.Text = $"{p0.Kind}: {Dash(p0.Speed)} | {Dash(p0.Primaries)} | VDD {Dash(p0.Vdd)}";
                var tip = new System.Text.StringBuilder();
                foreach (var p in sum.Profiles)
                    tip.AppendLine($"{p.Kind} {p.Name}:  {Dash(p.Speed)}  |  {Dash(p.Primaries)}  |  VDD {Dash(p.Vdd)}");
                _toolTip.SetToolTip(_xmpExpoLabel, tip.ToString().TrimEnd());
            }

            if (string.IsNullOrEmpty(sum.CrcStatus))
            {
                _crcStatusLabel.Text = "CRC: -";
                _crcStatusLabel.ForeColor = SystemColors.ControlText;
            }
            else
            {
                _crcStatusLabel.Text = $"CRC: {sum.CrcStatus}";
                _crcStatusLabel.ForeColor = sum.CrcOk ? Color.DarkGreen : Color.DarkRed;
            }
        }

        private static string Dash(string v) => string.IsNullOrEmpty(v) ? "-" : v;

        private void ResetModuleSummary()
        {
            if (_snMfgLabel != null) _snMfgLabel.Text = "P/N | Mfg: -";
            if (_mfgTimeLabel != null) _mfgTimeLabel.Text = "Mfg time: -";
            if (_speedGradeLabel != null) _speedGradeLabel.Text = "Speed: -";
            if (_capacityLabel != null) _capacityLabel.Text = "Capacity: -";
            if (_busWidthLabel != null) _busWidthLabel.Text = "DRAM: -";
            if (_xmpExpoLabel != null)
            {
                _xmpExpoLabel.Text = "XMP/EXPO: -";
                _toolTip.SetToolTip(_xmpExpoLabel, null);
            }
            if (_crcStatusLabel != null)
            {
                _crcStatusLabel.Text = "CRC: -";
                _crcStatusLabel.ForeColor = SystemColors.ControlText;
            }
        }

        private static string SafeSerialForFilename(byte[] spd)
        {
            var sum = SPDParsedFields.Summarize(spd);
            string sn = sum.SerialNumber;
            if (string.IsNullOrEmpty(sn)) return null;
            var sb = new System.Text.StringBuilder(sn.Length);
            foreach (char c in sn)
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            string cleaned = sb.ToString();
            if (cleaned.Length == 0) return null;
            string trimmed = cleaned.StartsWith("0x") || cleaned.StartsWith("0X") ? cleaned.Substring(2) : cleaned;
            bool allZero = trimmed.TrimStart('0').Length == 0;
            bool allF = trimmed.Replace("F", "").Replace("f", "").Length == 0 && trimmed.Length > 0;
            if (allZero || allF) return null;
            return "SN" + cleaned;
        }

        #endregion
    }

    internal class ParsedFieldsDialog : Form
    {
        public byte[] PatchedDump { get; private set; }

        private readonly byte[] _spd;
        private RichTextBox _viewer;
        private Button _recalcButton;
        private Button _closeButton;

        public ParsedFieldsDialog(byte[] spd)
        {
            _spd = spd;
            Text = "Parsed SPD Fields";
            Size = new Size(820, 740);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(600, 500);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                Padding = new Padding(8)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            _viewer = new RichTextBox
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
            _closeButton = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 100, Height = 32 };
            _recalcButton = new Button { Text = "Recalc && Fix CRC", Width = 160, Height = 32, Margin = new Padding(8, 0, 0, 0) };
            _recalcButton.Click += OnRecalcClick;
            buttonRow.Controls.Add(_closeButton);
            buttonRow.Controls.Add(_recalcButton);

            layout.Controls.Add(_viewer, 0, 0);
            layout.Controls.Add(buttonRow, 0, 1);
            Controls.Add(layout);

            Render(_spd);
        }

        private void OnRecalcClick(object sender, EventArgs e)
        {
            PatchedDump = SPDParsedFields.RecalcAndFixCrc(_spd);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Render(byte[] spd)
        {
            var result = SPDParsedFields.Parse(spd);
            _viewer.Clear();
            _viewer.SelectionFont = new Font("Consolas", 11F, FontStyle.Bold);
            _viewer.AppendText($"DRAM Type: {result.DramType}\n");
            _viewer.SelectionFont = new Font("Consolas", 9.5F);
            _viewer.AppendText(new string('-', 70) + "\n");

            int maxLabelLen = 0;
            foreach (var f in result.Fields)
                if (!string.IsNullOrEmpty(f.Value) && f.Label.Length > maxLabelLen)
                    maxLabelLen = f.Label.Length;

            foreach (var f in result.Fields)
            {
                if (string.IsNullOrEmpty(f.Value))
                {
                    _viewer.SelectionFont = new Font("Consolas", 9.5F, FontStyle.Bold);
                    _viewer.SelectionColor = Color.FromArgb(0, 78, 152);
                    _viewer.AppendText($"\n{f.Label}\n");
                    _viewer.SelectionColor = Color.Black;
                    _viewer.SelectionFont = new Font("Consolas", 9.5F);
                    continue;
                }

                if (f.IsCrcStatus)
                {
                    _viewer.SelectionColor = f.CrcOk ? Color.DarkGreen : Color.DarkRed;
                    _viewer.AppendText($"{f.Label.PadRight(maxLabelLen + 2)}: {(f.CrcOk ? "[OK]" : "[FAIL]")} {f.Value}\n");
                    _viewer.SelectionColor = Color.Black;
                }
                else
                {
                    _viewer.AppendText($"{f.Label.PadRight(maxLabelLen + 2)}: {f.Value}\n");
                }
            }
        }
    }
}