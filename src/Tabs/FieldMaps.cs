using System;
using System.Drawing;

namespace UnifiedDDRFlasher
{
    internal static class FieldMaps
    {
        internal static readonly Color[] HighlightPalette =
        {
            Color.FromArgb(0, 120, 215),
            Color.FromArgb(0, 158, 115),
            Color.FromArgb(213, 94, 0),
            Color.FromArgb(148, 33, 146),
            Color.FromArgb(0, 153, 168),
            Color.FromArgb(200, 40, 40),
            Color.FromArgb(120, 94, 0),
            Color.FromArgb(86, 60, 168),
        };

        public static void AddSpdFields(byte[] data, Action<int, int, string> add)
        {
            if (data == null || data.Length < 4) return;

            byte type = data[2];
            if (type == 0x12 || type == 0x14)
                AddDdr5Fields(data, add);
            else if (type == 0x13 || type == 0x15)
                AddLpddr5Fields(data, add);
            else if (type == 0x0C || type == 0x0E)
                AddDdr4Fields(data, add);
            else if (type == 0x0B)
                AddDdr3Fields(data, add);
        }

        public static void AddPmicFields(string pmicType, Action<int, int, string> add)
        {
            string t = pmicType ?? "";
            bool server = t.StartsWith("PMIC50");
            bool lpddr = t.StartsWith("PMIC52");

            if (t == "PMIC5120")
                add(0x00, 4, "Serial number bytes 0-3 (R00-R03)");
            add(0x04, 1, "Global error log (R04)");
            add(0x05, 2, "Power-on reset error log (R05-R06)");
            add(0x08, 4, "Live status: power good, temp, OV, OC (R08-R0B)");

            add(0x0C, 1, "SWA current/power readout (R0C)");
            if (server)
                add(0x0D, 1, "SWB current/power readout (R0D)");
            else if (lpddr)
                add(0x0D, 1, "SWD current/power readout (R0D)");
            else if (t == "PMIC5120")
                add(0x0D, 1, "Serial number byte 4 (R0D)");
            add(0x0E, 1, server ? "SWC current/power readout (R0E)" : "SWB current/power readout (R0E)");
            add(0x0F, 1, server ? "SWD current/power readout (R0F)" : "SWC current/power readout (R0F)");

            add(0x10, 5, "Clear status bits, write 1 (R10-R14)");
            add(0x15, 5, "Status masks (R15-R19)");
            add(0x1A, 1, "Power state and PG threshold config (R1A)");
            add(0x1B, 1, "VIN OV threshold, meter select, GSI_n (R1B)");

            add(0x1C, 1, "SWA high-current threshold (R1C)");
            if (server)
                add(0x1D, 1, "SWB high-current threshold (R1D)");
            else if (lpddr)
                add(0x1D, 1, "SWD high-current threshold (R1D)");
            add(0x1E, 1, server ? "SWC high-current threshold (R1E)" : "SWB high-current threshold (R1E)");
            add(0x1F, 1, server ? "SWD high-current threshold (R1F)" : "SWC high-current threshold (R1F)");

            add(0x20, 1, "Current limiter warning thresholds (R20)");

            add(0x21, 2, "SWA voltage and protection (R21-R22)");
            if (server)
                add(0x23, 2, "SWB voltage and protection (R23-R24)");
            else if (lpddr)
                add(0x23, 2, "SWD voltage and protection (R23-R24)");
            add(0x25, 2, server ? "SWC voltage and protection (R25-R26)" : "SWB voltage and protection (R25-R26)");
            add(0x27, 2, server ? "SWD voltage and protection (R27-R28)" : "SWC voltage and protection (R27-R28)");

            if (server)
            {
                add(0x29, 1, "SWA/SWB mode and frequency (R29)");
                add(0x2A, 1, "SWC/SWD mode and frequency (R2A)");
            }
            else if (lpddr)
            {
                add(0x29, 1, "SWA/SWD mode and frequency (R29)");
                add(0x2A, 1, "SWB/SWC mode and frequency (R2A)");
            }
            else
            {
                add(0x29, 1, "SWA mode and frequency (R29)");
                add(0x2A, 1, "SWB/SWC mode and frequency (R2A)");
            }

            add(0x2B, 1, server ? "LDO settings and output range (R2B)" : "1.8V / 1.0V LDO settings (R2B)");
            add(0x2C, 2, "Soft start times (R2C-R2D)");
            add(0x2E, 1, lpddr ? "DVFSQ/pin enables, shutdown temp (R2E)" : "Shutdown temperature threshold (R2E)");
            add(0x2F, 1, server ? "Rail enables, VIN_Mgmt, mode select (R2F)" : "Rail enables, secure/programmable mode (R2F)");
            add(0x30, 1, "ADC enable and channel select (R30)");
            add(0x31, 1, "ADC readout (R31)");
            add(0x32, 1, "VR enable, PWR_GOOD config (R32)");
            add(0x33, 1, server ? "Temperature readout, VIN_Mgmt status (R33)" : "Temperature readout (R33)");
            add(0x34, 1, "PEC/IBI/parity, HID code (R34)");
            add(0x35, 1, "Error injection (R35)");
            add(0x37, 2, "Password entry, LSB then MSB (R37-R38)");
            add(0x39, 1, "Command codes: unlock and burn (R39)");
            add(0x3A, 1, "Default read pointer config (R3A)");
            add(0x3B, 1, "Revision and capability ID (R3B)");
            add(0x3C, 2, "Vendor ID (R3C-R3D)");
            if (lpddr)
            {
                add(0x3E, 1, "SWB DVFSQ voltage setting (R3E)");
                add(0x3F, 1, "SWB voltage offset (R3F)");
            }

            add(0x40, 0x10, "Vendor NVM block 40: power-on defaults (R40-R4F)");
            add(0x50, 0x10, "Vendor NVM block 50: power-on defaults (R50-R5F)");
            add(0x60, 0x10, "Vendor NVM block 60: power-on defaults (R60-R6F)");
            add(0x70, 0x90, "Vendor specific region, password locked (R70-RFF)");
        }

