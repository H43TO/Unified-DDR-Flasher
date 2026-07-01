using UDFCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnifiedDDRFlasher
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        private const int EXIT_OK = 0;
        private const int EXIT_NOT_FOUND = 1;
        private const int EXIT_I2C_ERROR = 2;
        private const int EXIT_VERIFY_FAIL = 3;
        private const int EXIT_CRC_ERROR = 4;
        private const int EXIT_WRITE_ERROR = 5;
        private const int EXIT_USAGE = 6;

        [STAThread]
        static int Main(string[] args)
        {
            if (args == null || args.Length == 0)
                return RunGui();
            return RunCli(args);
        }

        #region GUI entry

        private static int RunGui()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            try { Application.Run(new MainForm()); }
            catch (Exception ex) { ShowErrorDialog("Application Startup Error", ex); return 1; }
            return 0;
        }

        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
            => ShowErrorDialog("Unhandled Thread Exception", e.Exception);

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                ShowErrorDialog("Unhandled Exception", ex);
        }

        private static void ShowErrorDialog(string title, Exception ex)
        {
            string message = $"An error occurred:\n\n{ex.Message}\n\nType: {ex.GetType().Name}";
            if (ex.InnerException != null) message += $"\n\nInner Exception: {ex.InnerException.Message}";
            message += $"\n\nStack Trace:\n{ex.StackTrace}";
            try { MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { Console.Error.WriteLine($"{title}: {ex}"); }
        }

        #endregion

        #region CLI entry

        private class CliOptions
        {
            public string Port;
            public bool AutoDetect;
            public int Baud = 115200;
            public int TimeoutMs = 5000;
            public string OutputFormat = "text";
            public bool Quiet;
            public bool UseSmbus;
            public string LogFile;
            public int ReconnectTimeoutSec = 0;
            public string Command;
            public List<string> Positional = new List<string>();
            public Dictionary<string, string> Switches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Flags = new List<string>();
        }

        private static int RunCli(string[] args)
        {
            if (!AttachConsole(-1))
                AllocConsole();

            CliOptions opts;
            try { opts = ParseArgs(args); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Argument error: {ex.Message}");
                PrintUsage();
                return EXIT_USAGE;
            }

            if (string.IsNullOrEmpty(opts.Command))
            {
                PrintUsage();
                return EXIT_USAGE;
            }

            if (string.Equals(opts.Command, "help", StringComparison.OrdinalIgnoreCase) ||
                opts.Command == "--help" || opts.Command == "-h")
            {
                PrintUsage();
                return EXIT_OK;
            }

            if (string.Equals(opts.Command, "spd", StringComparison.OrdinalIgnoreCase) &&
                opts.Positional.Count > 0 &&
                string.Equals(opts.Positional[0], "parse-file", StringComparison.OrdinalIgnoreCase))
            {
                return CmdSpdParseFile(opts);
            }

            if (opts.UseSmbus)
            {
                try
                {
                    var smbus = DirectSmbusDevice.Detect();
                    if (smbus == null)
                    {
                        WriteLine(opts, "ERROR: Could not initialise AMD SMBus controller.");
                        WriteLine(opts, "       Run as Administrator and ensure inpoutx64 is installed.");
                        return EXIT_NOT_FOUND;
                    }
                    using (smbus)
                    {
                        WriteLine(opts, $"[SMBus] {smbus.DeviceName}");
                        return DispatchCommandSmbus(smbus, opts);
                    }
                }
                catch (Exception ex)
                {
                    WriteLine(opts, $"ERROR initialising SMBus: {ex.Message}");
                    return EXIT_NOT_FOUND;
                }
            }

            string port = opts.Port;
            if (string.IsNullOrEmpty(port))
            {
                if (opts.AutoDetect)
                {
                    port = AutoDetectPort();
                    if (port == null)
                    {
                        WriteLine(opts, "ERROR: No UDF device detected on any COM port.");
                        return EXIT_NOT_FOUND;
                    }
                    WriteLine(opts, $"[UDF] Auto-detected port {port}");
                }
                else
                {
                    Console.Error.WriteLine("--port is required (or use --auto-detect)");
                    return EXIT_USAGE;
                }
            }

            try
            {
                using (var device = new UDFDevice(port, opts.Baud))
                {
                    return DispatchCommand(device, port, opts);
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("CMD_PING"))
            {
                WriteLine(opts, $"ERROR: {ex.Message}");
                return EXIT_NOT_FOUND;
            }
            catch (IOException ex) when (opts.ReconnectTimeoutSec > 0)
            {
                WriteLine(opts, $"[UDF] Connection lost: {ex.Message}");
                WriteLine(opts, $"[UDF] Waiting up to {opts.ReconnectTimeoutSec}s for {port} to reconnect...");

                var deadline = DateTime.UtcNow.AddSeconds(opts.ReconnectTimeoutSec);
                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(300);
                    if (!System.IO.Ports.SerialPort.GetPortNames()
                            .Contains(port, StringComparer.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        WriteLine(opts, $"[UDF] {port} reappeared, reconnecting...");
                        using (var device = new UDFDevice(port, opts.Baud))
                        {
                            return DispatchCommand(device, port, opts);
                        }
                    }
                    catch (Exception reconnectEx)
                    {
                        WriteLine(opts, $"[UDF] Reconnect attempt failed: {reconnectEx.Message}");
                    }
                }

                WriteLine(opts, $"ERROR: {port} did not reconnect within {opts.ReconnectTimeoutSec}s.");
                return EXIT_NOT_FOUND;
            }
            catch (Exception ex)
            {
                WriteLine(opts, $"ERROR: {ex.Message}");
                return EXIT_I2C_ERROR;
            }
        }

        private static int DispatchCommand(UDFDevice device, string port, CliOptions opts)
        {

            if (opts.Flags.Contains("--use-i3c"))
            {
                try { device.UseI3cTransport = true; }
                catch (Exception ex) { WriteLine(opts, $"[UDF] --use-i3c not honored by firmware: {ex.Message}"); }
            }

            if (opts.Switches.TryGetValue("--force-gen", out var forceGen))
            {
                ModuleType t;
                switch (forceGen.Trim().ToLowerInvariant())
                {
                    case "ddr3":
                    case "ddr3_or_other":
                    case "ddr2":
                        t = ModuleType.DDR3_Or_Other; break;
                    case "ddr4": t = ModuleType.DDR4; break;
                    case "ddr5": t = ModuleType.DDR5; break;
                    default:
                        WriteLine(opts, $"ERROR: --force-gen value '{forceGen}' must be one of: ddr3, ddr4, ddr5");
                        return EXIT_USAGE;
                }
                for (byte addr = 0x50; addr <= 0x57; addr++)
                    device.SetGenerationOverride(addr, t);
            }

            switch (opts.Command.ToLowerInvariant())
            {
                case "ping": return CmdPing(device, port, opts);
                case "version": return CmdVersion(device, port, opts);
                case "test": return CmdTest(device, opts);
                case "name": return CmdName(device, opts);
                case "i2c-speed": return CmdI2cSpeed(device, opts);
                case "scan": return CmdScan(device, opts);
                case "detect": return CmdDetect(device, opts);
                case "debug-dump": return CmdDebugDump(device, port, opts);
                case "debug-pagewalk": return CmdDebugPageWalk(device, port, opts);
                case "rswp-support": return CmdRswpSupport(device, opts);
                case "reboot-dimm": return CmdRebootDimm(device, opts);
                case "spd": return CmdSpd(device, opts);
                case "rswp": return CmdRswp(device, opts);
                case "pmic": return CmdPmic(device, opts);
                case "factory-reset": return CmdFactoryReset(device, opts);
                case "pin": return CmdPin(device, opts);
                case "eeprom": return CmdEeprom(device, opts);
                case "firmware": return CmdFirmware(device, port, opts);
                default:
                    Console.Error.WriteLine($"Unknown command: {opts.Command}");
                    PrintUsage();
                    return EXIT_USAGE;
            }
        }

        #endregion

        #region SMBus dispatcher (host AMD SMBus controller)

        private static int DispatchCommandSmbus(DirectSmbusDevice device, CliOptions opts)
        {
            switch (opts.Command.ToLowerInvariant())
            {
                case "ping":
                    {
                        var spds = device.ScanSpdBusAsync(CancellationToken.None)
                                         .ConfigureAwait(false).GetAwaiter().GetResult();
                        bool alive = spds.Count > 0;
                        if (opts.OutputFormat == "json")
                            WriteRaw(opts, ToJson(new Dictionary<string, object>
                            {
                                ["status"] = alive ? "ok" : "fail",
                                ["spd_devices"] = spds.Select(a => $"0x{a:X2}").ToList()
                            }) + "\n");
                        else
                            WriteLine(opts, alive ? "OK" : "FAIL (no SPD devices found)");
                        return alive ? EXIT_OK : EXIT_NOT_FOUND;
                    }

                case "version":
                    WriteLine(opts, $"[SMBus] AMD SMBus controller (I/O base: 0x{SmbusIo.BaseAddress:X4})");
                    return EXIT_OK;

                case "test":
                    WriteLine(opts, "Self-test not applicable to host SMBus.");
                    return EXIT_OK;

                case "name":
                    WriteLine(opts, "Device-name get/set is not available on host SMBus.");
                    return EXIT_OK;

                case "i2c-speed":
                    WriteLine(opts, "I2C speed is fixed by the host SMBus controller.");
                    return EXIT_OK;

                case "scan": return CmdSmScan(device, opts);
                case "detect": return CmdSmDetect(device, opts);
                case "spd": return CmdSmSpd(device, opts);
                case "pmic": return CmdSmPmic(device, opts);

                case "rswp":
                case "rswp-support":
                    WriteLine(opts, "RSWP operations are not available on host SMBus (no HV / SA1 control).");
                    return EXIT_OK;

                case "factory-reset":
                    WriteLine(opts, "Factory reset is a UDF-firmware-only operation.");
                    return EXIT_OK;

                case "reboot-dimm":
                    WriteLine(opts, "Reboot DIMM requires UDF firmware (VIN_CTRL pin).");
                    return EXIT_OK;

                case "pin":
                    WriteLine(opts, "Pin control is a UDF-firmware-only operation.");
                    return EXIT_USAGE;

                case "eeprom":
                    WriteLine(opts, "Internal device EEPROM is a UDF-firmware-only feature.");
                    return EXIT_USAGE;

                case "debug-dump":
                case "debug-pagewalk":
                    WriteLine(opts, $"'{opts.Command}' is not supported on host SMBus.");
                    return EXIT_USAGE;

                default:
                    Console.Error.WriteLine($"Command '{opts.Command}' is not supported on host SMBus.");
                    return EXIT_USAGE;
            }
        }


        private static int CmdSmScan(DirectSmbusDevice device, CliOptions opts)
        {
            var spds = device.ScanSpdBusAsync(CancellationToken.None)
                             .ConfigureAwait(false).GetAwaiter().GetResult();

            var pmics = new List<byte>();
            for (byte addr = 0x48; addr <= 0x4F; addr++)
            {
                var b = device.ReadByteAsync(addr, 0, CancellationToken.None)
                              .ConfigureAwait(false).GetAwaiter().GetResult();
                if (b != null) pmics.Add(addr);
            }

            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["spd"] = spds.Select(b => $"0x{b:X2}").ToList(),
                    ["pmic"] = pmics.Select(b => $"0x{b:X2}").ToList()
                }) + "\n");
            else
            {
                WriteLine(opts, $"SPD:  {string.Join(" ", spds.Select(b => $"0x{b:X2}"))}");
                WriteLine(opts, $"PMIC: {string.Join(" ", pmics.Select(b => $"0x{b:X2}"))}");
            }
            return EXIT_OK;
        }

        private static int CmdSmDetect(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1)
            {
                Console.Error.WriteLine("usage: detect <address>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[0]);

            var mr0 = device.ReadByteAsync(addr, 0x00, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            string type;
            int size;
            if (mr0 == 0x51)
            {
                type = "DDR5"; size = 1024;
            }
            else
            {
                var b2 = device.ReadByteAsync(addr, 0x02, CancellationToken.None)
                               .ConfigureAwait(false).GetAwaiter().GetResult();
                if (b2 == null) { type = "NotDetected"; size = 0; }
                else if (b2 == 0x0B) { type = "DDR3_Or_Other"; size = 256; }
                else if (b2 == 0x0C ||
                         b2 == 0x0E) { type = "DDR4"; size = 512; }
                else if (b2 >= 0x12 &&
                         b2 <= 0x15) { type = "DDR5"; size = 1024; }
                else { type = "DDR3_Or_Other"; size = 256; }
            }

            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["type"] = type,
                    ["size"] = size
                }) + "\n");
            else
                WriteLine(opts, $"[SPD] address=0x{addr:X2} type={type} size={size}");
            return type == "NotDetected" ? EXIT_NOT_FOUND : EXIT_OK;
        }


        private static int CmdSmSpd(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1)
            {
                Console.Error.WriteLine("usage: spd <subcommand> [args]");
                return EXIT_USAGE;
            }

            switch (opts.Positional[0].ToLowerInvariant())
            {
                case "read":
                case "dump": return CmdSmSpdRead(device, opts);
                case "read-bytes": return CmdSmSpdReadBytes(device, opts);
                case "write": return CmdSmSpdWrite(device, opts);
                case "write-byte": return CmdSmSpdWriteByte(device, opts);
                case "verify": return CmdSmSpdVerify(device, opts);
                case "crc": return CmdSmSpdCrc(device, opts);
                case "fix-crc": return CmdSmSpdFixCrc(device, opts);
                case "test-write": return CmdSmSpdTestWrite(device, opts);
                case "parse": return CmdSmSpdParse(device, opts);
                case "parse-file": return CmdSpdParseFile(opts);
                case "hub-reg": return CmdSmSpdHubReg(device, opts);
                case "pswp": return CmdSmSpdPswp(device, opts);
                default:
                    Console.Error.WriteLine($"Unknown spd subcommand: {opts.Positional[0]}");
                    return EXIT_USAGE;
            }
        }


        private static int CmdSmSpdRead(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: spd read <address> [--out file.bin] [--parsed]");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            var data = device.ReadEntireSpdAsync(addr).ConfigureAwait(false).GetAwaiter().GetResult();
            if (data == null)
            {
                WriteLine(opts, "FAIL: read returned null");
                return EXIT_I2C_ERROR;
            }

            if (opts.Switches.TryGetValue("--out", out var outPath) && !string.IsNullOrEmpty(outPath))
                File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
                WriteRaw(opts, BitConverter.ToString(data).Replace("-", " ") + "\n");
            else if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["size"] = data.Length,
                    ["bytes"] = BitConverter.ToString(data).Replace("-", "")
                }) + "\n");
            else
            {
                WriteLine(opts, $"[SPD] address=0x{addr:X2} size={data.Length}");
                if (opts.Flags.Contains("--parsed"))
                    EmitFullParsedSummary(opts, data);
            }
            return EXIT_OK;
        }

        private static int CmdSmSpdReadBytes(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd read-bytes <address> <offset> <length>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            byte length = byte.Parse(opts.Positional[3]);

            var data = device.ReadEntireSpdAsync(addr).ConfigureAwait(false).GetAwaiter().GetResult();
            if (data == null || offset + length > data.Length)
            {
                WriteLine(opts, "FAIL: read failed or range out of bounds");
                return EXIT_I2C_ERROR;
            }

            byte[] slice = new byte[length];
            Array.Copy(data, offset, slice, 0, length);

            if (opts.Switches.TryGetValue("--out", out var outPath) && !string.IsNullOrEmpty(outPath))
                File.WriteAllBytes(outPath, slice);

            if (opts.OutputFormat == "hex")
                WriteRaw(opts, BitConverter.ToString(slice).Replace("-", " ") + "\n");
            else if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["offset"] = offset,
                    ["length"] = slice.Length,
                    ["bytes"] = BitConverter.ToString(slice).Replace("-", "")
                }) + "\n");
            else
                WriteLine(opts, $"[SPD] 0x{addr:X2} offset={offset} len={slice.Length}: {BitConverter.ToString(slice).Replace("-", " ")}");
            return EXIT_OK;
        }

        private static int CmdSmSpdWrite(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: spd write <address> --in file.bin [--no-verify]");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            if (!opts.Switches.TryGetValue("--in", out var inPath) || string.IsNullOrEmpty(inPath))
            {
                Console.Error.WriteLine("--in required");
                return EXIT_USAGE;
            }

            var data = File.ReadAllBytes(inPath);

            for (ushort i = 0; i < data.Length; i++)
            {
                bool ok = device.WriteSpbByteAsync(addr, i, data[i], CancellationToken.None)
                                .ConfigureAwait(false).GetAwaiter().GetResult();
                if (!ok)
                {
                    WriteLine(opts, $"FAIL: write error at offset {i}");
                    return EXIT_WRITE_ERROR;
                }
            }

            if (!opts.Flags.Contains("--no-verify"))
            {
                var verify = device.ReadEntireSpdAsync(addr).ConfigureAwait(false).GetAwaiter().GetResult();
                if (verify == null || !verify.Take(data.Length).SequenceEqual(data))
                {
                    WriteLine(opts, "FAIL: verify");
                    return EXIT_VERIFY_FAIL;
                }
            }
            WriteLine(opts, "OK");
            return EXIT_OK;
        }

        private static int CmdSmSpdWriteByte(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd write-byte <address> <offset> <value> [--no-verify]");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            byte value = ParseByte(opts.Positional[3]);

            bool ok = device.WriteSpbByteAsync(addr, offset, value, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            if (!ok) { WriteLine(opts, "FAIL"); return EXIT_WRITE_ERROR; }

            if (!opts.Flags.Contains("--no-verify"))
            {
                var rb = device.ReadByteAsync(addr, (byte)(offset & 0xFF), CancellationToken.None)
                               .ConfigureAwait(false).GetAwaiter().GetResult();
                if (rb == null || rb.Value != value)
                {
                    WriteLine(opts, "FAIL: verify");
                    return EXIT_VERIFY_FAIL;
                }
            }
            WriteLine(opts, "OK");
            return EXIT_OK;
        }

        private static int CmdSmSpdVerify(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: spd verify <address> --in file.bin");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            if (!opts.Switches.TryGetValue("--in", out var inPath) || string.IsNullOrEmpty(inPath))
            {
                Console.Error.WriteLine("--in required");
                return EXIT_USAGE;
            }

            var expected = File.ReadAllBytes(inPath);
            var actual = device.ReadEntireSpdAsync(addr).ConfigureAwait(false).GetAwaiter().GetResult();
            bool eq = actual != null && actual.Take(expected.Length).SequenceEqual(expected);
            WriteLine(opts, eq ? "OK" : "FAIL: mismatch");
            return eq ? EXIT_OK : EXIT_VERIFY_FAIL;
        }

        private static int CmdSmSpdCrc(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: spd crc <address>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            var data = device.ReadEntireSpdAsync(addr).ConfigureAwait(false).GetAwaiter().GetResult();
            if (data == null) return EXIT_I2C_ERROR;

            bool isDdr5 = data.Length >= 3 && data[2] >= 0x12 && data[2] <= 0x15;
            bool ok = isDdr5 ? VerifyDdr5Crc(data) : VerifyDdr4Crc(data);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object> { ["crc"] = ok ? "ok" : "fail" }) + "\n");
            else
                WriteLine(opts, ok ? "CRC OK" : "CRC FAIL");
            return ok ? EXIT_OK : EXIT_CRC_ERROR;
        }

        private static int CmdSmSpdFixCrc(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: spd fix-crc <address>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);

            var data = device.ReadEntireSpdAsync(addr).ConfigureAwait(false).GetAwaiter().GetResult();
            if (data == null) { WriteLine(opts, "FAIL: could not read SPD"); return EXIT_I2C_ERROR; }

            byte[] patched = SPDParsedFields.RecalcAndFixCrc(data);
            if (patched == null) { WriteLine(opts, "FAIL: CRC patch returned null"); return EXIT_I2C_ERROR; }

            bool anyFixed = false;
            for (ushort i = 0; i < patched.Length; i++)
            {
                if (patched[i] == data[i]) continue;
                bool ok = device.WriteSpbByteAsync(addr, i, patched[i], CancellationToken.None)
                                .ConfigureAwait(false).GetAwaiter().GetResult();
                if (!ok) { WriteLine(opts, $"FAIL: write at offset {i}"); return EXIT_WRITE_ERROR; }
                anyFixed = true;
            }
            WriteLine(opts, anyFixed ? "OK: CRC patched and written to DIMM" : "CRC was already correct");
            return EXIT_OK;
        }

        private static int CmdSmSpdTestWrite(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: spd test-write <address> <offset>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);

            var rb = device.ReadByteAsync(addr, (byte)(offset & 0xFF), CancellationToken.None)
                           .ConfigureAwait(false).GetAwaiter().GetResult();
            if (rb == null) { WriteLine(opts, "FAIL: read before test"); return EXIT_I2C_ERROR; }

            bool ok = device.WriteSpbByteAsync(addr, offset, rb.Value, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            WriteLine(opts, ok ? "Write test: OK (byte is writable)"
                              : "Write test: FAIL (write-protected or error)");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdSmSpdParse(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: spd parse <address>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            var data = device.ReadEntireSpdAsync(addr).ConfigureAwait(false).GetAwaiter().GetResult();
            if (data == null) { WriteLine(opts, "FAIL: could not read SPD"); return EXIT_I2C_ERROR; }
            return EmitFullParsedSummary(opts, data);
        }

        private static int CmdSmSpdHubReg(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd hub-reg <get|set> <address> <register> [value]");
                return EXIT_USAGE;
            }
            string sub = opts.Positional[1].ToLowerInvariant();
            byte addr = ParseAddress(opts.Positional[2]);
            byte reg = ParseMrName(opts.Positional[3]);

            if (sub == "get")
            {
                var val = device.ReadByteAsync(addr, reg, CancellationToken.None)
                                .ConfigureAwait(false).GetAwaiter().GetResult();
                if (val == null) return EXIT_I2C_ERROR;
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    {
                        ["address"] = $"0x{addr:X2}",
                        ["register"] = $"MR{reg} (0x{reg:X2})",
                        ["value"] = $"0x{val.Value:X2}"
                    }) + "\n");
                else
                    WriteLine(opts, $"hub-reg 0x{addr:X2} MR{reg}=0x{val.Value:X2}");
                return EXIT_OK;
            }
            if (sub == "set")
            {
                if (opts.Positional.Count < 5) { Console.Error.WriteLine("value required"); return EXIT_USAGE; }
                byte value = ParseByte(opts.Positional[4]);
                bool ok = device.WriteByteAsync(addr, reg, value, CancellationToken.None)
                                .ConfigureAwait(false).GetAwaiter().GetResult();
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("hub-reg subcommand must be get or set");
            return EXIT_USAGE;
        }

        private static int CmdSmSpdPswp(DirectSmbusDevice device, CliOptions opts)
        {
            WriteLine(opts, "PSWP get/set requires UDF firmware; not available on host SMBus.");
            return EXIT_OK;
        }


        private static int CmdSmPmic(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1)
            {
                Console.Error.WriteLine("usage: pmic <subcommand> [args]");
                return EXIT_USAGE;
            }
            switch (opts.Positional[0].ToLowerInvariant())
            {
                case "read": return CmdSmPmicRead(device, opts);
                case "reg-read": return CmdSmPmicRegRead(device, opts);
                case "reg-write": return CmdSmPmicRegWrite(device, opts);
                case "type": return CmdSmPmicType(device, opts);
                case "mode": return CmdSmPmicMode(device, opts);
                default:
                    Console.Error.WriteLine($"PMIC subcommand '{opts.Positional[0]}' not supported on host SMBus.");
                    return EXIT_USAGE;
            }
        }

        private static int CmdSmPmicRead(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: pmic read <address> [--out file.bin]");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            var regs = device.ReadAllPmicRegistersAsync(addr, CancellationToken.None)
                              .ConfigureAwait(false).GetAwaiter().GetResult();
            if (regs == null) { WriteLine(opts, "FAIL: read returned null"); return EXIT_I2C_ERROR; }

            if (opts.Switches.TryGetValue("--out", out var outPath) && !string.IsNullOrEmpty(outPath))
                File.WriteAllBytes(outPath, regs);

            if (opts.OutputFormat == "hex")
                WriteRaw(opts, BitConverter.ToString(regs).Replace("-", " ") + "\n");
            else if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["bytes"] = BitConverter.ToString(regs).Replace("-", "")
                }) + "\n");
            else
                WriteLine(opts, $"PMIC 0x{addr:X2}: {regs.Length} bytes read");
            return EXIT_OK;
        }

        private static int CmdSmPmicRegRead(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: pmic reg-read <address> <register>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort reg = ParseRegister(opts.Positional[2]);
            var val = device.ReadPmicRegisterAsync(addr, (byte)reg, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            if (val == null) return EXIT_I2C_ERROR;
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["register"] = $"0x{reg:X2}",
                    ["value"] = $"0x{val.Value:X2}"
                }) + "\n");
            else
                WriteLine(opts, $"reg=0x{reg:X2} value=0x{val.Value:X2}");
            return EXIT_OK;
        }

        private static int CmdSmPmicRegWrite(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: pmic reg-write <address> <register> <value>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort reg = ParseRegister(opts.Positional[2]);
            byte value = ParseByte(opts.Positional[3]);
            bool ok = device.WritePmicRegisterAsync(addr, (byte)reg, value, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdSmPmicType(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: pmic type <address>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);

            var r3b = device.ReadPmicRegisterAsync(addr, 0x3B, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            var r23 = device.ReadPmicRegisterAsync(addr, 0x23, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            string type;
            if (r3b == null || r23 == null)
                type = "Read error";
            else if (r23.Value == 0)
                type = (r3b.Value & 0x40) == 0 ? "PMIC5100" : "PMIC5120";
            else
            {
                int code = ((r3b.Value >> 6) & 0x03) * 2 + (r3b.Value & 0x01);
                switch (code)
                {
                    case 0: type = "PMIC5010"; break;
                    case 1: type = "PMIC5000/5200"; break;
                    case 2:
                    case 3: type = "PMIC5020"; break;
                    default: type = "Unknown"; break;
                }
            }

            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["type"] = type
                }) + "\n");
            else
                WriteLine(opts, $"PMIC 0x{addr:X2}: {type}");
            return EXIT_OK;
        }

        private static int CmdSmPmicMode(DirectSmbusDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2)
            {
                Console.Error.WriteLine("usage: pmic mode <address>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            var r2f = device.ReadPmicRegisterAsync(addr, 0x2F, CancellationToken.None)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
            string mode = r2f != null && (r2f.Value & 0x04) != 0 ? "Programmable" : "Locked";
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["mode"] = mode
                }) + "\n");
            else
                WriteLine(opts, $"PMIC 0x{addr:X2} mode: {mode}");
            return EXIT_OK;
        }

        #endregion

        #region Argument parser

        private static CliOptions ParseArgs(string[] args)
        {
            var opts = new CliOptions();
            int i = 0;
            var positional = new List<string>();

            while (i < args.Length)
            {
                string a = args[i];
                switch (a)
                {
                    case "--port": opts.Port = RequireValue(args, ++i, "--port"); break;
                    case "--auto-detect": opts.AutoDetect = true; break;
                    case "--baud": opts.Baud = int.Parse(RequireValue(args, ++i, "--baud")); break;
                    case "--timeout": opts.TimeoutMs = int.Parse(RequireValue(args, ++i, "--timeout")); break;
                    case "--reconnect-timeout": opts.ReconnectTimeoutSec = int.Parse(RequireValue(args, ++i, "--reconnect-timeout")); break;
                    case "--output-format":
                        {
                            string v = RequireValue(args, ++i, "--output-format");
                            if (v != "text" && v != "json" && v != "hex")
                                throw new ArgumentException("--output-format must be text|json|hex");
                            opts.OutputFormat = v;
                            break;
                        }
                    case "--quiet": opts.Quiet = true; break;
                    case "--smbus": opts.UseSmbus = true; break;
                    case "--log-file": opts.LogFile = RequireValue(args, ++i, "--log-file"); break;

                    case "--out":
                    case "--in":
                    case "--block":
                    case "--lsb":
                    case "--msb":
                    case "--cur-lsb":
                    case "--cur-msb":
                    case "--new-lsb":
                    case "--new-msb":
                    case "--offset":
                    case "--length":
                    case "--value":
                    case "--register":
                    case "--mode":
                    case "--force-gen":
                    case "--uf2":
                        opts.Switches[a] = RequireValue(args, ++i, a);
                        break;

                    case "--no-verify":
                    case "--parsed":
                    case "--ticks":
                    case "--full":
                    case "--enable":
                    case "--disable":
                    case "--use-i3c":
                        opts.Flags.Add(a);
                        break;

                    default:
                        positional.Add(a);
                        break;
                }
                i++;
            }

            if (positional.Count > 0)
            {
                opts.Command = positional[0];
                for (int p = 1; p < positional.Count; p++) opts.Positional.Add(positional[p]);
            }
            return opts;
        }

        private static string RequireValue(string[] args, int idx, string optName)
        {
            if (idx >= args.Length) throw new ArgumentException($"{optName} requires a value");
            return args[idx];
        }

        private static byte ParseAddress(string s)
        {
            if (string.IsNullOrEmpty(s)) throw new ArgumentException("address required");
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToByte(s.Substring(2), 16);
            return Convert.ToByte(s, 16);
        }

        private static byte ParseByte(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToByte(s.Substring(2), 16);
            if (s.All(char.IsDigit))
                return byte.Parse(s);
            return Convert.ToByte(s, 16);
        }

        private static ushort ParseRegister(string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt16(s.Substring(2), 16);
            if (s.All(char.IsDigit))
                return ushort.Parse(s);
            return Convert.ToUInt16(s, 16);
        }

        #endregion

        #region Output helpers

        private static void WriteLine(CliOptions opts, string line)
        {
            if (!opts.Quiet) Console.WriteLine(line);
            if (!string.IsNullOrEmpty(opts.LogFile))
                try { File.AppendAllText(opts.LogFile, line + Environment.NewLine); } catch { }
        }

        private static void WriteRaw(CliOptions opts, string s)
        {
            Console.Write(s);
            if (!string.IsNullOrEmpty(opts.LogFile))
                try { File.AppendAllText(opts.LogFile, s); } catch { }
        }

        private static string ToJson(object o)
        {
            var sb = new StringBuilder();
            EmitJson(sb, o, 0);
            return sb.ToString();
        }

        private static void EmitJson(StringBuilder sb, object o, int depth)
        {
            string indent = new string(' ', depth * 2);
            string indent1 = new string(' ', (depth + 1) * 2);
            if (o == null) { sb.Append("null"); return; }
            if (o is string s) { sb.Append('"').Append(JsonEscape(s)).Append('"'); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is byte || o is sbyte || o is short || o is ushort ||
                o is int || o is uint || o is long || o is ulong)
            { sb.Append(o.ToString()); return; }
            if (o is double d) { sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (o is float f) { sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (o is IDictionary<string, object> dict)
            {
                sb.Append("{\n");
                int idx = 0;
                foreach (var kv in dict)
                {
                    sb.Append(indent1).Append('"').Append(JsonEscape(kv.Key)).Append("\": ");
                    EmitJson(sb, kv.Value, depth + 1);
                    if (idx++ < dict.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(indent).Append('}');
                return;
            }
            if (o is System.Collections.IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(", ");
                    EmitJson(sb, item, depth + 1);
                    first = false;
                }
                sb.Append(']');
                return;
            }
            sb.Append('"').Append(JsonEscape(o.ToString())).Append('"');
        }

        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        #endregion

        #region Basic / diagnostic commands

        private static int CmdPing(UDFDevice device, string port, CliOptions opts)
        {
            bool ok = device.Ping();
            uint ver = device.GetVersion();
            if (opts.OutputFormat == "json")
            {
                var obj = new Dictionary<string, object>
                {
                    ["status"] = ok ? "ok" : "fail",
                    ["port"] = port,
                    ["firmware"] = $"0x{ver:X8}"
                };
                WriteRaw(opts, ToJson(obj) + "\n");
            }
            else
            {
                WriteLine(opts, $"[UDF] firmware=0x{ver:X8} port={port}");
                WriteLine(opts, ok ? "OK" : "FAIL");
            }
            return ok ? EXIT_OK : EXIT_NOT_FOUND;
        }

        private static int CmdVersion(UDFDevice device, string port, CliOptions opts)
        {
            uint ver = device.GetVersion();
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object> { ["firmware"] = $"0x{ver:X8}" }) + "\n");
            else
                WriteLine(opts, $"firmware=0x{ver:X8}");
            return EXIT_OK;
        }

        private static int CmdTest(UDFDevice device, CliOptions opts)
        {
            bool ok = device.Test();
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object> { ["self_test"] = ok ? "pass" : "fail" }) + "\n");
            else
                WriteLine(opts, ok ? "Self-test: PASS" : "Self-test: FAIL");
            return ok ? EXIT_OK : EXIT_I2C_ERROR;
        }

        private static int CmdName(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count == 0 || opts.Positional[0] == "get")
            {
                string name = device.GetDeviceName();
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object> { ["name"] = name }) + "\n");
                else
                    WriteLine(opts, name);
                return EXIT_OK;
            }
            if (opts.Positional[0] == "set" && opts.Positional.Count >= 2)
            {
                bool ok = device.SetDeviceName(opts.Positional[1]);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("usage: name [get|set <name>]");
            return EXIT_USAGE;
        }

        private static int CmdI2cSpeed(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count == 0)
            {
                byte mode = device.GetI2CClockMode();
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["mode"] = mode, ["label"] = SpeedLabel(mode) }) + "\n");
                else
                    WriteLine(opts, $"i2c-speed={mode} ({SpeedLabel(mode)})");
                return EXIT_OK;
            }
            byte newMode = byte.Parse(opts.Positional[0]);
            bool set = device.SetI2CClockMode(newMode);
            WriteLine(opts, set ? $"OK i2c-speed={newMode} ({SpeedLabel(newMode)})" : "FAIL");
            return set ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static string SpeedLabel(byte mode) =>
            mode == 0 ? "100kHz" : mode == 1 ? "400kHz" : mode == 2 ? "1MHz" : "?";

        private static int CmdScan(UDFDevice device, CliOptions opts)
        {
            var spds = device.ScanBus();
            var pmics = new List<byte>();
            for (byte a = 0x48; a <= 0x4F; a++)
                if (device.ProbeAddress(a)) pmics.Add(a);

            if (opts.OutputFormat == "json")
            {
                var obj = new Dictionary<string, object>
                {
                    ["spd"] = spds.Select(b => $"0x{b:X2}").ToList(),
                    ["pmic"] = pmics.Select(b => $"0x{b:X2}").ToList()
                };
                WriteRaw(opts, ToJson(obj) + "\n");
            }
            else
            {
                WriteLine(opts, $"SPD:  {string.Join(" ", spds.Select(b => $"0x{b:X2}"))}");
                WriteLine(opts, $"PMIC: {string.Join(" ", pmics.Select(b => $"0x{b:X2}"))}");
            }
            return EXIT_OK;
        }

        private static int CmdDetect(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: detect <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[0]);
            var info = device.DetectModule(addr);
            if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["type"] = info.Type.ToString(),
                    ["size"] = info.Size
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"[SPD] address=0x{addr:X2} type={info.Type} size={info.Size}");
            }
            return info.Type == ModuleType.NotDetected ? EXIT_NOT_FOUND : EXIT_OK;
        }

        private static int CmdRswpSupport(UDFDevice device, CliOptions opts)
        {
            var sup = device.GetRSWPSupport();
            if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["ddr5"] = sup.DDR5Supported,
                    ["ddr4"] = sup.DDR4Supported,
                    ["ddr3"] = sup.DDR3Supported
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"RSWP support: {sup}");
            }
            return EXIT_OK;
        }

        private static int CmdRebootDimm(UDFDevice device, CliOptions opts)
        {
            byte verify = 0;
            if (opts.Positional.Count > 0)
                verify = ParseAddress(opts.Positional[0]);

            WriteLine(opts, verify != 0
                ? $"Power-cycling DIMM (verifying via 0x{verify:X2})..."
                : "Power-cycling DIMM...");

            byte status = device.RebootDIMM(verify, 2000);
            switch (status)
            {
                case 1:
                    WriteLine(opts, "OK - module power-cycled and responding");
                    return EXIT_OK;
                case 4:
                    WriteLine(opts, "OK - cycle sent (old firmware, not verified)");
                    return EXIT_OK;
                case 2:
                    WriteLine(opts, "FAIL - module never lost power (VIN switch ineffective or back-powered)");
                    return EXIT_WRITE_ERROR;
                case 3:
                    WriteLine(opts, "Cycled, but the module has not responded yet");
                    return EXIT_WRITE_ERROR;
                default:
                    WriteLine(opts, "FAIL");
                    return EXIT_WRITE_ERROR;
            }
        }

        private static int CmdFactoryReset(UDFDevice device, CliOptions opts)
        {
            bool ok = device.FactoryReset();
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        #endregion

        #region Debug dump

        private static int CmdDebugDump(UDFDevice device, string port, CliOptions opts)
        {
            string outPath = null;
            if (opts.Switches.TryGetValue("--out", out var o)) outPath = o;
            if (string.IsNullOrEmpty(outPath))
            {
                outPath = $"udf-debug-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            }

            byte? targetAddr = null;
            if (opts.Positional.Count > 0)
            {
                try { targetAddr = ParseAddress(opts.Positional[0]); }
                catch { Console.Error.WriteLine("Invalid address; ignoring."); }
            }

            var ctx = new DebugDumpContext(outPath);
            try
            {
                ctx.Open();
                RunDebugDump(device, port, opts, targetAddr, ctx);
            }
            catch (Exception ex)
            {
                ctx.Section("FATAL");
                ctx.Line($"Unhandled exception: {ex.GetType().Name}: {ex.Message}");
                ctx.Line(ex.StackTrace ?? "(no stack)");
            }
            finally
            {
                ctx.Close();
            }

            try { Console.WriteLine($"Debug dump written to: {Path.GetFullPath(outPath)}"); }
            catch { }
            return EXIT_OK;
        }

        private class DebugDumpContext
        {
            private readonly string _path;
            private StreamWriter _w;
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            public DebugDumpContext(string path) { _path = path; }
            public void Open()
            {
                _w = new StreamWriter(_path, append: false, encoding: new UTF8Encoding(false));
                _w.AutoFlush = true;
            }
            public void Close()
            {
                try { _w?.Flush(); _w?.Dispose(); } catch { }
            }
            public void Line(string s)
            {
                if (_w == null) return;
                _w.WriteLine(s);
            }
            public void Section(string title)
            {
                if (_w == null) return;
                _w.WriteLine();
                _w.WriteLine("================================================================================");
                _w.WriteLine($"== {title}");
                _w.WriteLine("================================================================================");
            }
            public void Sub(string title)
            {
                if (_w == null) return;
                _w.WriteLine();
                _w.WriteLine($"-- {title} " + new string('-', Math.Max(0, 75 - title.Length)));
            }
            public long Stamp(string label)
            {
                long ms = _sw.ElapsedMilliseconds;
                Line($"[+{ms,6} ms] {label}");
                return ms;
            }
        }

        private static void HexDumpToCtx(DebugDumpContext ctx, byte[] data, ushort baseOffset = 0)
        {
            if (data == null) { ctx.Line("    (null)"); return; }
            if (data.Length == 0) { ctx.Line("    (empty)"); return; }

            for (int row = 0; row < data.Length; row += 16)
            {
                int rowLen = Math.Min(16, data.Length - row);
                var hex = new StringBuilder();
                var asc = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    if (i < rowLen)
                    {
                        byte b = data[row + i];
                        hex.AppendFormat("{0:X2} ", b);
                        asc.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                    }
                    else
                    {
                        hex.Append("   ");
                        asc.Append(' ');
                    }
                    if (i == 7) hex.Append(' ');
                }
                ctx.Line($"    {(baseOffset + row):X4}  {hex}  {asc}");
            }
        }

        private static T Safe<T>(DebugDumpContext ctx, string label, Func<T> op, T fallback = default)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                T v = op();
                sw.Stop();
                ctx.Line($"  [{sw.ElapsedMilliseconds,5} ms] {label} = {DumpValue(v)}");
                return v;
            }
            catch (Exception ex)
            {
                ctx.Line($"  [ ERR  ] {label}: {ex.GetType().Name}: {ex.Message}");
                return fallback;
            }
        }

        private static string DumpValue(object v)
        {
            if (v == null) return "<null>";
            if (v is bool b) return b ? "true" : "false";
            if (v is byte by) return $"0x{by:X2} ({by})";
            if (v is byte[] arr)
            {
                if (arr.Length == 0) return "<empty>";
                var preview = new StringBuilder();
                preview.Append('[').Append(arr.Length).Append(" bytes]");
                int n = Math.Min(arr.Length, 8);
                preview.Append(' ');
                for (int i = 0; i < n; i++) preview.AppendFormat("{0:X2} ", arr[i]);
                if (arr.Length > n) preview.Append("...");
                return preview.ToString().TrimEnd();
            }
            if (v is List<byte> lb)
            {
                var preview = new StringBuilder();
                preview.Append('[').Append(lb.Count).Append("] ");
                foreach (var x in lb.Take(16)) preview.AppendFormat("0x{0:X2} ", x);
                if (lb.Count > 16) preview.Append("...");
                return preview.ToString().TrimEnd();
            }
            return v.ToString();
        }

        private static void RunDebugDump(UDFDevice device, string port, CliOptions opts, byte? targetAddr, DebugDumpContext ctx)
        {
            ctx.Section("ENVIRONMENT");
            ctx.Line($"Generated:           {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            ctx.Line($"Tool:                Unified DDR Flasher v4.0.0 (debug-dump command)");
            ctx.Line($"Operating System:    {Environment.OSVersion}");
            ctx.Line($"Process bitness:     {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
            ctx.Line($"CLR version:         {Environment.Version}");
            ctx.Line($"Working directory:   {Environment.CurrentDirectory}");
            ctx.Line($"Command line:        {Environment.CommandLine}");
            ctx.Line($"Port:                {port}");
            ctx.Line($"Baud:                {opts.Baud}");
            ctx.Line($"Per-cmd timeout ms:  {opts.TimeoutMs}");
            if (targetAddr.HasValue) ctx.Line($"Target address:      0x{targetAddr.Value:X2} (deep-dive only)");
            else ctx.Line($"Target address:      (all of 0x50..0x57)");

            ctx.Section("DEVICE / FIRMWARE");
            Safe(ctx, "Ping", () => device.Ping());
            uint fwVer = Safe(ctx, "GetVersion (raw)", () => device.GetVersion());
            try
            {
                int y = (int)(fwVer / 10000), m = (int)((fwVer / 100) % 100), d = (int)(fwVer % 100);
                if (y >= 2020 && y <= 2099 && m >= 1 && m <= 12 && d >= 1 && d <= 31)
                    ctx.Line($"  GetVersion decoded: {y:D4}-{m:D2}-{d:D2}");
                else
                    ctx.Line($"  GetVersion decoded: <not a date code> ({fwVer})");
            }
            catch { }
            Safe(ctx, "Test (CMD_TEST)", () => device.Test());
            Safe(ctx, "GetDeviceName", () => device.GetDeviceName());
            Safe(ctx, "GetI2CClockMode", () => device.GetI2CClockMode());
            Safe(ctx, "GetRSWPSupport", () => device.GetRSWPSupport()?.ToString());

            ctx.Sub("Internal device EEPROM (256 bytes)");
            try
            {
                var ee = new byte[256];
                int gotEE = 0;
                for (ushort off = 0; off < 256; off += 32)
                {
                    try
                    {
                        var chunk = device.ReadInternalEEPROM(off, 32);
                        if (chunk != null && chunk.Length == 32)
                        {
                            Buffer.BlockCopy(chunk, 0, ee, off, 32);
                            gotEE += 32;
                        }
                        else
                        {
                            ctx.Line($"  ReadInternalEEPROM(off={off}, len=32): null/short (got {chunk?.Length ?? 0})");
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Line($"  ReadInternalEEPROM(off={off}, len=32): {ex.Message}");
                    }
                }
                ctx.Line($"  Bytes successfully read: {gotEE}/256");
                HexDumpToCtx(ctx, ee, 0);
            }
            catch (Exception ex) { ctx.Line($"  Internal EEPROM dump exception: {ex.Message}"); }

            ctx.Sub("Pin states (CMD_PIN_CONTROL --get)");
            string[] pinNames = { "HV_SWITCH", "SA1_SWITCH", "DEV_STATUS", "HV_CONVERTER",
                                  "DDR5_VIN_CTRL", "PMIC_CTRL", "PMIC_FLAG", "RFU1" };
            for (byte p = 0; p < pinNames.Length; p++)
            {
                byte pin = p;
                Safe(ctx, $"GetPin({pinNames[p]}) [#{p}]", () => device.GetPin(pin));
            }

            ctx.Section("BUS TOPOLOGY");
            ctx.Sub("Firmware-side scan (CMD_SCAN_BUS - only 0x50..0x57)");
            var fwScan = Safe(ctx, "ScanBus", () => device.ScanBus());
            if (fwScan != null)
                foreach (var a in fwScan) ctx.Line($"    found: 0x{a:X2}");

            ctx.Sub("Manual probe (CMD_PROBE_ADDRESS) across 0x08..0x77");
            var responsive = new List<byte>();
            for (int a = 0x08; a <= 0x77; a++)
            {
                try
                {
                    if (device.ProbeAddress((byte)a)) { responsive.Add((byte)a); ctx.Line($"    0x{a:X2}: ACK"); }
                }
                catch (Exception ex)
                {
                    ctx.Line($"    0x{a:X2}: probe exception: {ex.Message}");
                    break;
                }
            }
            ctx.Line($"  Total responsive: {responsive.Count}");

            ctx.Section("SPD ADDRESSES");
            for (byte addr = 0x50; addr <= 0x57; addr++)
            {
                if (targetAddr.HasValue && addr != targetAddr.Value) continue;
                DumpSpdAddress(device, addr, ctx);
            }

            ctx.Section("PMIC ADDRESSES");
            for (byte addr = 0x48; addr <= 0x4F; addr++)
            {
                bool present = false;
                try { present = device.ProbeAddress(addr); } catch { }
                if (!present) { ctx.Sub($"PMIC 0x{addr:X2} - not present"); continue; }
                DumpPmicAddress(device, addr, ctx);
            }

            ctx.Section("END OF DUMP");
            ctx.Stamp("Total elapsed");
        }

        private static void DumpSpdAddress(UDFDevice device, byte addr, DebugDumpContext ctx)
        {
            ctx.Sub($"SPD address 0x{addr:X2}");

            bool present = Safe(ctx, "ProbeAddress", () => device.ProbeAddress(addr));
            if (!present) { ctx.Line("  No device responds at this address - skipping."); return; }

            ctx.Line("");
            ctx.Line("  Detection probes (independent - each called fresh):");
            try { device.InvalidateModuleCache(addr); } catch { }
            Safe(ctx, "  CMD_DDR4_DETECT", () => device.DetectDDR4(addr));
            try { device.InvalidateModuleCache(addr); } catch { }
            Safe(ctx, "  CMD_DDR5_DETECT", () => device.DetectDDR5(addr));

            ctx.Line("");
            ctx.Line("  Direct SPD5 hub register reads (CMD_SPD5_HUB_REG):");
            Safe(ctx, "  MR0  (Device Type MSB, expect 0x51)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR0));
            Safe(ctx, "  MR1  (Device Type LSB, expect 0x18 or 0x10)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR1));
            Safe(ctx, "  MR2  (Device Revision)", () => device.ReadSPD5HubRegister(addr, 0x02));
            Safe(ctx, "  MR3  (Vendor ID Byte 0)", () => device.ReadSPD5HubRegister(addr, 0x03));
            Safe(ctx, "  MR4  (Vendor ID Byte 1)", () => device.ReadSPD5HubRegister(addr, 0x04));
            Safe(ctx, "  MR5  (Capability)", () => device.ReadSPD5HubRegister(addr, 0x05));
            Safe(ctx, "  MR6  (Write Recovery Time)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR6));
            Safe(ctx, "  MR11 (I2C Legacy Mode Cfg / page pointer)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR11));
            Safe(ctx, "  MR12 (WP Block 7..0)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR12));
            Safe(ctx, "  MR13 (WP Block 15..8)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR13));
            Safe(ctx, "  MR14 (Local Interface)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR14));
            Safe(ctx, "  MR18 (Device Config - PEC/PAR/I3C/RDPTR)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR18));
            Safe(ctx, "  MR20 (Clear Reg Status)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR20));
            Safe(ctx, "  MR48 (Status / In-progress flags)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR48));
            Safe(ctx, "  MR52 (Error/Interrupt Status)", () => device.ReadSPD5HubRegister(addr, UDFDevice.MR52));

            try
            {
                byte mr18 = device.ReadSPD5HubRegister(addr, UDFDevice.MR18);
                ctx.Line("");
                ctx.Line($"  MR18 decoded ({mr18:X2}h):");
                ctx.Line($"    [7] PEC_EN          = {((mr18 >> 7) & 1)}  (1 = PEC required - host-side breaker)");
                ctx.Line($"    [6] PAR_DIS         = {((mr18 >> 6) & 1)}  (1 = parity disabled, 0 = required in I3C)");
                ctx.Line($"    [5] INF_SEL         = {((mr18 >> 5) & 1)}  (1 = I3C latched - REQUIRES POWER CYCLE TO CLEAR)");
                ctx.Line($"    [4] DEF_RD_ADDR_EN  = {((mr18 >> 4) & 1)}  (1 = reads return MR49-pointed data)");
                ctx.Line($"    [3:2] RDPTR_START   = {((mr18 >> 2) & 3)}");
                ctx.Line($"    [1] RDPTR_BL        = {((mr18 >> 1) & 1)}");
            }
            catch { }

            ctx.Line("");
            ctx.Line("  Full MR0..MR127 dump (CMD_SPD5_HUB_REG, one byte at a time):");
            try
            {
                var allMr = new byte[128];
                int gotMr = 0;
                for (byte r = 0; r < 128; r++)
                {
                    try { allMr[r] = device.ReadSPD5HubRegister(addr, r); gotMr++; }
                    catch { allMr[r] = 0xFF; }
                }
                ctx.Line($"    Reads succeeded: {gotMr}/128");
                HexDumpToCtx(ctx, allMr, 0);
            }
            catch (Exception ex) { ctx.Line($"    MR walk exception: {ex.Message}"); }

            ctx.Line("");
            ctx.Line("  Firmware size probe (CMD_SPD_SIZE):");
            Safe(ctx, "  GetSPDSizeCode", () => device.GetSPDSizeCode(addr));
            Safe(ctx, "  GetSPDSizeBytes", () => device.GetSPDSizeBytes(addr));

            ctx.Line("");
            ctx.Line("  Final DetectModule (host-side, full pipeline):");
            try { device.InvalidateModuleCache(addr); } catch { }
            ModuleInfo info = null;
            try { info = device.DetectModule(addr); }
            catch (Exception ex) { ctx.Line($"    DetectModule exception: {ex.Message}"); }
            if (info != null)
            {
                ctx.Line($"    Type:                   {info.Type}");
                ctx.Line($"    Size:                   {info.Size} bytes");
                ctx.Line($"    DetectedViaFallback:    {info.DetectedViaFallback}");
            }

            ctx.Line("");
            ctx.Line("  Raw SPD reads (CMD_SPD_READ_PAGE - addresses passed verbatim):");
            try
            {
                var raw256 = ReadRawSpdRange(device, addr, 0, 256);
                ctx.Line($"    Raw read 0x000..0x0FF: {raw256?.Length ?? 0} bytes");
                HexDumpToCtx(ctx, raw256, 0);
            }
            catch (Exception ex) { ctx.Line($"    Raw 256 exception: {ex.Message}"); }

            try
            {
                var raw512 = ReadRawSpdRange(device, addr, 0, 512);
                ctx.Line($"");
                ctx.Line($"    Raw read 0x000..0x1FF: {raw512?.Length ?? 0} bytes");
                if (raw512 != null && raw512.Length > 256)
                {
                    ctx.Line("    (second half only - first half identical to above)");
                    HexDumpToCtx(ctx, raw512.Skip(256).ToArray(), 256);
                }
            }
            catch (Exception ex) { ctx.Line($"    Raw 512 exception: {ex.Message}"); }

            ctx.Line("");
            ctx.Line("  DDR5 page-walked SPD read (forced, regardless of detection):");
            try
            {
                var ddr5Buf = new List<byte>();
                bool walkOk = true;
                for (byte page = 0; page < 8 && walkOk; page++)
                {
                    bool wrote;
                    try { wrote = device.WriteSPD5HubRegister(addr, UDFDevice.MR11, page); }
                    catch (Exception ex) { ctx.Line($"    page {page}: WriteSPD5HubRegister(MR11) exception: {ex.Message}"); walkOk = false; break; }
                    ctx.Line($"    page {page}: WriteSPD5HubRegister(MR11, {page}) = {wrote}");
                    if (!wrote) { walkOk = false; break; }
                    System.Threading.Thread.Sleep(10);

                    var pageBytes = new List<byte>();
                    int pageOffsetBase = page * 128;
                    for (int off = 0; off < 128; off += 32)
                    {
                        try
                        {
                            var chunk = device.ReadSPD(addr, (ushort)(pageOffsetBase + off), 32);
                            if (chunk == null) { ctx.Line($"      offset 0x{(pageOffsetBase + off):X3}: NULL response"); walkOk = false; break; }
                            pageBytes.AddRange(chunk);
                        }
                        catch (Exception ex)
                        {
                            ctx.Line($"      offset 0x{(pageOffsetBase + off):X3}: exception: {ex.Message}");
                            walkOk = false; break;
                        }
                    }
                    ctx.Line($"      page {page} bytes captured: {pageBytes.Count}");
                    ddr5Buf.AddRange(pageBytes);
                }
                if (ddr5Buf.Count > 0)
                {
                    ctx.Line($"    Total DDR5-walked bytes: {ddr5Buf.Count}");
                    HexDumpToCtx(ctx, ddr5Buf.ToArray(), 0);
                }
            }
            catch (Exception ex) { ctx.Line($"    DDR5 walk exception: {ex.Message}"); }

            ctx.Line("");
            ctx.Line("  ReadEntireSPD (the path Read SPD button uses):");
            try
            {
                var entire = device.ReadEntireSPD(addr);
                ctx.Line($"    Returned: {(entire == null ? "NULL" : entire.Length + " bytes")}");
                if (entire != null)
                {
                    HexDumpToCtx(ctx, entire, 0);
                    ctx.Line("");
                    ctx.Line("  SPDParsedFields.Parse output:");
                    try
                    {
                        var parsed = SPDParsedFields.Parse(entire);
                        ctx.Line($"    DRAM type detected by parser: {parsed.DramType}");
                        foreach (var f in parsed.Fields)
                        {
                            string lbl = (f.Label ?? "").TrimEnd();
                            string val = f.Value ?? "";
                            if (string.IsNullOrEmpty(val)) ctx.Line($"    {lbl}");
                            else ctx.Line($"    {lbl,-44}{val}");
                        }
                    }
                    catch (Exception ex) { ctx.Line($"    Parser exception: {ex.Message}"); }
                }
            }
            catch (Exception ex) { ctx.Line($"    ReadEntireSPD exception: {ex.Message}"); }

            if (info != null && info.Type == ModuleType.DDR5)
            {
                ctx.Line("");
                ctx.Line("  RSWP block status (DDR5):");
                for (byte block = 0; block < 16; block++)
                {
                    byte b = block;
                    Safe(ctx, $"    GetRSWP(block {b})", () => device.GetRSWP(addr, b));
                }
            }
        }

        private static byte[] ReadRawSpdRange(UDFDevice device, byte addr, ushort startOffset, int length)
        {
            const int chunk = 32;
            var all = new List<byte>();
            for (int o = 0; o < length; o += chunk)
            {
                int n = Math.Min(chunk, length - o);
                byte[] r;
                try { r = device.ReadSPD(addr, (ushort)(startOffset + o), (byte)n); }
                catch { return all.Count > 0 ? all.ToArray() : null; }
                if (r == null || r.Length != n) return all.Count > 0 ? all.ToArray() : null;
                all.AddRange(r);
            }
            return all.ToArray();
        }

        private static void DumpPmicAddress(UDFDevice device, byte addr, DebugDumpContext ctx)
        {
            ctx.Sub($"PMIC address 0x{addr:X2}");

            Safe(ctx, "ProbeAddress", () => device.ProbeAddress(addr));
            Safe(ctx, "GetPMICType", () => device.GetPMICType(addr));
            Safe(ctx, "GetPMICMode", () => device.GetPMICMode(addr));
            Safe(ctx, "GetPMICPGoodStatus", () => device.GetPMICPGoodStatus(addr));
            Safe(ctx, "IsVendorRegionUnlocked", () => device.IsVendorRegionUnlocked(addr));
            Safe(ctx, "GetProgMode", () => device.GetProgMode(addr));
            Safe(ctx, "GetVRegEnabled", () => device.GetVRegEnabled(addr));

            ctx.Line("");
            ctx.Line("  Full PMIC register dump (256 bytes, CMD_PMIC_READREG):");
            var pmicDump = new byte[256];
            int got = 0;
            for (int r = 0; r < 256; r++)
            {
                try
                {
                    var resp = device.ReadPMICDevice(addr, (ushort)r);
                    if (resp != null && resp.Length >= 1) { pmicDump[r] = resp[0]; got++; }
                    else pmicDump[r] = 0xFF;
                }
                catch { pmicDump[r] = 0xFF; }
            }
            ctx.Line($"    Reads succeeded: {got}/256");
            HexDumpToCtx(ctx, pmicDump, 0);
        }

        #endregion

        #region Debug page-walk

        private static int CmdDebugPageWalk(UDFDevice device, string port, CliOptions opts)
        {
            string outPath = null;
            if (opts.Switches.TryGetValue("--out", out var o)) outPath = o;
            if (string.IsNullOrEmpty(outPath))
                outPath = $"udf-pagewalk-{DateTime.Now:yyyyMMdd-HHmmss}.txt";

            byte addr = 0x50;
            if (opts.Positional.Count > 0)
            {
                try { addr = ParseAddress(opts.Positional[0]); }
                catch { Console.Error.WriteLine("Invalid address; using 0x50."); }
            }

            var ctx = new DebugDumpContext(outPath);
            try
            {
                ctx.Open();
                RunPageWalk(device, port, opts, addr, ctx);
            }
            catch (Exception ex)
            {
                ctx.Section("FATAL");
                ctx.Line($"Unhandled exception: {ex.GetType().Name}: {ex.Message}");
                ctx.Line(ex.StackTrace ?? "(no stack)");
            }
            finally
            {
                ctx.Close();
            }

            try { Console.WriteLine($"Page-walk dump written to: {Path.GetFullPath(outPath)}"); }
            catch { }
            return EXIT_OK;
        }

        private static void RunPageWalk(UDFDevice device, string port, CliOptions opts, byte addr, DebugDumpContext ctx)
        {
            ctx.Section("PAGE-WALK DEBUG (transparent / flat-memory)");
            ctx.Line($"Generated:           {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            ctx.Line($"Tool:                Unified DDR Flasher v4.0.0 (debug-pagewalk command)");
            ctx.Line($"Port:                {port}");
            ctx.Line($"Baud:                {opts.Baud}");
            ctx.Line($"Per-cmd timeout ms:  {opts.TimeoutMs}");
            ctx.Line($"Target address:      0x{addr:X2}");
            ctx.Line("");
            ctx.Line("This command treats the SPD5 hub as a flat 256-byte memory array,");
            ctx.Line("just like the PMIC. The transactions are transparent - every read");
            ctx.Line("is one I2C write of (offset_byte) followed by one I2C read of N bytes.");
            ctx.Line("No detection, no MemReg OR-in, no auto-MR11 - what's on the wire is");
            ctx.Line("what you see in this file.");
            ctx.Line("");
            ctx.Line("SPD5118 memory map:");
            ctx.Line("  0x00..0x7F  MR space (mode registers)");
            ctx.Line("  0x80..0xFF  NVM page selected by MR11 (currently 0..7)");
            ctx.Line("");
            ctx.Line($"Firmware version:    {Safe(ctx, "GetVersion", () => device.GetVersion())}");
            ctx.Line($"I2C clock mode:      {Safe(ctx, "GetI2CClockMode", () => device.GetI2CClockMode())}");

            ctx.Sub("Step 1 - Probe address");
            bool probed = Safe(ctx, "ProbeAddress", () => device.ProbeAddress(addr));
            if (!probed)
            {
                ctx.Line("");
                ctx.Line("  Address NACK'd. Cannot continue page-walk.");
                ctx.Stamp("Total elapsed");
                return;
            }

            ctx.Sub("Step 2 - Read entire 256-byte memory map (no MR11 touch yet)");
            ctx.Line("  (0x00..0x7F = MR space, 0x80..0xFF = current NVM page)");
            ctx.Line("");
            byte[] flat256 = ReadFlatRange(device, addr, 0, 256, ctx);
            if (flat256 != null)
            {
                ctx.Line($"  Got {flat256.Length} bytes:");
                HexDumpToCtx(ctx, flat256, 0);
            }

            ctx.Sub("Step 3 - Explicit page walk: MR11 = 0..7, read 0x80..0xFF each");
            for (byte page = 0; page < 8; page++)
            {
                ctx.Line("");
                ctx.Line($"--- Page {page} ---");

                Safe(ctx, $"  WriteSPD5HubRegister(MR11, {page})",
                    () => device.WriteSPD5HubRegister(addr, UDFDevice.MR11, page));

                System.Threading.Thread.Sleep(5);

                ctx.Line($"  Reading 128 bytes from offset 0x80 (NVM window for page {page}):");
                byte[] nvmWindow = ReadFlatRange(device, addr, 0x80, 128, ctx);
                if (nvmWindow != null)
                {
                    ctx.Line($"     ✓ {nvmWindow.Length} bytes captured:");
                    HexDumpToCtx(ctx, nvmWindow, (ushort)(page * 128));
                }
            }

            ctx.Sub("Step 4 - Reset MR11 to page 0 (good citizenship)");
            Safe(ctx, "  WriteSPD5HubRegister(MR11, 0)",
                () => device.WriteSPD5HubRegister(addr, UDFDevice.MR11, 0));

            ctx.Section("END OF PAGE-WALK");
            ctx.Stamp("Total elapsed");
        }

        private static byte[] ReadFlatRange(UDFDevice device, byte addr, byte startOffset, int length, DebugDumpContext ctx)
        {
            const int chunk = 64;
            var all = new List<byte>(length);
            for (int o = 0; o < length; o += chunk)
            {
                int n = Math.Min(chunk, length - o);
                byte off = (byte)(startOffset + o);
                byte[] r;
                try { r = device.ReadRawBytes(addr, off, (byte)n); }
                catch (Exception ex)
                {
                    ctx.Line($"     offset 0x{off:X2}: exception: {ex.Message}");
                    return null;
                }
                if (r == null || r.Length != n)
                {
                    ctx.Line($"     offset 0x{off:X2}: NULL/short (got {r?.Length ?? 0}/{n})");
                    return all.Count > 0 ? all.ToArray() : null;
                }
                all.AddRange(r);
            }
            return all.ToArray();
        }

        #endregion

        #region SPD command (full coverage)

        private static int CmdSpd(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: spd <subcommand> [args]"); return EXIT_USAGE; }

            switch (opts.Positional[0].ToLowerInvariant())
            {
                case "read":
                case "dump": return CmdSpdRead(device, opts);
                case "read-bytes": return CmdSpdReadBytes(device, opts);
                case "write": return CmdSpdWrite(device, opts);
                case "write-byte": return CmdSpdWriteByte(device, opts);
                case "verify": return CmdSpdVerify(device, opts);
                case "crc": return CmdSpdCrc(device, opts);
                case "fix-crc": return CmdSpdFixCrc(device, opts);
                case "test-write": return CmdSpdTestWrite(device, opts);
                case "parse": return CmdSpdParse(device, opts);
                case "parse-file": return CmdSpdParseFile(opts);
                case "hub-reg": return CmdSpdHubReg(device, opts);
                case "pswp": return CmdSpdPswp(device, opts);
                default:
                    Console.Error.WriteLine($"Unknown spd subcommand: {opts.Positional[0]}");
                    return EXIT_USAGE;
            }
        }

        private static int CmdSpdRead(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd read <address> [--out file.bin] [--parsed]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var data = device.ReadEntireSPD(addr);
            if (data == null) { WriteLine(opts, "FAIL: read returned null"); return EXIT_I2C_ERROR; }

            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
            {
                var sb = new StringBuilder(data.Length * 3);
                for (int i = 0; i < data.Length; i++) sb.AppendLine($"{data[i]:X2}");
                WriteRaw(opts, sb.ToString());
            }
            else if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["size"] = data.Length,
                    ["bytes"] = BitConverter.ToString(data).Replace("-", "")
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"[SPD] address=0x{addr:X2} size={data.Length}");
                if (opts.Flags.Contains("--parsed"))
                    EmitFullParsedSummary(opts, data);
            }
            return EXIT_OK;
        }

        private static int CmdSpdReadBytes(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd read-bytes <address> <offset> <length>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            byte length = byte.Parse(opts.Positional[3]);

            var data = device.ReadSPD(addr, offset, length);
            if (data == null) { WriteLine(opts, "FAIL: read returned null"); return EXIT_I2C_ERROR; }

            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
                WriteRaw(opts, BitConverter.ToString(data).Replace("-", " ") + "\n");
            else if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["address"] = $"0x{addr:X2}",
                    ["offset"] = offset,
                    ["length"] = data.Length,
                    ["bytes"] = BitConverter.ToString(data).Replace("-", "")
                }) + "\n");
            else
                WriteLine(opts, $"[SPD] 0x{addr:X2} offset={offset} len={data.Length}: {BitConverter.ToString(data).Replace("-", " ")}");

            return EXIT_OK;
        }

        private static int CmdSpdWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd write <address> --in file.bin [--no-verify]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string inPath; opts.Switches.TryGetValue("--in", out inPath);
            if (string.IsNullOrEmpty(inPath)) { Console.Error.WriteLine("--in required"); return EXIT_USAGE; }

            var data = File.ReadAllBytes(inPath);
            bool ok = device.WriteEntireSPD(addr, data);
            if (!ok) { WriteLine(opts, "FAIL"); return EXIT_WRITE_ERROR; }

            if (!opts.Flags.Contains("--no-verify"))
            {
                var verify = device.ReadEntireSPD(addr);
                if (verify == null || !verify.SequenceEqual(data))
                { WriteLine(opts, "FAIL: verify"); return EXIT_VERIFY_FAIL; }
            }
            WriteLine(opts, "OK");
            return EXIT_OK;
        }

        private static int CmdSpdWriteByte(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd write-byte <address> <offset> <value> [--no-verify]");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            byte value = ParseByte(opts.Positional[3]);

            bool ok = device.WriteSPDByte(addr, offset, value);
            if (!ok) { WriteLine(opts, "FAIL"); return EXIT_WRITE_ERROR; }

            if (!opts.Flags.Contains("--no-verify"))
            {
                var rb = device.ReadSPD(addr, offset, 1);
                if (rb == null || rb.Length == 0 || rb[0] != value)
                { WriteLine(opts, "FAIL: verify"); return EXIT_VERIFY_FAIL; }
            }
            WriteLine(opts, "OK");
            return EXIT_OK;
        }

        private static int CmdSpdVerify(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd verify <address> --in file.bin"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string inPath; opts.Switches.TryGetValue("--in", out inPath);
            if (string.IsNullOrEmpty(inPath)) { Console.Error.WriteLine("--in required"); return EXIT_USAGE; }

            var data = File.ReadAllBytes(inPath);
            var actual = device.ReadEntireSPD(addr);
            bool eq = actual != null && actual.SequenceEqual(data);
            WriteLine(opts, eq ? "OK" : "FAIL: mismatch");
            return eq ? EXIT_OK : EXIT_VERIFY_FAIL;
        }

        private static int CmdSpdCrc(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd crc <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var data = device.ReadEntireSPD(addr);
            if (data == null) return EXIT_I2C_ERROR;
            bool isDdr5 = data.Length >= 3 && data[2] == 0x12;
            bool ok = isDdr5 ? VerifyDdr5Crc(data) : VerifyDdr4Crc(data);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object> { ["crc"] = ok ? "ok" : "fail" }) + "\n");
            else
                WriteLine(opts, ok ? "CRC OK" : "CRC FAIL");
            return ok ? EXIT_OK : EXIT_CRC_ERROR;
        }

        private static int CmdSpdFixCrc(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd fix-crc <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);

            var data = device.ReadEntireSPD(addr);
            if (data == null) { WriteLine(opts, "FAIL: could not read SPD"); return EXIT_I2C_ERROR; }

            byte[] patched = SPDParsedFields.RecalcAndFixCrc(data);
            if (patched == null) { WriteLine(opts, "FAIL: CRC patch returned null"); return EXIT_I2C_ERROR; }

            bool anyFixed = false;
            for (ushort i = 0; i < patched.Length; i++)
            {
                if (patched[i] == data[i]) continue;
                bool ok = device.WriteSPDByte(addr, i, patched[i]);
                if (!ok) { WriteLine(opts, $"FAIL: write at offset {i}"); return EXIT_WRITE_ERROR; }
                anyFixed = true;
            }

            WriteLine(opts, anyFixed ? "OK: CRC patched and written to DIMM" : "CRC was already correct - no bytes written");
            return EXIT_OK;
        }

        private static int CmdSpdTestWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: spd test-write <address> <offset>");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort offset = ParseRegister(opts.Positional[2]);
            bool ok = device.TestWrite(addr, offset);
            WriteLine(opts, ok ? "Write test: OK (byte is writable)" : "Write test: FAIL (write-protected or error)");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdSpdParse(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: spd parse <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            WriteLine(opts, $"Reading SPD at 0x{addr:X2}...");
            var data = device.ReadEntireSPD(addr);
            if (data == null) { WriteLine(opts, "FAIL: could not read SPD"); return EXIT_I2C_ERROR; }
            return EmitFullParsedSummary(opts, data);
        }

        private static int CmdSpdParseFile(CliOptions opts)
        {
            string path = opts.Positional.Count >= 2 ? opts.Positional[1] : null;
            string s;
            if (string.IsNullOrEmpty(path) && opts.Switches.TryGetValue("--in", out s)) path = s;
            if (string.IsNullOrEmpty(path))
            {
                Console.Error.WriteLine("usage: spd parse-file <path.bin>  (or --in <path.bin>)");
                return EXIT_USAGE;
            }
            if (!File.Exists(path)) { Console.Error.WriteLine($"File not found: {path}"); return EXIT_NOT_FOUND; }
            return EmitFullParsedSummary(opts, File.ReadAllBytes(path));
        }

        private static int CmdSpdHubReg(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4)
            {
                Console.Error.WriteLine("usage: spd hub-reg <get|set> <address> <register> [value]");
                Console.Error.WriteLine("  register: MR0 MR1 MR6 MR11 MR12 MR13 MR14 MR18 MR20 MR48 MR52");
                Console.Error.WriteLine("  writable: MR11 MR12 MR13");
                return EXIT_USAGE;
            }
            string sub = opts.Positional[1].ToLowerInvariant();
            byte addr = ParseAddress(opts.Positional[2]);
            byte reg = ParseMrName(opts.Positional[3]);

            if (sub == "get")
            {
                byte val = device.ReadSPD5HubRegister(addr, reg);
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["address"] = $"0x{addr:X2}", ["register"] = $"MR{reg} (0x{reg:X2})", ["value"] = $"0x{val:X2}" }) + "\n");
                else
                    WriteLine(opts, $"hub-reg 0x{addr:X2} MR{reg}=0x{val:X2}");
                return EXIT_OK;
            }
            if (sub == "set")
            {
                if (opts.Positional.Count < 5) { Console.Error.WriteLine("value required"); return EXIT_USAGE; }
                byte value = ParseByte(opts.Positional[4]);
                bool ok = device.WriteSPD5HubRegister(addr, reg, value);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("hub-reg subcommand must be get or set");
            return EXIT_USAGE;
        }

        private static int CmdSpdPswp(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: spd pswp <get|set> <address>");
                return EXIT_USAGE;
            }
            string sub = opts.Positional[1].ToLowerInvariant();
            byte addr = ParseAddress(opts.Positional[2]);

            if (sub == "get")
            {
                bool set = device.GetPSWP(addr);
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["address"] = $"0x{addr:X2}", ["pswp"] = set ? "protected" : "open" }) + "\n");
                else
                    WriteLine(opts, $"PSWP 0x{addr:X2}: {(set ? "PROTECTED" : "OPEN")}");
                return EXIT_OK;
            }
            if (sub == "set")
            {
                bool ok = device.SetPSWP(addr);
                WriteLine(opts, ok ? "OK (PSWP set - this is permanent and cannot be undone!)" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("pswp subcommand must be get or set");
            return EXIT_USAGE;
        }

        #endregion

        #region RSWP command

        private static int CmdRswp(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: rswp <get|set|clear> <address>"); return EXIT_USAGE; }
            string sub = opts.Positional[0];
            byte addr = ParseAddress(opts.Positional[1]);

            switch (sub)
            {
                case "get":
                    {
                        byte block = 0;
                        string b; if (opts.Switches.TryGetValue("--block", out b)) block = byte.Parse(b);
                        bool set = device.GetRSWP(addr, block);
                        if (opts.OutputFormat == "json")
                            WriteRaw(opts, ToJson(new Dictionary<string, object>
                            { ["address"] = $"0x{addr:X2}", ["block"] = block, ["state"] = set ? "protected" : "open" }) + "\n");
                        else
                            WriteLine(opts, $"rswp address=0x{addr:X2} block={block} state={(set ? "PROTECTED" : "OPEN")}");
                        return EXIT_OK;
                    }
                case "set":
                    {
                        string b; if (!opts.Switches.TryGetValue("--block", out b)) { Console.Error.WriteLine("--block required"); return EXIT_USAGE; }
                        byte block = byte.Parse(b);
                        bool ok = device.SetRSWP(addr, block);
                        WriteLine(opts, ok ? "OK" : "FAIL");
                        return ok ? EXIT_OK : EXIT_WRITE_ERROR;
                    }
                case "clear":
                    {
                        bool ok = device.ClearRSWP(addr);
                        WriteLine(opts, ok ? "OK" : "FAIL");
                        return ok ? EXIT_OK : EXIT_WRITE_ERROR;
                    }
                default:
                    Console.Error.WriteLine($"Unknown rswp subcommand: {sub}");
                    return EXIT_USAGE;
            }
        }

        #endregion

        #region PMIC command (full coverage)

        private static int CmdPmic(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: pmic <subcommand> [args]"); return EXIT_USAGE; }

            switch (opts.Positional[0].ToLowerInvariant())
            {
                case "read": return CmdPmicRead(device, opts);
                case "write": return CmdPmicWrite(device, opts);
                case "reg-read": return CmdPmicRegRead(device, opts);
                case "reg-write": return CmdPmicRegWrite(device, opts);
                case "unlock": return CmdPmicUnlock(device, opts);
                case "lock": return CmdPmicLock(device, opts);
                case "measure": return CmdPmicMeasure(device, opts);
                case "toggle-vreg": return CmdPmicToggleVreg(device, opts);
                case "enable-vreg": return CmdPmicSetVreg(device, opts, true);
                case "disable-vreg": return CmdPmicSetVreg(device, opts, false);
                case "vreg-state": return CmdPmicVregState(device, opts);
                case "reboot-dimm": return CmdRebootDimm(device, opts);
                case "type": return CmdPmicType(device, opts);
                case "mode": return CmdPmicMode(device, opts);
                case "enable-prog-mode": return CmdPmicEnableProgMode(device, opts);
                case "enable-full-access": return CmdPmicEnableFullAccess(device, opts);
                case "is-unlocked": return CmdPmicIsUnlocked(device, opts);
                case "pgood-status": return CmdPmicPgoodStatus(device, opts);
                case "pgood-pin": return CmdPmicPgoodPin(device, opts);
                case "set-pgood-mode": return CmdPmicSetPgoodMode(device, opts);
                case "change-password": return CmdPmicChangePassword(device, opts);
                default:
                    Console.Error.WriteLine($"Unknown pmic subcommand: {opts.Positional[0]}");
                    return EXIT_USAGE;
            }
        }

        private static int CmdPmicRead(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic read <address> [--out file.bin]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var data = new byte[256];
            for (int reg = 0; reg < 256; reg++)
            {
                var r = device.ReadPMICDevice(addr, (ushort)reg);
                data[reg] = (r == null || r.Length == 0) ? (byte)0xFF : r[0];
            }
            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 256; i++) sb.AppendLine($"{data[i]:X2}");
                WriteRaw(opts, sb.ToString());
            }
            else if (opts.OutputFormat == "json")
            {
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["bytes"] = BitConverter.ToString(data).Replace("-", "") }) + "\n");
            }
            else
            {
                WriteLine(opts, $"PMIC 0x{addr:X2}: 256 bytes read");
            }
            return EXIT_OK;
        }

        private static int CmdPmicWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic write <address> --in file.bin [--full | --block 0-2]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string inPath; opts.Switches.TryGetValue("--in", out inPath);
            if (string.IsNullOrEmpty(inPath)) { Console.Error.WriteLine("--in required"); return EXIT_USAGE; }
            var data = File.ReadAllBytes(inPath);

            if (opts.Flags.Contains("--full"))
            {
                if (data.Length != 256) { Console.Error.WriteLine("--full requires 256-byte input"); return EXIT_USAGE; }
                bool ok = device.WritePMICFullDump(addr, data);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            string blkS;
            if (opts.Switches.TryGetValue("--block", out blkS))
            {
                int blk = int.Parse(blkS);
                bool ok = device.BurnBlock(addr, blk, data);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            Console.Error.WriteLine("--full or --block required");
            return EXIT_USAGE;
        }

        private static int CmdPmicRegRead(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3) { Console.Error.WriteLine("usage: pmic reg-read <address> <register>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort reg = ParseRegister(opts.Positional[2]);
            var r = device.ReadPMICDevice(addr, reg);
            if (r == null || r.Length == 0) return EXIT_I2C_ERROR;
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["register"] = $"0x{reg:X2}", ["value"] = $"0x{r[0]:X2}" }) + "\n");
            else
                WriteLine(opts, $"reg=0x{reg:X2} value=0x{r[0]:X2}");
            return EXIT_OK;
        }

        private static int CmdPmicRegWrite(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 4) { Console.Error.WriteLine("usage: pmic reg-write <address> <register> <value>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            ushort reg = ParseRegister(opts.Positional[2]);
            byte value = ParseByte(opts.Positional[3]);
            bool ok = device.WriteI2CDevice(addr, reg, value);
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicUnlock(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic unlock <address> [--lsb 73 --msb 94]"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            byte lsb = 0x73, msb = 0x94;
            string s;
            if (opts.Switches.TryGetValue("--lsb", out s)) lsb = ParseByte(s);
            if (opts.Switches.TryGetValue("--msb", out s)) msb = ParseByte(s);
            bool ok = device.UnlockVendorRegion(addr, lsb, msb);
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicLock(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic lock <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok = device.LockVendorRegion(addr);
            WriteLine(opts, ok ? "OK" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicMeasure(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic measure <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            var m = device.ReadAllMeasurements(addr);
            if (m == null) return EXIT_I2C_ERROR;

            if (opts.OutputFormat == "json")
            {
                var obj = new Dictionary<string, object>
                {
                    ["device"] = m.DeviceType,
                    ["voltages_mV"] = m.Voltages_mV.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    ["currents_mA"] = m.Currents_mA.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
                    ["powers_mW"] = m.Powers_mW.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
                };
                if (!double.IsNaN(m.TotalPower_mW)) obj["total_mW"] = m.TotalPower_mW;
                WriteRaw(opts, ToJson(obj) + "\n");
            }
            else
            {
                WriteLine(opts, $"Device: {m.DeviceType}");
                foreach (var kv in m.Voltages_mV) WriteLine(opts, $"  {kv.Key}: {kv.Value:F0} mV");
                foreach (var kv in m.Currents_mA) WriteLine(opts, $"  {kv.Key}: {kv.Value:F1} mA");
                foreach (var kv in m.Powers_mW) WriteLine(opts, $"  {kv.Key}: {kv.Value:F1} mW");
                if (!double.IsNaN(m.TotalPower_mW)) WriteLine(opts, $"  Total: {m.TotalPower_mW:F1} mW");
            }
            return EXIT_OK;
        }

        private static int CmdPmicToggleVreg(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic toggle-vreg <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool newState;
            bool ok = device.ToggleVReg(addr, out newState);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["result"] = ok ? "ok" : "fail", ["vreg"] = newState ? "enabled" : "disabled" }) + "\n");
            else
                WriteLine(opts, ok ? $"OK - VReg is now {(newState ? "ENABLED" : "DISABLED")}" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicSetVreg(UDFDevice device, CliOptions opts, bool enable)
        {
            string cmd = enable ? "enable-vreg" : "disable-vreg";
            if (opts.Positional.Count < 2) { Console.Error.WriteLine($"usage: pmic {cmd} <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok = device.SetVRegEnable(addr, enable);
            WriteLine(opts, ok ? $"OK - VReg {(enable ? "enabled" : "disabled")}" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicVregState(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic vreg-state <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool enabled = device.GetVRegEnabled(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["vreg"] = enabled ? "enabled" : "disabled" }) + "\n");
            else
                WriteLine(opts, $"VReg 0x{addr:X2}: {(enabled ? "ENABLED" : "DISABLED")}");
            return EXIT_OK;
        }

        private static int CmdPmicType(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic type <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string type = device.GetPMICType(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["type"] = type }) + "\n");
            else
                WriteLine(opts, $"PMIC 0x{addr:X2}: {type}");
            return EXIT_OK;
        }

        private static int CmdPmicMode(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic mode <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string mode = device.GetPMICMode(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["mode"] = mode }) + "\n");
            else
                WriteLine(opts, $"PMIC 0x{addr:X2} mode: {mode}");
            return EXIT_OK;
        }

        private static int CmdPmicEnableProgMode(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic enable-prog-mode <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok = device.EnableProgMode(addr);
            WriteLine(opts, ok ? "OK - Programmable mode enabled" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicEnableFullAccess(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic enable-full-access <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool ok = device.EnableFullAccess(addr);
            WriteLine(opts, ok ? "OK - Full (manufacturer) access enabled" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicIsUnlocked(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic is-unlocked <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            bool unlocked = device.IsVendorRegionUnlocked(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["unlocked"] = unlocked }) + "\n");
            else
                WriteLine(opts, $"Vendor region 0x{addr:X2}: {(unlocked ? "UNLOCKED" : "LOCKED")}");
            return EXIT_OK;
        }

        private static int CmdPmicPgoodStatus(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("usage: pmic pgood-status <address>"); return EXIT_USAGE; }
            byte addr = ParseAddress(opts.Positional[1]);
            string stat = device.GetPMICPGoodStatus(addr);
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["address"] = $"0x{addr:X2}", ["pgood_status"] = stat }) + "\n");
            else
                WriteLine(opts, $"PGOOD 0x{addr:X2}: {stat}");
            return EXIT_OK;
        }

        private static int CmdPmicPgoodPin(UDFDevice device, CliOptions opts)
        {
            bool high = device.GetPGoodPinState();
            if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["pgood_pin"] = high ? "high" : "low" }) + "\n");
            else
                WriteLine(opts, $"PGOOD pin: {(high ? "HIGH (power good)" : "LOW (fault / disabled)")}");
            return EXIT_OK;
        }

        private static int CmdPmicSetPgoodMode(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 3)
            {
                Console.Error.WriteLine("usage: pmic set-pgood-mode <address> <0|1|2>");
                Console.Error.WriteLine("  0 = hardware default");
                Console.Error.WriteLine("  1 = force LOW (PWR_GOOD deasserted)");
                Console.Error.WriteLine("  2 = force HIGH or open-drain");
                return EXIT_USAGE;
            }
            byte addr = ParseAddress(opts.Positional[1]);
            int mode = int.Parse(opts.Positional[2]);
            if (mode < 0 || mode > 2) { Console.Error.WriteLine("mode must be 0, 1, or 2"); return EXIT_USAGE; }
            bool ok = device.SetPGoodOutputMode(addr, mode);
            WriteLine(opts, ok ? $"OK - PGOOD mode set to {mode}" : "FAIL");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        private static int CmdPmicChangePassword(UDFDevice device, CliOptions opts)
        {
            byte addr, curLsb, curMsb, newLsb, newMsb;
            string s;

            if (opts.Positional.Count >= 6)
            {
                addr = ParseAddress(opts.Positional[1]);
                curLsb = ParseByte(opts.Positional[2]);
                curMsb = ParseByte(opts.Positional[3]);
                newLsb = ParseByte(opts.Positional[4]);
                newMsb = ParseByte(opts.Positional[5]);
            }
            else if (opts.Positional.Count >= 2 && opts.Switches.TryGetValue("--cur-lsb", out s))
            {
                addr = ParseAddress(opts.Positional[1]);
                curLsb = ParseByte(s);
                opts.Switches.TryGetValue("--cur-msb", out s); curMsb = ParseByte(s ?? "94");
                opts.Switches.TryGetValue("--new-lsb", out s); newLsb = ParseByte(s ?? "73");
                opts.Switches.TryGetValue("--new-msb", out s); newMsb = ParseByte(s ?? "94");
            }
            else
            {
                Console.Error.WriteLine("usage: pmic change-password <address> <cur_lsb> <cur_msb> <new_lsb> <new_msb>");
                Console.Error.WriteLine("  or:  pmic change-password <address> --cur-lsb <x> --cur-msb <x> --new-lsb <x> --new-msb <x>");
                return EXIT_USAGE;
            }

            bool ok = device.ChangePMICPassword(addr, curLsb, curMsb, newLsb, newMsb);
            WriteLine(opts, ok ? "OK - password changed and verified" : "FAIL (wrong current password or burn error)");
            return ok ? EXIT_OK : EXIT_WRITE_ERROR;
        }

        #endregion

        #region Pin command

        private static int CmdPin(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: pin <get|set|reset> [args]"); return EXIT_USAGE; }
            string sub = opts.Positional[0];

            if (sub == "reset")
            {
                bool ok = device.ResetPins();
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            if (opts.Positional.Count < 2) { Console.Error.WriteLine("pin name required"); return EXIT_USAGE; }
            byte pin = MapPinName(opts.Positional[1]);

            if (sub == "get")
            {
                byte v = device.GetPin(pin);
                if (opts.OutputFormat == "json")
                    WriteRaw(opts, ToJson(new Dictionary<string, object>
                    { ["pin"] = opts.Positional[1], ["state"] = v }) + "\n");
                else
                    WriteLine(opts, $"pin={opts.Positional[1]} state={v}");
                return EXIT_OK;
            }
            if (sub == "set")
            {
                if (opts.Positional.Count < 3) { Console.Error.WriteLine("set requires 0|1"); return EXIT_USAGE; }
                byte state = byte.Parse(opts.Positional[2]);
                bool ok = device.SetPin(pin, state);
                WriteLine(opts, ok ? "OK" : "FAIL");
                return ok ? EXIT_OK : EXIT_WRITE_ERROR;
            }
            return EXIT_USAGE;
        }

        private static byte MapPinName(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "hv-switch": return UDFDevice.PIN_HV_SWITCH;
                case "sa1": return UDFDevice.PIN_SA1_SWITCH;
                case "status": return UDFDevice.PIN_DEV_STATUS;
                case "hv-conv": return UDFDevice.PIN_HV_CONVERTER;
                case "vin-ctrl": return UDFDevice.PIN_DDR5_VIN_CTRL;
                case "pmic-ctrl": return UDFDevice.PIN_PMIC_CTRL;
                case "pmic-flag": return UDFDevice.PIN_PMIC_FLAG;
                default: throw new ArgumentException($"Unknown pin name: {name}");
            }
        }

        #endregion

        #region EEPROM command

        private static int CmdEeprom(UDFDevice device, CliOptions opts)
        {
            if (opts.Positional.Count < 1) { Console.Error.WriteLine("usage: eeprom read <offset> <length>"); return EXIT_USAGE; }
            if (!string.Equals(opts.Positional[0], "read", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Unknown eeprom subcommand: {opts.Positional[0]}");
                return EXIT_USAGE;
            }
            if (opts.Positional.Count < 3) { Console.Error.WriteLine("usage: eeprom read <offset> <length>"); return EXIT_USAGE; }

            ushort offset = ParseRegister(opts.Positional[1]);
            ushort length = ushort.Parse(opts.Positional[2]);

            var data = device.ReadInternalEEPROM(offset, length);
            if (data == null) { WriteLine(opts, "FAIL: internal EEPROM read returned null"); return EXIT_I2C_ERROR; }

            string outPath; opts.Switches.TryGetValue("--out", out outPath);
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, data);

            if (opts.OutputFormat == "hex")
                WriteRaw(opts, BitConverter.ToString(data).Replace("-", " ") + "\n");
            else if (opts.OutputFormat == "json")
                WriteRaw(opts, ToJson(new Dictionary<string, object>
                { ["offset"] = offset, ["length"] = data.Length, ["bytes"] = BitConverter.ToString(data).Replace("-", "") }) + "\n");
            else
                WriteLine(opts, $"[EEPROM] offset={offset} length={data.Length}: {BitConverter.ToString(data).Replace("-", " ")}");

            return EXIT_OK;
        }

        private static int CmdFirmware(UDFDevice device, string port, CliOptions opts)
        {
            if (opts.Positional.Count < 1)
            {
                Console.Error.WriteLine("usage: firmware update --uf2 <path>");
                return EXIT_USAGE;
            }
            switch (opts.Positional[0].ToLowerInvariant())
            {
                case "update": return CmdFirmwareUpdate(device, port, opts);
                default:
                    Console.Error.WriteLine($"Unknown firmware subcommand: {opts.Positional[0]}");
                    return EXIT_USAGE;
            }
        }

        private static int CmdFirmwareUpdate(UDFDevice device, string port, CliOptions opts)
        {
            if (!opts.Switches.TryGetValue("--uf2", out var uf2Path) || string.IsNullOrEmpty(uf2Path))
            {
                Console.Error.WriteLine("--uf2 <path> required");
                return EXIT_USAGE;
            }
            if (!File.Exists(uf2Path))
            {
                Console.Error.WriteLine($"UF2 file not found: {uf2Path}");
                return EXIT_USAGE;
            }
            if (string.IsNullOrEmpty(port))
            {
                Console.Error.WriteLine("--port or --auto-detect required to send CMD_RebootBootsel");
                return EXIT_USAGE;
            }

            try { device?.Dispose(); } catch { }

            var progress = new Progress<FirmwareUpdater.Status>(s =>
                WriteLine(opts, $"[FW Update] {s}"));

            try
            {
                bool ok = FirmwareUpdater
                    .UpdateAsync(uf2Path, port, progress: progress)
                    .ConfigureAwait(false).GetAwaiter().GetResult();
                if (!ok)
                {
                    WriteLine(opts, "FAIL: firmware update did not complete");
                    return EXIT_WRITE_ERROR;
                }
                WriteLine(opts, "OK: firmware updated");
                return EXIT_OK;
            }
            catch (FileNotFoundException ex)
            {
                WriteLine(opts, $"ERROR: {ex.Message}");
                return EXIT_USAGE;
            }
            catch (TimeoutException ex)
            {
                WriteLine(opts, $"ERROR: {ex.Message}");
                return EXIT_NOT_FOUND;
            }
            catch (Exception ex)
            {
                WriteLine(opts, $"ERROR: {ex.Message}");
                return EXIT_WRITE_ERROR;
            }
        }

        #endregion

        #region SPD parsed output

        private static int EmitFullParsedSummary(CliOptions opts, byte[] spd)
        {
            var result = SPDParsedFields.Parse(spd);

            if (opts.OutputFormat == "json")
            {
                var fields = new Dictionary<string, object>();
                foreach (var f in result.Fields)
                    if (!string.IsNullOrEmpty(f.Value))
                        fields[f.Label] = f.Value;

                WriteRaw(opts, ToJson(new Dictionary<string, object>
                {
                    ["dram_type"] = result.DramType,
                    ["fields"] = fields
                }) + "\n");
            }
            else
            {
                WriteLine(opts, $"DRAM Type: {result.DramType}");
                foreach (var f in result.Fields)
                {
                    if (string.IsNullOrEmpty(f.Value))
                    {
                        WriteLine(opts, "");
                        WriteLine(opts, f.Label);
                    }
                    else
                    {
                        WriteLine(opts, $"  {f.Label,-36} {f.Value}");
                    }
                }
            }
            return EXIT_OK;
        }

        #endregion

        #region Misc helpers

        private static string AutoDetectPort()
        {
            foreach (var port in SerialPort.GetPortNames())
            {
                try
                {
                    using (var dev = new UDFDevice(port, 115200))
                    {
                        if (dev.Ping()) return port;
                    }
                }
                catch { }
            }
            return null;
        }

        private static byte ParseMrName(string s)
        {
            string u = s.ToUpperInvariant();
            if (u.StartsWith("MR"))
                return (byte)int.Parse(u.Substring(2));
            return ParseByte(s);
        }

        private static bool VerifyDdr4Crc(byte[] spd)
        {
            if (spd.Length < 128) return false;
            ushort crc = Crc16(spd, 0, 126);
            ushort stored = (ushort)(spd[126] | (spd[127] << 8));
            return crc == stored;
        }

        private static bool VerifyDdr5Crc(byte[] spd)
        {
            if (spd.Length < 512) return false;
            ushort crc = Crc16(spd, 0, 510);
            ushort stored = (ushort)(spd[510] | (spd[511] << 8));
            return crc == stored;
        }

        private static ushort Crc16(byte[] data, int offset, int length)
        {
            ushort crc = 0;
            for (int i = 0; i < length; i++)
            {
                crc = (ushort)(crc ^ (data[offset + i] << 8));
                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                    else crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        #endregion

        #region Usage

        private static void PrintUsage()
        {
            Console.WriteLine(
@"Unified DDR Flasher  (v4.0.0)  - CLI reference

Usage: Unified-DDR-Flasher.exe <command> [options]

GLOBAL OPTIONS
  --port COM3                COM port  (required unless --auto-detect)
  --auto-detect              Use the first detected UDF device
  --baud 115200              Baud rate  (default 115200)
  --timeout 5000             Per-command timeout ms  (default 5000)
  --output-format text|json|hex
  --quiet                    Suppress informational output
  --smbus                    Use the host's AMD SMBus controller instead of
                             a UDF board (no firmware required). Needs admin
                             privileges and inpoutx64.dll. Subset of commands
                             supported: ping, version, scan, detect, spd,
                             pmic. RSWP, pin control, EEPROM, factory-reset,
                             reboot-dimm, debug-dump, debug-pagewalk are NOT
                             available in this mode.
  --log-file <path>          Append all output to a file
  --reconnect-timeout <sec>  On USB disconnect, wait up to N seconds for the
                             port to reappear before failing  (default: 0 = off)
  --force-gen ddr3|ddr4|ddr5  Hidden override: pin every SPD address to the
                             given generation, bypassing auto-detect. Use only
                             when auto-detect mis-identifies a stick. A wrong
                             override produces garbage reads - by design.
  --use-i3c                  Hidden flag: route hub access through the I3C-aware
                             code path. Currently issues a best-effort recovery
                             pulse before each detect (helps recover hubs in
                             confused states). Full PIO-based I3C is reserved
                             for future firmware.

──────────────────────────────────────────────────────────────────────
DEVICE / DIAGNOSTICS
  ping                         Connectivity check + firmware version
  version                      Print firmware version
  test                         Device self-test (CMD_TEST)
  name [get | set <name>]      Read or write device label
  i2c-speed [0|1|2]            Read/set I2C clock  0=100k 1=400k 2=1M
  scan                         List all I2C devices on the bus
  detect <address>             Identify module type at hex I2C address
  debug-dump [address] [--out file.txt]
                               Full diagnostic dump for bug
                               reports. Captures firmware version, full
                               bus scan, every MR register, raw + DDR5
                               SPD reads via every code path, full PMIC
                               dump, internal EEPROM, pin states, and
                               SPD parser output. Writes a single text
                               file (default udf-debug-YYYYMMDD-HHMMSS.txt).
                               Pass an optional address to deep-dive
                               only that DIMM.
  debug-pagewalk [address] [--out file.txt]
                               Surgical page-walk diagnostic for DDR5
                               sticks that misbehave. Probes the address,
                               reads 256 bytes raw, then for each NVM
                               page 0..7 writes MR11 = page, reads MR11
                               back to verify, and reads 128 bytes from
                               (page * 128). Logs everything. Use when
                               debug-dump shows weird MR/NVM behavior.
                               Default address: 0x50.
  rswp-support                 Report firmware RSWP capabilities
  reboot-dimm [addr]           Power-cycle DIMM; addr (e.g. PMIC 0x48)
                               verifies it really lost power and came back
  factory-reset                Erase internal device EEPROM to defaults

──────────────────────────────────────────────────────────────────────
SPD OPERATIONS   (address is a hex I2C address, e.g. 50 or 0x50)

  spd read   <addr> [--out file.bin] [--parsed]
             Read whole SPD; --parsed prints decoded fields.

  spd read-bytes <addr> <offset> <length> [--out file.bin]
             Read a byte range (up to 64 bytes).

  spd write  <addr> --in file.bin [--no-verify]
             Write entire SPD from binary file.

  spd write-byte <addr> <offset> <value> [--no-verify]
             Write a single byte at offset.

  spd verify <addr> --in file.bin
             Compare live SPD against a reference file.

  spd crc    <addr>
             Verify stored CRC against recalculated value.

  spd fix-crc <addr>
             Recalculate CRC and patch the corrected bytes back to DIMM.

  spd test-write <addr> <offset>
             Non-destructive write-capability probe at offset.

  spd parse  <addr>
             Full SPD field decode for DDR4 and DDR5:
             timings, capacity, CAS latencies,
             manufacturer IDs, CRC verification.

  spd parse-file <path.bin>
             Decode a binary dump without a connected device.
             Also accepts:  spd parse-file --in <path.bin>

  spd hub-reg get <addr> <register>
  spd hub-reg set <addr> <register> <value>
             DDR5 SPD5-hub mode registers.
             Register names: MR0 MR1 MR6 MR11 MR12 MR13 MR14 MR18
                             MR20 MR48 MR52  (or hex: 0x0B / decimal: 11)
             Only MR11, MR12, MR13 are writable.

  spd pswp get <addr>
  spd pswp set <addr>
             Permanent Software Write Protect - set is IRREVERSIBLE.

──────────────────────────────────────────────────────────────────────
RSWP  (Reversible Software Write Protection)
  rswp get   <addr> [--block 0-15]
  rswp set   <addr> --block 0-15
  rswp clear <addr>

──────────────────────────────────────────────────────────────────────
PMIC OPERATIONS

  --- Register access ---
  pmic read        <addr> [--out file.bin]   Dump all 256 registers
  pmic write       <addr> --in file.bin [--full | --block 0-2]
  pmic reg-read    <addr> <register>
  pmic reg-write   <addr> <register> <value>

  --- Identification ---
  pmic type        <addr>        Chip model  (PMIC5000/5100/5200 …)
  pmic mode        <addr>        Locked / Programmable / Manufacturer Access

  --- Access control ---
  pmic enable-prog-mode   <addr>   Enable programmable mode (reg 0x2F[2])
  pmic enable-full-access <addr>   Unlock vendor region (default password)
  pmic is-unlocked        <addr>   Check vendor-region lock state
  pmic unlock  <addr> [--lsb 73 --msb 94]
  pmic lock    <addr>
  pmic change-password <addr> <cur_lsb> <cur_msb> <new_lsb> <new_msb>
               Burns a new MTP password.  Named-switch form also accepted:
               --cur-lsb x --cur-msb x --new-lsb x --new-msb x

  --- Voltage regulator ---
  pmic vreg-state  <addr>        Current VReg enabled/disabled state
  pmic enable-vreg <addr>        Enable VReg
  pmic disable-vreg <addr>       Disable VReg
  pmic toggle-vreg  <addr>       Toggle VReg and report new state

  --- Power ---
  pmic reboot-dimm [addr]        Power-cycle DIMM (same as top-level)
  pmic pgood-pin                 Read PGOOD hardware pin (no addr needed)
  pmic pgood-status  <addr>      Detailed power-good rail status
  pmic set-pgood-mode <addr> <0|1|2>
               0 = hardware default   1 = force LOW   2 = force HIGH/OD

  --- ADC measurements ---
  pmic measure  <addr>           Voltages (mV), currents (mA), powers (mW)

──────────────────────────────────────────────────────────────────────
PIN CONTROL
  pin get   <pin-name>
  pin set   <pin-name> <0|1>
  pin reset
  Pin names: hv-switch | sa1 | status | hv-conv | vin-ctrl | pmic-ctrl | pmic-flag

──────────────────────────────────────────────────────────────────────
INTERNAL DEVICE EEPROM
  eeprom read <offset> <length> [--out file.bin]
             Read from programmer's own storage (max 32 bytes per call).

──────────────────────────────────────────────────────────────────────
FIRMWARE UPDATE
  firmware update --uf2 <path>
             Reboot the device into BOOTSEL mode and program a new UF2
             without the user pressing the BOOTSEL button. Requires the
             COM port to be open (use --port or --auto-detect). Recovery
             from a fully-corrupted firmware is still possible by
             holding BOOTSEL on power-up and dragging the UF2 manually.

──────────────────────────────────────────────────────────────────────
EXIT CODES
  0  Success
  1  Device not found / ping failed
  2  I2C / communication error
  3  Verification mismatch
  4  CRC error
  5  Write error
  6  Usage / argument error
");
        }

        #endregion
    }
}