        private static void AddDdr5Fields(byte[] data, Action<int, int, string> add)
        {
            add(0, 1, "SPD bytes total / beta level (byte 0)");
            add(1, 1, "SPD revision, base config (byte 1)");
            add(2, 1, "Key byte: DRAM type (byte 2)");
            add(3, 1, "Key byte: module type, hybrid (byte 3)");
            add(4, 1, "First SDRAM density / package (byte 4)");
            add(5, 1, "First SDRAM rows / columns (byte 5)");
            add(6, 1, "First SDRAM I/O width (byte 6)");
            add(7, 1, "First SDRAM bank groups / banks (byte 7)");
            add(8, 1, "Second SDRAM density / package (byte 8)");
            add(9, 1, "Second SDRAM rows / columns (byte 9)");
            add(10, 1, "Second SDRAM I/O width (byte 10)");
            add(11, 1, "Second SDRAM bank groups / banks (byte 11)");
            add(12, 1, "BL32 and post package repair (byte 12)");
            add(13, 1, "DCA and PASR support (byte 13)");
            add(14, 1, "PRAC, fault handling, temp sense (byte 14)");
            add(16, 1, "VDD nominal voltage (byte 16)");
            add(17, 1, "VDDQ nominal voltage (byte 17)");
            add(18, 1, "VPP nominal voltage (byte 18)");
            add(19, 1, "Standard vs non-standard timing (byte 19)");
            add(20, 2, "tCKAVGmin, min cycle time (bytes 20-21)");
            add(22, 2, "tCKAVGmax, max cycle time (bytes 22-23)");
            add(24, 5, "CAS latencies supported (bytes 24-28)");
            add(30, 2, "tAA, CAS latency time (bytes 30-31)");
            add(32, 2, "tRCD, RAS to CAS delay (bytes 32-33)");
            add(34, 2, "tRP, row precharge (bytes 34-35)");
            add(36, 2, "tRAS, activate to precharge (bytes 36-37)");
            add(38, 2, "tRC, activate to refresh (bytes 38-39)");
            add(40, 2, "tWR, write recovery (bytes 40-41)");
            add(42, 2, "tRFC1, normal refresh recovery (bytes 42-43)");
            add(44, 2, "tRFC2, FGR refresh recovery (bytes 44-45)");
            add(46, 2, "tRFCsb, same-bank refresh (bytes 46-47)");
            add(48, 6, "3DS different-rank refresh, tRFC dlr set (bytes 48-53)");
            add(54, 4, "Refresh management, SDRAM 1 / 2 (bytes 54-57)");
            add(58, 12, "Adaptive refresh levels A / B / C (bytes 58-69)");
            add(70, 3, "tRRD_L, ACT to ACT same bank group (bytes 70-72)");
            add(73, 3, "tCCD_L, RD to RD same bank group (bytes 73-75)");
            add(76, 3, "tCCD_L_WR, WR to WR same bank group (bytes 76-78)");
            add(79, 3, "tCCD_L_WR2, WR to WR non-RMW (bytes 79-81)");
            add(82, 3, "tFAW, four activate window (bytes 82-84)");
            add(85, 3, "tCCD_L_WTR, WR to RD same bank group (bytes 85-87)");
            add(88, 3, "tCCD_S_WTR, WR to RD diff bank group (bytes 88-90)");
            add(91, 3, "tRTP, read to precharge (bytes 91-93)");
            add(94, 3, "tCCD_M, RD to RD diff bank same group (bytes 94-96)");
            add(97, 3, "tCCD_M_WR, WR to WR diff bank same group (bytes 97-99)");
            add(100, 3, "tCCD_M_WTR, WR to RD diff bank same group (bytes 100-102)");

            AddSpd5CommonModuleFields(add);

            switch (data[3] & 0x0F)
            {
                case 0x01:
                case 0x04:
                    add(240, 4, "RCD: mfr ID, type, revision (bytes 240-243)");
                    add(244, 4, "Data buffer: mfr ID, type, revision (bytes 244-247)");
                    add(248, 1, "RCD-RW08 clock enables (byte 248)");
                    add(249, 1, "RCD-RW09 address/control enables (byte 249)");
                    add(250, 1, "RCD-RW0A QCK drive characteristics (byte 250)");
                    add(252, 1, "RCD-RW0C QxCA / QxCS drive (byte 252)");
                    add(253, 1, "RCD-RW0D DB interface drive (byte 253)");
                    add(254, 1, "RCD-RW0E QCK/QCA/QCS slew rate (byte 254)");
                    add(255, 1, "RCD-RW0F BCK/BCOM/BCS slew rate (byte 255)");
                    add(256, 1, "DB-RW86 DQS RTT park (byte 256)");
                    break;
                case 0x02:
                case 0x03:
                case 0x05:
                case 0x06:
                    add(240, 4, "Clock driver: mfr ID, type, revision (bytes 240-243)");
                    add(244, 1, "CKD-RW00 configuration (byte 244)");
                    add(245, 1, "CKD-RW02 QCK drive characteristics (byte 245)");
                    add(246, 1, "CKD-RW03 QCK differential slew (byte 246)");
                    break;
                case 0x07:
                    add(240, 4, "MRCD: mfr ID, type, revision (bytes 240-243)");
                    add(244, 4, "Mux data buffer: mfr ID, type, revision (bytes 244-247)");
                    add(248, 1, "MRCD-RW08 clock enables (byte 248)");
                    add(249, 1, "MRCD-RW09 address/control enables (byte 249)");
                    add(250, 1, "MRCD-RW0A QCK drive characteristics (byte 250)");
                    add(252, 1, "MRCD-RW0C QxCA / QxCS drive (byte 252)");
                    add(253, 1, "MRCD-RW0D DB interface drive (byte 253)");
                    add(254, 1, "MRCD-RW0E QCK/QCA/QCS slew rate (byte 254)");
                    add(255, 1, "MRCD-RW0F BCK/BCOM/BCS slew rate (byte 255)");
                    add(256, 1, "MDB duty cycle adjuster (byte 256)");
                    add(257, 1, "MDB DRAM receiver type (byte 257)");
                    break;
                case 0x08:
                case 0x09:
                    add(240, 4, "Clock driver 0: mfr ID, type, revision (bytes 240-243)");
                    add(244, 4, "Clock driver 1: mfr ID, type, revision (bytes 244-247)");
                    add(256, 3, "CKD0 RW00/02/03 setup (bytes 256-258)");
                    add(259, 3, "CKD1 RW00/02/03 setup (bytes 259-261)");
                    break;
                case 0x0A:
                    add(240, 4, "Differential buffer: mfr ID, type, revision (bytes 240-243)");
                    break;
            }

            AddSpd5ManufacturingFields(add);
        }

        private static void AddSpd5CommonModuleFields(Action<int, int, string> add)
        {
            add(192, 1, "SPD revision, module bytes (byte 192)");
            add(193, 1, "Hashing sequence (byte 193)");
            add(194, 4, "SPD hub: mfr ID, type, revision (bytes 194-197)");
            add(198, 4, "PMIC 0: mfr ID, type, revision (bytes 198-201)");
            add(202, 4, "PMIC 1: mfr ID, type, revision (bytes 202-205)");
            add(206, 4, "PMIC 2: mfr ID, type, revision (bytes 206-209)");
            add(210, 4, "Thermal sensor: mfr ID, type, revision (bytes 210-213)");
            add(229, 1, "Additional support devices (byte 229)");
            add(230, 1, "Module nominal height (byte 230)");
            add(231, 1, "Module maximum thickness (byte 231)");
            add(232, 1, "Reference raw card (byte 232)");
            add(233, 1, "DIMM attributes (byte 233)");
            add(234, 1, "Module organization, ranks (byte 234)");
            add(235, 1, "Memory channel bus width (byte 235)");
        }

        private static void AddSpd5ManufacturingFields(Action<int, int, string> add)
        {
            add(510, 2, "CRC over bytes 0-509 (bytes 510-511)");
            add(512, 2, "Module manufacturer ID (bytes 512-513)");
            add(514, 1, "Manufacturing location (byte 514)");
            add(515, 2, "Manufacturing date (bytes 515-516)");
            add(517, 4, "Module serial number (bytes 517-520)");
            add(521, 30, "Module part number (bytes 521-550)");
            add(551, 1, "Module revision code (byte 551)");
            add(552, 2, "DRAM manufacturer ID (bytes 552-553)");
            add(554, 1, "DRAM stepping (byte 554)");
            add(555, 83, "Manufacturer specific data (bytes 555-637)");
            add(640, 384, "End user programmable (bytes 640-1023)");
        }

        private static void AddLpddr5Fields(byte[] data, Action<int, int, string> add)
        {
            add(0, 1, "SPD bytes total / beta level (byte 0)");
            add(1, 1, "SPD revision, base config (byte 1)");
            add(2, 1, "Key byte: DRAM type (byte 2)");
            add(3, 1, "Key byte: module type (byte 3)");
            add(4, 1, "SDRAM density and banks (byte 4)");
            add(5, 1, "SDRAM addressing (byte 5)");
            add(6, 1, "SDRAM package type (byte 6)");
            add(9, 1, "Optional SDRAM features (byte 9)");
            add(12, 1, "Module organization (byte 12)");
            add(13, 1, "Bus width (byte 13)");
            add(16, 1, "Signal loading (byte 16)");
            add(17, 1, "Timebases (byte 17)");
            add(18, 1, "tCKAVGmin, min cycle time (byte 18)");
            add(19, 1, "tCKAVGmax, max cycle time (byte 19)");
            add(24, 1, "tAAmin, CAS latency time (byte 24)");
            add(26, 1, "tRCDmin, RAS to CAS delay (byte 26)");
            add(27, 1, "tRPab, all-bank precharge (byte 27)");
            add(28, 1, "tRPpb, per-bank precharge (byte 28)");
            add(29, 2, "tRFCab, all-bank refresh recovery (bytes 29-30)");
            add(31, 2, "tRFCpb, per-bank refresh recovery (bytes 31-32)");
            add(120, 6, "Fine timing offsets (bytes 120-125)");

            AddSpd5CommonModuleFields(add);

            add(240, 4, "Clock driver 0: mfr ID, type, revision (bytes 240-243)");
            add(244, 4, "Clock driver 1: mfr ID, type, revision (bytes 244-247)");
            if ((data[3] & 0x0F) == 0x09)
            {
                add(248, 4, "Voltage regulator 2: mfr ID, rail, revision (bytes 248-251)");
                add(252, 4, "Voltage regulator 3: mfr ID, rail, revision (bytes 252-255)");
            }

            AddSpd5ManufacturingFields(add);
        }

        private static void AddDdr4Fields(byte[] data, Action<int, int, string> add)
        {
            add(0, 1, "SPD bytes used / total (byte 0)");
            add(1, 1, "SPD revision (byte 1)");
            add(2, 1, "Key byte: DRAM type (byte 2)");
            add(3, 1, "Key byte: module type (byte 3)");
            add(4, 1, "Density and banks (byte 4)");
            add(5, 1, "Rows / columns addressing (byte 5)");
            add(6, 1, "Primary package type (byte 6)");
            add(7, 1, "Optional features, MAC (byte 7)");
            add(8, 1, "Thermal and refresh options (byte 8)");
            add(9, 1, "Other optional features, PPR (byte 9)");
            add(10, 1, "Secondary package type (byte 10)");
            add(11, 1, "Module nominal voltage (byte 11)");
            add(12, 1, "Module organization, ranks (byte 12)");
            add(13, 1, "Module bus width (byte 13)");
            add(14, 1, "Thermal sensor flag (byte 14)");
            add(15, 1, "Extended module type (byte 15)");
            add(17, 1, "Timebases (byte 17)");
            add(18, 1, "tCKAVGmin, min cycle time (byte 18)");
            add(19, 1, "tCKAVGmax, max cycle time (byte 19)");
            add(20, 4, "CAS latencies supported (bytes 20-23)");
            add(24, 1, "tAA, CAS latency time (byte 24)");
            add(25, 1, "tRCD, RAS to CAS delay (byte 25)");
            add(26, 1, "tRP, row precharge (byte 26)");
            add(27, 1, "Upper nibbles tRAS / tRC (byte 27)");
            add(28, 1, "tRAS LSB (byte 28)");
            add(29, 1, "tRC LSB (byte 29)");
            add(30, 2, "tRFC1, refresh recovery (bytes 30-31)");
            add(32, 2, "tRFC2, 2x refresh recovery (bytes 32-33)");
            add(34, 2, "tRFC4, 4x refresh recovery (bytes 34-35)");
            add(36, 1, "Upper nibble tFAW (byte 36)");
            add(37, 1, "tFAW LSB (byte 37)");
            add(38, 1, "tRRD_S, diff bank group (byte 38)");
            add(39, 1, "tRRD_L, same bank group (byte 39)");
            add(40, 1, "tCCD_L, same bank group (byte 40)");
            add(41, 1, "Upper nibble tWR (byte 41)");
            add(42, 1, "tWR, write recovery (byte 42)");
            add(43, 1, "Upper nibbles tWTR (byte 43)");
            add(44, 1, "tWTR_S, diff bank group (byte 44)");
            add(45, 1, "tWTR_L, same bank group (byte 45)");
            add(60, 18, "Connector to SDRAM bit mapping (bytes 60-77)");
            add(117, 9, "Fine timing offsets (bytes 117-125)");
            add(126, 2, "Base block CRC (bytes 126-127)");

            int moduleType = data[3] & 0x0F;
            bool registered = moduleType == 0x01 || moduleType == 0x05 || moduleType == 0x08;
            bool loadReduced = moduleType == 0x04;
            if (registered || loadReduced)
            {
                add(128, 1, "Raw card extension, height (byte 128)");
                add(129, 1, "Module maximum thickness (byte 129)");
                add(130, 1, "Reference raw card (byte 130)");
                add(131, 1, "DIMM attributes: registers, rows (byte 131)");
                add(132, 1, "Heat spreader solution (byte 132)");
                add(133, 2, loadReduced
                    ? "Register / DB manufacturer ID (bytes 133-134)"
                    : "Register manufacturer ID (bytes 133-134)");
                add(135, 1, "Register revision (byte 135)");
                add(136, 1, "Register to DRAM mapping (byte 136)");
                add(137, 1, "Register drive: control, CA (byte 137)");
                add(138, 1, registered
                    ? "Register drive: clock (byte 138)"
                    : "Register drive: clock, BCOM (byte 138)");
                if (loadReduced)
                {
                    add(139, 1, "Data buffer revision (byte 139)");
                    add(140, 4, "DRAM VrefDQ, ranks 0-3 (bytes 140-143)");
                    add(144, 1, "Data buffer VrefDQ (byte 144)");
                    add(145, 3, "DB MDQ drive / RTT by speed (bytes 145-147)");
                    add(148, 1, "DRAM drive strength (byte 148)");
                    add(149, 3, "DRAM ODT RTT_WR / RTT_NOM by speed (bytes 149-151)");
                    add(152, 3, "DRAM ODT RTT_PARK by speed (bytes 152-154)");
                }
            }
            else
            {
                add(128, 1, "Raw card extension, height (byte 128)");
                add(129, 1, "Module maximum thickness (byte 129)");
                add(130, 1, "Reference raw card (byte 130)");
                add(131, 1, "Edge connector to DRAM mapping (byte 131)");
            }

            if ((data[3] & 0x80) != 0)
                add(192, 62, "Hybrid module parameters (bytes 192-253)");
            add(254, 2, "Module block CRC (bytes 254-255)");
            add(320, 2, "Module manufacturer ID (bytes 320-321)");
            add(322, 1, "Manufacturing location (byte 322)");
            add(323, 2, "Manufacturing date (bytes 323-324)");
            add(325, 4, "Module serial number (bytes 325-328)");
            add(329, 20, "Module part number (bytes 329-348)");
            add(349, 1, "Module revision code (byte 349)");
            add(350, 2, "DRAM manufacturer ID (bytes 350-351)");
            add(352, 1, "DRAM stepping (byte 352)");
            add(353, 29, "Manufacturer specific data (bytes 353-381)");
            add(384, 128, "End user programmable (bytes 384-511)");
        }

        private static void AddDdr3Fields(byte[] data, Action<int, int, string> add)
        {
            add(0, 1, "SPD bytes used / total (byte 0)");
            add(1, 1, "SPD revision (byte 1)");
            add(2, 1, "Key byte: DRAM type (byte 2)");
            add(3, 1, "Key byte: module type (byte 3)");
            add(4, 1, "Density and banks (byte 4)");
            add(5, 1, "Rows / columns addressing (byte 5)");
            add(6, 1, "Module nominal voltage (byte 6)");
            add(7, 1, "Ranks and device width (byte 7)");
            add(8, 1, "Module bus width (byte 8)");
            add(9, 1, "Fine timebase divisor (byte 9)");
            add(10, 2, "Medium timebase (bytes 10-11)");
            add(12, 1, "tCKmin, min cycle time (byte 12)");
            add(14, 2, "CAS latencies supported (bytes 14-15)");
            add(16, 1, "tAA, CAS latency time (byte 16)");
            add(17, 1, "tWR, write recovery (byte 17)");
            add(18, 1, "tRCD, RAS to CAS delay (byte 18)");
            add(19, 1, "tRRD, row to row delay (byte 19)");
            add(20, 1, "tRP, row precharge (byte 20)");
            add(21, 1, "Upper nibbles tRAS / tRC (byte 21)");
            add(22, 1, "tRAS LSB (byte 22)");
            add(23, 1, "tRC LSB (byte 23)");
            add(24, 2, "tRFC, refresh recovery (bytes 24-25)");
            add(26, 1, "tWTR, write to read (byte 26)");
            add(27, 1, "tRTP, read to precharge (byte 27)");
            add(28, 2, "tFAW, four activate window (bytes 28-29)");
            add(30, 1, "SDRAM optional features (byte 30)");
            add(31, 1, "Thermal and refresh options (byte 31)");
            add(32, 1, "Module thermal sensor (byte 32)");
            add(33, 1, "SDRAM device type (byte 33)");
            add(34, 5, "Fine timing offsets (bytes 34-38)");
            add(60, 1, "Module height (byte 60)");
            add(61, 1, "Module thickness (byte 61)");
            add(62, 1, "Reference raw card (byte 62)");

            int moduleType = data[3] & 0x0F;
            if (moduleType == 0x01 || moduleType == 0x05)
            {
                add(65, 2, "Register manufacturer ID (bytes 65-66)");
                add(67, 1, "Register type (byte 67)");
            }

            add(117, 2, "Module manufacturer ID (bytes 117-118)");
            add(119, 1, "Manufacturing location (byte 119)");
            add(120, 2, "Manufacturing date (bytes 120-121)");
            add(122, 4, "Module serial number (bytes 122-125)");
            add(126, 2, "CRC (bytes 126-127)");
            add(128, 18, "Module part number (bytes 128-145)");
            add(146, 2, "Module revision (bytes 146-147)");
            add(148, 2, "DRAM manufacturer ID (bytes 148-149)");
        }

        public static void AddRcdFields(int page, Action<int, int, string> add)
        {
            add(0x00, 1, "Global features (RW00)");
            add(0x01, 1, "Parity, CMD blocking, alert (RW01)");
            add(0x02, 1, "Host interface training mode (RW02)");
            add(0x03, 1, "DRAM and DB interface training (RW03)");
            add(0x04, 1, "Command space (RW04)");
            add(0x05, 1, "DIMM operating speed (RW05)");
            add(0x06, 1, "Fine granularity operating speed (RW06)");
            add(0x07, 1, "Validation pass-through, lockout (RW07)");
            add(0x08, 1, "Clock driver enable (RW08)");
            add(0x09, 1, "Output address and control enable (RW09)");
            add(0x0A, 1, "QCK driver characteristics (RW0A)");
            add(0x0C, 1, "QxCA / QxCS driver characteristics (RW0C)");
            add(0x0D, 1, "DB interface driver characteristics (RW0D)");
            add(0x0E, 1, "QCK/QCA/QCS output slew rate (RW0E)");
            add(0x0F, 1, "BCK/BCOM/BCS output slew rate (RW0F)");
            add(0x10, 1, "Input bus termination (RW10)");
            add(0x11, 1, "Command latency adder (RW11)");
            add(0x12, 1, "QACK output delay (RW12)");
            add(0x13, 1, "QBCK output delay (RW13)");
            add(0x14, 1, "QCCK output delay (RW14)");
            add(0x15, 1, "QDCK output delay (RW15)");
            add(0x17, 1, "QACS0_n output delay (RW17)");
            add(0x18, 1, "QACS1_n output delay (RW18)");
            add(0x19, 1, "QBCS0_n output delay (RW19)");
            add(0x1A, 1, "QBCS1_n output delay (RW1A)");
            add(0x1B, 1, "QACA output delay (RW1B)");
            add(0x1C, 1, "QBCA output delay (RW1C)");
            add(0x1D, 1, "BCS_n and BCOM output delay (RW1D)");
            add(0x1E, 1, "BCK output delay (RW1E)");
            add(0x25, 1, "Sideband bus global control (RW25)");
            add(0x26, 1, "RX loopback control (RW26)");
            add(0x27, 1, "Loopback I/O control (RW27)");
            add(0x28, 1, "Bus error status (RW28)");
            add(0x29, 1, "Clear bus error status (RW29)");
            add(0x2A, 1, "Vendor specific (RW2A)");
            add(0x30, 1, "DFE Vref range limit status (RW30)");
            add(0x31, 1, "DFE configuration (RW31)");
            add(0x32, 1, "DPAR / DCA DFE training mode (RW32)");
            add(0x33, 1, "DFE training extra filtering (RW33)");
            add(0x34, 1, "LFSR DFE training mode (RW34)");
            add(0x35, 1, "LFSR state (RW35)");
            add(0x36, 2, "DFETM loop start values (RW36-RW37)");
            add(0x38, 2, "DFETM loop current values (RW38-RW39)");
            add(0x3A, 1, "DFETM loop step size (RW3A)");
            add(0x3B, 2, "DFETM loop increment counts (RW3B-RW3C)");
            add(0x3D, 2, "DFETM loop current increments (RW3D-RW3E)");
            add(0x3F, 1, "DFE Vref range selection (RW3F)");
            add(0x40, 8, "Internal VrefCA (RW40-RW47)");
            add(0x48, 2, "Internal VrefCS (RW48-RW49)");
            add(0x4A, 1, "DERROR_IN_n Vref (RW4A)");
            add(0x4B, 1, "Loopback Vref (RW4B)");
            add(0x5E, 1, "CW read pointer (RW5E)");
            add(0x5F, 1, "CW page select (RW5F)");

            if (page == 3)
            {
                add(0x60, 3, "Date code (page 3, RW60-RW62)");
                add(0x63, 7, "Vendor unique unit code (page 3, RW63-RW69)");
                add(0x6A, 2, "Vendor ID (page 3, RW6A-RW6B)");
                add(0x6C, 2, "Vendor device ID (page 3, RW6C-RW6D)");
                add(0x6E, 1, "Revision ID (page 3, RW6E)");
            }
            else if (page == 4)
            {
                add(0x60, 0x20, "VHost paged control words (page 4, RW60-RW7F)");
            }
            else
            {
                add(0x60, 0x20, $"DFE paged control words (page {page}, RW60-RW7F)");
            }
        }

        public static void AddCkdFields(Action<int, int, string> add)
        {
            add(0x00, 1, "CKD configuration (RW00)");
            add(0x01, 1, "Output delay control enable (RW01)");
            add(0x02, 1, "QCK driver characteristics (RW02)");
            add(0x03, 1, "QCK output differential slew rate (RW03)");
            add(0x04, 4, "QCK0-QCK3 output delay range (RW04-RW07)");
            add(0x28, 1, "Bus error status (RW28)");
            add(0x29, 1, "Clear bus error status (RW29)");
            add(0x40, 3, "Date code (RW40-RW42)");
            add(0x43, 7, "Vendor unique unit code (RW43-RW49)");
            add(0x4A, 2, "Vendor ID (RW4A-RW4B)");
            add(0x4C, 2, "Device ID (RW4C-RW4D)");
            add(0x4E, 1, "Vendor revision ID (RW4E)");
        }

        public static void AddTsFields(Action<int, int, string> add)
        {
            add(0x00, 2, "Device type, MSB then LSB (MR0-MR1)");
            add(0x02, 1, "Device revision (MR2)");
            add(0x03, 2, "Vendor ID (MR3-MR4)");
            add(0x07, 1, "Device configuration, HID (MR7)");
            add(0x12, 1, "Device configuration (MR18)");
            add(0x13, 1, "Clear temperature status (MR19)");
            add(0x14, 1, "Clear error status (MR20)");
            add(0x1A, 1, "Sensor configuration (MR26)");
            add(0x1B, 1, "Interrupt configuration (MR27)");
            add(0x1C, 2, "Temp high limit, low/high byte (MR28-MR29)");
            add(0x1E, 2, "Temp low limit (MR30-MR31)");
            add(0x20, 2, "Critical temp high limit (MR32-MR33)");
            add(0x22, 2, "Critical temp low limit (MR34-MR35)");
            add(0x30, 1, "Device status (MR48)");
            add(0x31, 2, "Current temperature, low/high byte (MR49-MR50)");
            add(0x33, 1, "Temperature status (MR51)");
            add(0x34, 1, "Error status (MR52)");
            add(0x50, 5, "Serial number (MR80-MR84)");
            add(0x80, 0x80, "Vendor specific registers (MR128-MR255)");
        }

        public static void AddSpd5HubFields(Action<int, int, string> add)
        {
            add(0x00, 2, "Device type, MSB then LSB (MR0-MR1)");
            add(0x02, 1, "Device revision (MR2)");
            add(0x03, 2, "Vendor ID (MR3-MR4)");
            add(0x05, 1, "Device capability (MR5)");
            add(0x06, 1, "Write recovery time capability (MR6)");
            add(0x0B, 1, "Legacy mode config, NVM page select (MR11)");
            add(0x0C, 2, "NVM block write protection (MR12-MR13)");
            add(0x0E, 1, "Host and local interface IO config (MR14)");
            add(0x12, 1, "Device configuration (MR18)");
            add(0x13, 1, "Clear temperature status (MR19)");
            add(0x14, 1, "Clear error status (MR20)");
            add(0x1A, 1, "Sensor configuration (MR26)");
            add(0x1B, 1, "Interrupt configuration (MR27)");
            add(0x1C, 2, "Temp high limit, low/high byte (MR28-MR29)");
            add(0x1E, 2, "Temp low limit (MR30-MR31)");
            add(0x20, 2, "Critical temp high limit (MR32-MR33)");
            add(0x22, 2, "Critical temp low limit (MR34-MR35)");
            add(0x30, 1, "Device status (MR48)");
            add(0x31, 2, "Current temperature, low/high byte (MR49-MR50)");
            add(0x33, 1, "Temperature status (MR51)");
            add(0x34, 1, "Hub, thermal and NVM error status (MR52)");
        }
    }
}