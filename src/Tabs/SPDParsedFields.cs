using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UnifiedDDRFlasher
{
    public static class SPDParsedFields
    {
        public class Field
        {
            public string Label;
            public string Value;
            public bool IsCrcStatus;
            public bool CrcOk;
            public Field(string l, string v) { Label = l; Value = v; }
        }

        public class ParsedResult
        {
            public string DramType;
            public List<Field> Fields = new List<Field>();
            public byte[] PatchedDump;
        }

        public class OcProfile
        {
            public string Kind;
            public string Name;
            public string Speed;
            public string Primaries;
            public string Vdd;
        }

        public class ModuleSummary
        {
            public string Generation;
            public string SpdSize;
            public string SerialNumber;
            public string ModuleMfg;
            public string MfgTime;
            public string PartNumber;
            public string Speed;
            public string Primaries;
            public string Capacity;
            public string DramMfg;
            public string DramDensity;
            public string CrcStatus;
            public bool CrcOk;
            public List<OcProfile> Profiles = new List<OcProfile>();
        }

        public static ParsedResult Parse(byte[] spd)
        {
            var r = new ParsedResult();
            if (spd == null || spd.Length < 32)
            {
                r.DramType = "Unknown (insufficient data)";
                return r;
            }
            spd = PadTo(spd, spd[2] == 0x12 || spd[2] == 0x14 ? 1024
                          : spd[2] == 0x0C || spd[2] == 0x0E ? 512
                          : spd.Length);
            switch (spd[2])
            {
                case 0x0C:
                case 0x0E:
                    r.DramType = spd[2] == 0x0E ? "DDR4E" : "DDR4";
                    ParseDdr4(spd, r);
                    return r;
                case 0x12:
                case 0x14:
                    r.DramType = spd[2] == 0x14 ? "DDR5 NVDIMM-P" : "DDR5";
                    ParseDdr5(spd, r);
                    return r;
                default:
                    r.DramType = $"Unknown (0x{spd[2]:X2})";
                    r.Fields.Add(new Field("DRAM Type", $"0x{spd[2]:X2} (unsupported by parser)"));
                    return r;
            }
        }

        private static void Section(ParsedResult r, string title)
            => r.Fields.Add(new Field("─── " + title + " ───", ""));

        public static ModuleSummary Summarize(byte[] spd)
        {
            var s = new ModuleSummary();
            if (spd == null || spd.Length < 32) { s.Generation = "Unknown"; return s; }

            var parsed = Parse(spd);
            s.Generation = parsed.DramType;
            bool ddr5 = spd[2] == 0x12 || spd[2] == 0x14;
            bool ddr4 = spd[2] == 0x0C || spd[2] == 0x0E;

            s.SpdSize = ddr5 ? FieldValue(parsed, "SPD Device Size")
                          : ddr4 ? FieldValue(parsed, "SPD Bytes Total") : null;
            s.ModuleMfg = FieldValue(parsed, "Module MfgID");
            s.DramMfg = FieldValue(parsed, "DRAM MfgID");
            s.MfgTime = FieldValue(parsed, "Mfg Date");
            s.PartNumber = FieldValue(parsed, "Module PN");
            s.SerialNumber = FieldValue(parsed, "Module SN");
            s.Capacity = FieldValue(parsed, "Module Capacity")
                          ?? FieldValue(parsed, "Module Capacity (asymmetric)");

            string tck = FieldValue(parsed, "tCKAVGmin");
            s.Speed = ExtractParen(tck) ?? tck;

            string dens = FieldValue(parsed, "DRAM Density");
            string width = ddr5 ? FieldValue(parsed, "SDRAM IO Width")
                         : ddr4 ? FieldValue(parsed, "DRAM Width") : null;
            if (!string.IsNullOrEmpty(dens))
                s.DramDensity = string.IsNullOrEmpty(width) ? dens : $"{dens} {width}";

            s.Primaries = AssemblePrimaries(parsed);

            var crcFields = parsed.Fields.FindAll(f => f.IsCrcStatus);
            if (crcFields.Count > 0)
            {
                int ok = 0;
                foreach (var f in crcFields) if (f.CrcOk) ok++;
                s.CrcOk = ok == crcFields.Count;
                s.CrcStatus = crcFields.Count == 1
                    ? (s.CrcOk ? "OK" : "FAIL")
                    : $"{ok}/{crcFields.Count} OK";
            }

            if (ddr5) { ParseXmp3(spd, s); ParseExpo(spd, s); }
            else if (ddr4) { ParseXmp2(spd, s); }

            return s;
        }

        private static string FieldValue(ParsedResult r, string label)
        {
            foreach (var f in r.Fields) if (f.Label == label) return f.Value;
            return null;
        }

        private static string ExtractParen(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open >= 0 && close > open) return s.Substring(open + 1, close - open - 1);
            return s;
        }

        private static int NckOf(ParsedResult r, string label)
        {
            string v = FieldValue(r, label);
            if (string.IsNullOrEmpty(v)) return 0;
            int sp = v.IndexOf(' ');
            string head = sp > 0 ? v.Substring(0, sp) : v;
            int n;
            return int.TryParse(head, out n) ? n : 0;
        }

        private static string AssemblePrimaries(ParsedResult r)
        {
            int cl = NckOf(r, "tAAmin");
            int trcd = NckOf(r, "tRCDmin");
            int trp = NckOf(r, "tRPmin");
            int tras = NckOf(r, "tRASmin");
            if (cl == 0 || trcd == 0 || trp == 0 || tras == 0) return null;
            return $"{cl}-{trcd}-{trp}-{tras}";
        }


        private static int U16LE(byte[] b, int o)
            => (o + 1 < b.Length) ? (b[o] | (b[o + 1] << 8)) : 0;

        private static bool InRange(byte[] b, int o, int len) => o >= 0 && o + len <= b.Length;

        private static byte[] PadTo(byte[] b, int size)
        {
            if (b == null) return new byte[size > 0 ? size : 0];
            if (b.Length >= size) return b;
            var padded = new byte[size];
            Array.Copy(b, padded, b.Length);
            return padded;
        }

        private static string RateFromTckPs(int tckPs, bool ddr5)
            => tckPs <= 0 ? null : (ddr5 ? Ddr5SpeedGrade(tckPs) : Ddr4SpeedGrade(tckPs));

        private static string Ddr5OcVoltage(byte raw)
        {
            int centivolts = (raw >> 5) * 100 + (raw & 0x1F) * 5;
            if (centivolts <= 0) return null;
            return $"{centivolts / 100.0:0.00} V";
        }

        private const int XMP3_HDR = 640;
        private const int XMP3_PROFILE0 = 704;
        private const int XMP3_PROFILE_STRIDE = 64;

        private static void ParseXmp3(byte[] spd, ModuleSummary s)
        {
            if (!InRange(spd, XMP3_HDR, 0x40) || spd[XMP3_HDR] != 0x0C || spd[XMP3_HDR + 1] != 0x4A) return;

            byte profileEnabled = spd[XMP3_HDR + 3];
            for (int p = 0; p < 3; p++)
            {
                if ((profileEnabled & (1 << p)) == 0) continue;
                int baseOff = XMP3_PROFILE0 + p * XMP3_PROFILE_STRIDE;
                if (!InRange(spd, baseOff, XMP3_PROFILE_STRIDE)) break;

                int tckPs = U16LE(spd, baseOff + 5);
                if (tckPs < 100 || tckPs > 1000) continue;

                var prof = new OcProfile { Kind = "XMP 3.0", Name = XmpProfileName(spd, p) };
                prof.Speed = RateFromTckPs(tckPs, true);
                prof.Vdd = Ddr5OcVoltage(spd[baseOff + 1]);
                prof.Primaries = AssembleOcPrimaries(
                    U16LE(spd, baseOff + 0x0D), U16LE(spd, baseOff + 0x0F),
                    U16LE(spd, baseOff + 0x11), U16LE(spd, baseOff + 0x13), tckPs);

                if (prof.Speed != null || prof.Primaries != null || prof.Vdd != null)
                    s.Profiles.Add(prof);
            }
        }

        private static string XmpProfileName(byte[] spd, int p)
        {
            int off = XMP3_HDR + 0x0E + p * 16;
            if (!InRange(spd, off, 16)) return $"Profile {p + 1}";
            var sb = new StringBuilder(16);
            for (int i = 0; i < 16; i++)
            {
                byte b = spd[off + i];
                if (b == 0) break;
                if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            }
            string name = sb.ToString().Trim();
            return name.Length > 0 ? name : $"Profile {p + 1}";
        }

        private static void ParseXmp2(byte[] spd, ModuleSummary s)
        {
            const int HDR = 384;
            if (!InRange(spd, HDR, 2) || spd[HDR] != 0x0C || spd[HDR + 1] != 0x4A) return;

            for (int p = 0; p < 2; p++)
            {
                int baseOff = 393 + p * 47;
                if (!InRange(spd, baseOff, 12)) break;

                int tckPs = spd[baseOff] * 125;
                if (tckPs < 600 || tckPs > 2500) continue;

                var prof = new OcProfile { Kind = "XMP 2.0", Name = $"Profile {p + 1}" };
                prof.Speed = RateFromTckPs(tckPs, false);

                int vddRaw = spd[HDR + 2 + p];
                if (vddRaw > 0) prof.Vdd = $"{vddRaw * 0.05:0.00} V";

                int tAA = spd[baseOff + 2] * 125;
                int tRCD = spd[baseOff + 4] * 125;
                int tRP = spd[baseOff + 6] * 125;
                int tRAS = (((spd[baseOff + 8] & 0x0F) << 8) | spd[baseOff + 9]) * 125;
                int cl = PsToNckDdr4(tAA, tckPs), rcd = PsToNckDdr4(tRCD, tckPs);
                int rp = PsToNckDdr4(tRP, tckPs), ras = PsToNckDdr4(tRAS, tckPs);
                if (cl > 0 && rcd > 0 && rp > 0 && ras > 0)
                    prof.Primaries = $"{cl}-{rcd}-{rp}-{ras}";

                if (prof.Speed != null || prof.Primaries != null || prof.Vdd != null)
                    s.Profiles.Add(prof);
            }
        }

        private static readonly int[] ExpoEnableBit = { 0, 4 };
        private const int EXPO_PROFILE_STRIDE = 40;

        private static void ParseExpo(byte[] spd, ModuleSummary s)
        {
            int hdr = FindSignature(spd, new byte[] { (byte)'E', (byte)'X', (byte)'P', (byte)'O' }, 512);
            if (hdr < 0 || !InRange(spd, hdr, 10)) return;

            byte profileEnabled = spd[hdr + 5];
            for (int p = 0; p < 2; p++)
            {
                if ((profileEnabled & (1 << ExpoEnableBit[p])) == 0) continue;
                int baseOff = hdr + 10 + p * EXPO_PROFILE_STRIDE;
                if (!InRange(spd, baseOff, EXPO_PROFILE_STRIDE)) break;

                int tckPs = U16LE(spd, baseOff + 4);
                if (tckPs < 100 || tckPs > 1000) continue;

                var prof = new OcProfile { Kind = "EXPO", Name = $"Profile {p + 1}" };
                prof.Speed = RateFromTckPs(tckPs, true);
                prof.Vdd = Ddr5OcVoltage(spd[baseOff]);
                prof.Primaries = AssembleOcPrimaries(
                    U16LE(spd, baseOff + 6), U16LE(spd, baseOff + 8),
                    U16LE(spd, baseOff + 10), U16LE(spd, baseOff + 12), tckPs);

                if (prof.Speed != null || prof.Primaries != null || prof.Vdd != null)
                    s.Profiles.Add(prof);
            }
        }

        private static string AssembleOcPrimaries(int tAAps, int tRCDps, int tRPps, int tRASps, int tckPs)
        {
            int cl = PsToNckDdr5(tAAps, tckPs);
            int rcd = PsToNckDdr5(tRCDps, tckPs);
            int rp = PsToNckDdr5(tRPps, tckPs);
            int ras = PsToNckDdr5(tRASps, tckPs);
            if (cl <= 0 || rcd <= 0 || rp <= 0 || ras <= 0) return null;
            return $"{cl}-{rcd}-{rp}-{ras}";
        }

        private static int FindSignature(byte[] b, byte[] sig, int startAt)
        {
            if (b == null || sig == null || sig.Length == 0) return -1;
            int last = b.Length - sig.Length;
            for (int i = Math.Max(0, startAt); i <= last; i++)
            {
                bool hit = true;
                for (int j = 0; j < sig.Length; j++)
                    if (b[i + j] != sig[j]) { hit = false; break; }
                if (hit) return i;
            }
            return -1;
        }

        #region DDR4

        private static int Ddr4PsFromMtbFtb(byte mtbValue, sbyte ftbValue)
            => mtbValue * 125 + ftbValue;

        private static int PsToNckDdr4(int timingPs, int tCkPs)
            => (int)Math.Ceiling((double)timingPs / tCkPs - 0.01);

        private static void ParseDdr4(byte[] spd, ParsedResult r)
        {
            Section(r, "General");
            int spdBytesUsed = spd[0] & 0x0F;
            int spdBytesTotal = (spd[0] >> 4) & 0x0F;
            r.Fields.Add(new Field("SPD Bytes Used",
                spdBytesUsed == 1 ? "128"
                : spdBytesUsed == 2 ? "256"
                : spdBytesUsed == 3 ? "384"
                : spdBytesUsed == 4 ? "512"
                : $"reserved (0x{spdBytesUsed:X})"));
            r.Fields.Add(new Field("SPD Bytes Total",
                spdBytesTotal == 1 ? "256"
                : spdBytesTotal == 2 ? "512"
                : $"reserved (0x{spdBytesTotal:X})"));
            r.Fields.Add(new Field("DRAM Type", spd[2] == 0x0E ? "DDR4E SDRAM" : "DDR4 SDRAM"));
            r.Fields.Add(new Field("SPD Revision", $"{spd[1] >> 4}.{spd[1] & 0xF}"));
            r.Fields.Add(new Field("Module Type", Ddr4ModuleType(spd[3] & 0x0F)));
            r.Fields.Add(new Field("Hybrid", Ddr4HybridType((spd[3] >> 4) & 0x0F)));

            Section(r, "SDRAM");
            int densIdx = spd[4] & 0x0F;
            int[] densMb = { 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 12288, 24576 };
            int sdramCapMb = (densIdx >= 0 && densIdx < densMb.Length) ? densMb[densIdx] : 0;
            r.Fields.Add(new Field("DRAM Density", FormatMb(sdramCapMb)));

            int bgBits = (spd[4] >> 6) & 0x03;
            r.Fields.Add(new Field("Bank Groups", bgBits == 0 ? "0 (no BG)" : (bgBits == 1 ? "2" : (bgBits == 2 ? "4" : "reserved"))));
            int banksPerBg = (spd[4] >> 4) & 0x03;
            r.Fields.Add(new Field("Banks per BG", banksPerBg == 0 ? "4" : (banksPerBg == 1 ? "8" : "reserved")));
            r.Fields.Add(new Field("Row Bits", $"{((spd[5] >> 3) & 0x07) + 12}"));
            r.Fields.Add(new Field("Col Bits", $"{(spd[5] & 0x07) + 9}"));

            int dieCount = ((spd[6] >> 4) & 0x07) + 1;
            r.Fields.Add(new Field("Die Count (pkg)", dieCount.ToString()));
            r.Fields.Add(new Field("Package Type", (spd[6] & 0x80) == 0 ? "Monolithic SDP" : "Non-monolithic"));
            int sigLoad = spd[6] & 0x03;
            r.Fields.Add(new Field("3DS Signal Load",
                sigLoad == 0 ? "not specified"
                : sigLoad == 1 ? "Multi-load"
                : sigLoad == 2 ? "3DS single-load"
                : "reserved"));

            int macIdx = spd[7] & 0x0F;
            string mac =
                  macIdx == 0 ? "Untested"
                : macIdx == 1 ? "700 K"
                : macIdx == 2 ? "600 K"
                : macIdx == 3 ? "500 K"
                : macIdx == 4 ? "400 K"
                : macIdx == 5 ? "300 K"
                : macIdx == 6 ? "200 K"
                : macIdx == 8 ? "Unlimited (>=1.8M)"
                : $"reserved (0x{macIdx:X})";
            r.Fields.Add(new Field("Max Activate Count", mac));
            int mawIdx = (spd[7] >> 4) & 0x03;
            r.Fields.Add(new Field("Max Activate Window",
                mawIdx == 0 ? "8192*tREFI" : mawIdx == 1 ? "4096*tREFI" : (mawIdx == 2 ? "2048*tREFI" : "reserved")));

            Section(r, "Module Electrical");
            var v = new List<string>();
            if ((spd[11] & 0x01) != 0) v.Add("1.2V operable");
            if ((spd[11] & 0x02) != 0) v.Add("1.2V endurant");
            if ((spd[11] & 0x04) != 0) v.Add("TBD1");
            if ((spd[11] & 0x08) != 0) v.Add("TBD2");
            r.Fields.Add(new Field("Voltage", v.Count == 0 ? "not specified" : string.Join(", ", v)));

            int packageRanks = ((spd[12] >> 3) & 0x07) + 1;
            r.Fields.Add(new Field("Package Ranks", packageRanks.ToString()));
            r.Fields.Add(new Field("Rank Mix", (spd[12] & 0x40) == 0 ? "Symmetrical" : "Asymmetrical"));

            int ioWidthIdx = spd[12] & 0x07;
            int[] ioBits = { 4, 8, 16, 32 };
            int ioWidth = (ioWidthIdx >= 0 && ioWidthIdx < ioBits.Length) ? ioBits[ioWidthIdx] : 0;
            r.Fields.Add(new Field("DRAM Width", $"x{ioWidth}"));

            int busWidthIdx = spd[13] & 0x07;
            int[] busBits = { 8, 16, 32, 64 };
            int busWidth = (busWidthIdx >= 0 && busWidthIdx < busBits.Length) ? busBits[busWidthIdx] : 0;
            r.Fields.Add(new Field("Bus Width", $"{busWidth} bits"));
            int eccBits = (spd[13] >> 3) & 0x03;
            r.Fields.Add(new Field("ECC Extension", eccBits == 0 ? "none" : (eccBits == 1 ? "8-bit ECC" : "reserved")));

            r.Fields.Add(new Field("Thermal Sensor",
                (spd[14] & 0x80) != 0 ? "Installed (TS+EE on hub)" : "not installed / extended"));
            r.Fields.Add(new Field("Extended Module Type", $"0x{spd[15]:X2}"));
            r.Fields.Add(new Field("Maximum Bytes Per Module Self-Refresh",
                (spd[16] & 0x80) != 0 ? "PASR supported" : "PASR not supported"));

            int logicalRanks;
            bool is3dsSingleLoad = (spd[6] & 0x03) == 0x02;
            if (is3dsSingleLoad)
                logicalRanks = packageRanks * dieCount;
            else
                logicalRanks = packageRanks;

            long totalMb = 0;
            if (sdramCapMb > 0 && busWidth > 0 && ioWidth > 0)
                totalMb = (long)(sdramCapMb / 8) * (busWidth / ioWidth) * logicalRanks;
            r.Fields.Add(new Field("Module Capacity", FormatMb(totalMb)));
            r.Fields.Add(new Field("Logical Ranks", logicalRanks.ToString()));

            Section(r, "Timing");
            int tCkAvgMinPs = Ddr4PsFromMtbFtb(spd[18], (sbyte)spd[125]);
            int tCkAvgMaxPs = Ddr4PsFromMtbFtb(spd[19], (sbyte)spd[124]);

            r.Fields.Add(new Field("tCKAVGmin", $"{tCkAvgMinPs} ps ({Ddr4SpeedGrade(tCkAvgMinPs)})"));
            r.Fields.Add(new Field("tCKAVGmax", $"{tCkAvgMaxPs} ps"));

            int tAaPs = Ddr4PsFromMtbFtb(spd[24], (sbyte)spd[123]);
            int tRcdPs = Ddr4PsFromMtbFtb(spd[25], (sbyte)spd[122]);
            int tRpPs = Ddr4PsFromMtbFtb(spd[26], (sbyte)spd[121]);
            int tRasPs = (((spd[27] & 0x0F) << 8) | spd[28]) * 125;
            int tRcPs = (((spd[27] >> 4) << 8) | spd[29]) * 125 + (sbyte)spd[120];
            int tWrPs = (((spd[41] & 0x0F) << 8) | spd[42]) * 125;
            int tRfc1Ps = ((spd[31] << 8) | spd[30]) * 125;
            int tRfc2Ps = ((spd[33] << 8) | spd[32]) * 125;
            int tRfc4Ps = ((spd[35] << 8) | spd[34]) * 125;
            int tFawPs = (((spd[36] & 0x0F) << 8) | spd[37]) * 125;
            int tRrdSPs = Ddr4PsFromMtbFtb(spd[38], (sbyte)spd[119]);
            int tRrdLPs = Ddr4PsFromMtbFtb(spd[39], (sbyte)spd[118]);
            int tCcdLPs = Ddr4PsFromMtbFtb(spd[40], (sbyte)spd[117]);
            int tWtrSPs = (((spd[43] & 0x0F) << 8) | spd[44]) * 125;
            int tWtrLPs = (((spd[43] >> 4) << 8) | spd[45]) * 125;

            int tCk = Math.Max(tCkAvgMinPs, 1);
            r.Fields.Add(new Field("tAAmin", FormatNck(tAaPs, tCk)));
            r.Fields.Add(new Field("tRCDmin", FormatNck(tRcdPs, tCk)));
            r.Fields.Add(new Field("tRPmin", FormatNck(tRpPs, tCk)));
            r.Fields.Add(new Field("tRASmin", FormatNck(tRasPs, tCk)));
            r.Fields.Add(new Field("tRCmin", FormatNck(tRcPs, tCk)));
            r.Fields.Add(new Field("tWRmin", FormatNck(tWrPs, tCk)));
            r.Fields.Add(new Field("tRFC1min", FormatNck(tRfc1Ps, tCk)));
            r.Fields.Add(new Field("tRFC2min", FormatNck(tRfc2Ps, tCk)));
            r.Fields.Add(new Field("tRFC4min", FormatNck(tRfc4Ps, tCk)));
            r.Fields.Add(new Field("tFAWmin", FormatNck(tFawPs, tCk)));
            r.Fields.Add(new Field("tRRD_Smin", FormatNck(tRrdSPs, tCk)));
            r.Fields.Add(new Field("tRRD_Lmin", FormatNck(tRrdLPs, tCk)));
            r.Fields.Add(new Field("tCCD_Lmin", FormatNck(tCcdLPs, tCk)));
            r.Fields.Add(new Field("tWTR_Smin", FormatNck(tWtrSPs, tCk)));
            r.Fields.Add(new Field("tWTR_Lmin", FormatNck(tWtrLPs, tCk)));

            r.Fields.Add(new Field("CAS Latencies (CL)", string.Join(", ", DecodeDdr4CasLatencies(spd))));

            r.Fields.Add(new Field("Connector to SDRAM Bit Mapping", $"B60-B77 (raw, see hex viewer)"));

            Section(r, "CRC");
            ushort baseStored = (ushort)(spd[126] | (spd[127] << 8));
            ushort baseCalc = Crc16(spd, 126, 0);
            var baseField = new Field("Base CRC (B0-125)",
                $"stored=0x{baseStored:X4} calc=0x{baseCalc:X4} {(baseStored == baseCalc ? "OK" : "FAIL")}")
            { IsCrcStatus = true, CrcOk = baseStored == baseCalc };
            r.Fields.Add(baseField);

            if (spd.Length >= 256)
            {
                ushort blk1Stored = (ushort)(spd[254] | (spd[255] << 8));
                ushort blk1Calc = Crc16(spd, 126, 128);
                var blk1Field = new Field("Block 1 CRC (B128-253)",
                    $"stored=0x{blk1Stored:X4} calc=0x{blk1Calc:X4} {(blk1Stored == blk1Calc ? "OK" : "FAIL")}")
                { IsCrcStatus = true, CrcOk = blk1Stored == blk1Calc };
                r.Fields.Add(blk1Field);
            }

            if (spd.Length >= 132)
            {
                Section(r, "Module Geometry");
                int height = spd[128] & 0x1F;
                r.Fields.Add(new Field("Module Height", height == 0 ? "<= 15 mm" : $"(15 + {height}) mm"));
                int rawCardThickFront = spd[129] & 0x0F;
                int rawCardThickBack = (spd[129] >> 4) & 0x0F;
                r.Fields.Add(new Field("Raw Card Front Thickness", rawCardThickFront == 0 ? "<= 1 mm" : $"(1 + {rawCardThickFront}) mm"));
                r.Fields.Add(new Field("Raw Card Back Thickness", rawCardThickBack == 0 ? "<= 1 mm" : $"(1 + {rawCardThickBack}) mm"));
                int rcRev = (spd[130] >> 5) & 0x07;
                int rcRef = spd[130] & 0x1F;
                r.Fields.Add(new Field("Reference Raw Card",
                    rcRef == 0x1F ? $"see B131 (extension)" : $"{(char)('A' + rcRef)} rev {rcRev}"));
            }

            if (spd.Length >= 384)
            {
                Section(r, "Manufacturing");
                r.Fields.Add(new Field("Module MfgID", LookupManufacturer(spd[320], spd[321])));
                r.Fields.Add(new Field("Mfg Location", $"0x{spd[322]:X2}"));
                r.Fields.Add(new Field("Mfg Date", FormatBcdDate(spd[323], spd[324])));
                r.Fields.Add(new Field("Module SN",
                    $"0x{spd[325]:X2}{spd[326]:X2}{spd[327]:X2}{spd[328]:X2}"));
                r.Fields.Add(new Field("Module PN", AsciiTrim(spd, 329, 20)));
                r.Fields.Add(new Field("Module Revision", $"0x{spd[349]:X2}"));
                r.Fields.Add(new Field("DRAM MfgID", LookupManufacturer(spd[350], spd[351])));
                r.Fields.Add(new Field("DRAM Stepping", spd[352] == 0xFF ? "N/A" : $"0x{spd[352]:X2}"));
            }
        }

        public static string Ddr4SpeedGrade(int tCkAvgMinPs)
        {
            if (tCkAvgMinPs >= 1250) return "DDR4-1600";
            if (tCkAvgMinPs >= 1071) return "DDR4-1866";
            if (tCkAvgMinPs >= 937) return "DDR4-2133";
            if (tCkAvgMinPs >= 833) return "DDR4-2400";
            if (tCkAvgMinPs >= 750) return "DDR4-2666";
            if (tCkAvgMinPs >= 714) return "DDR4-2800";
            if (tCkAvgMinPs >= 682) return "DDR4-2933";
            if (tCkAvgMinPs >= 652) return "DDR4-3066";
            if (tCkAvgMinPs >= 625) return "DDR4-3200";
            return $"DDR4 (~{2_000_000 / Math.Max(tCkAvgMinPs, 1)} MT/s)";
        }

        private static List<int> DecodeDdr4CasLatencies(byte[] spd)
        {
            bool highRange = (spd[23] & 0x80) != 0;
            int baseCL = highRange ? 23 : 7;
            var supportedCLs = new List<int>();
            for (int byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                byte b = spd[20 + byteIndex];
                for (int bit = 0; bit < 8; bit++)
                {
                    if (byteIndex == 3 && (bit == 7 || bit == 6)) continue;
                    if (((b >> bit) & 1) == 1)
                        supportedCLs.Add(baseCL + byteIndex * 8 + bit);
                }
            }
            return supportedCLs;
        }

        private static string Ddr4ModuleType(int code)
        {
            switch (code)
            {
                case 0x00: return "Extended (see B15)";
                case 0x01: return "RDIMM";
                case 0x02: return "UDIMM";
                case 0x03: return "SO-DIMM";
                case 0x04: return "LRDIMM";
                case 0x05: return "Mini-RDIMM";
                case 0x06: return "Mini-UDIMM";
                case 0x08: return "72b-SO-RDIMM";
                case 0x09: return "72b-SO-UDIMM";
                case 0x0C: return "16b-SO-DIMM";
                case 0x0D: return "32b-SO-DIMM";
                default: return $"reserved (0x{code:X})";
            }
        }

        private static string Ddr4HybridType(int code) =>
            code == 0 ? "none" : (code == 9 ? "NVDIMM" : $"reserved (0x{code:X})");

        #endregion

        #region DDR5

        private static int Ddr5Ps16(byte[] spd, int lsbIndex)
            => spd[lsbIndex] | (spd[lsbIndex + 1] << 8);

        private static int PsToNckDdr5(int timingPs, int tCkPs)
        {
            long temp = ((long)timingPs * 997L) / Math.Max(tCkPs, 1) + 1000L;
            return (int)(temp / 1000L);
        }

        public static string Ddr5SpeedGrade(int tCkAvgMinPs)
        {
            if (tCkAvgMinPs <= 0) return "DDR5";
            int dataRate = 2_000_000 / tCkAvgMinPs;
            int[] std = { 3200, 3600, 4000, 4400, 4800, 5200, 5600, 6000,
                          6400, 6800, 7200, 7600, 8000, 8400, 8800, 9200 };
            int best = std[0]; int bestDiff = Math.Abs(std[0] - dataRate);
            foreach (var s in std)
            {
                int d = Math.Abs(s - dataRate);
                if (d < bestDiff) { best = s; bestDiff = d; }
            }
            return $"DDR5-{best}";
        }

        private static List<int> DecodeDdr5CasLatencies(byte[] spd)
        {
            var cls = new List<int>();
            for (int b = 0; b < 5; b++)
                for (int bit = 0; bit < 8; bit++)
                    if (((spd[24 + b] >> bit) & 1) == 1)
                        cls.Add(20 + b * 16 + bit * 2);
            return cls;
        }

        private static int Ddr5DensityToGbits(int densIdx)
        {
            switch (densIdx)
            {
                case 0x01: return 4;
                case 0x02: return 8;
                case 0x03: return 12;
                case 0x04: return 16;
                case 0x05: return 24;
                case 0x06: return 32;
                case 0x07: return 48;
                case 0x08: return 64;
                default: return 0;
            }
        }

        private static int Ddr5LogicalRanksPerPackage(int dieIdx)
        {
            switch (dieIdx)
            {
                case 0: return 1;
                case 1: return 1;
                case 2: return 2;
                case 3: return 4;
                case 4: return 8;
                case 5: return 16;
                default: return 1;
            }
        }

        private static void ParseDdr5(byte[] spd, ParsedResult r)
        {
            Section(r, "General");
            int spdBetaLevel = ((spd[0] >> 7) & 0x01) << 4 | (spd[0] & 0x0F);
            int spdBytesTotal = (spd[0] >> 4) & 0x07;
            r.Fields.Add(new Field("SPD Device Size",
                spdBytesTotal == 1 ? "256 B"
                : spdBytesTotal == 2 ? "512 B"
                : spdBytesTotal == 3 ? "1024 B (e.g. SPD5118)"
                : spdBytesTotal == 4 ? "2048 B (e.g. ESPD5216)"
                : $"undefined/reserved (0x{spdBytesTotal:X})"));
            r.Fields.Add(new Field("SPD Beta Level", spdBetaLevel.ToString()));
            r.Fields.Add(new Field("DRAM Type", spd[2] == 0x14 ? "DDR5 NVDIMM-P" : "DDR5 SDRAM"));
            r.Fields.Add(new Field("SPD Revision (base)", $"{spd[1] >> 4}.{spd[1] & 0xF}"));
            r.Fields.Add(new Field("Module Type", Ddr5ModuleType(spd[3] & 0x0F)));
            r.Fields.Add(new Field("Hybrid Media", Ddr5HybridType((spd[3] >> 4) & 0x0F)));

            Section(r, "First SDRAM");
            int densIdx = spd[4] & 0x1F;
            int firstGbits = Ddr5DensityToGbits(densIdx);
            r.Fields.Add(new Field("DRAM Density",
                firstGbits > 0 ? $"{firstGbits} Gb" : $"reserved (0x{densIdx:X})"));

            int dieIdx = (spd[4] >> 5) & 0x07;
            string diePerPkg;
            switch (dieIdx)
            {
                case 0: diePerPkg = "1 (Mono SDP)"; break;
                case 1: diePerPkg = "2 (DDP)"; break;
                case 2: diePerPkg = "2H 3DS"; break;
                case 3: diePerPkg = "4H 3DS"; break;
                case 4: diePerPkg = "8H 3DS"; break;
                case 5: diePerPkg = "16H 3DS"; break;
                default: diePerPkg = $"reserved (0x{dieIdx:X})"; break;
            }
            r.Fields.Add(new Field("Die Per Package", diePerPkg));

            int rowIdx = spd[5] & 0x1F;
            r.Fields.Add(new Field("Row Bits",
                rowIdx == 0 ? "16"
                : rowIdx == 1 ? "17"
                : rowIdx == 2 ? "18"
                : $"reserved (0x{rowIdx:X})"));
            int colIdx = (spd[5] >> 5) & 0x07;
            r.Fields.Add(new Field("Col Bits",
                colIdx == 0 ? "10"
                : colIdx == 1 ? "11"
                : $"reserved (0x{colIdx:X})"));

            int ioIdx = (spd[6] >> 5) & 0x07;
            int[] ioMap = { 4, 8, 16, 32 };
            int firstIoWidth = ioIdx < ioMap.Length ? ioMap[ioIdx] : 0;
            r.Fields.Add(new Field("SDRAM IO Width",
                firstIoWidth > 0 ? $"x{firstIoWidth}" : "reserved"));

            int bgIdx = (spd[7] >> 5) & 0x07;
            int[] bgMap = { 1, 2, 4, 8 };
            r.Fields.Add(new Field("Bank Groups",
                bgIdx < bgMap.Length ? bgMap[bgIdx].ToString() : "reserved"));
            int bpgIdx = spd[7] & 0x07;
            int[] bpgMap = { 1, 2, 4 };
            r.Fields.Add(new Field("Banks per BG",
                bpgIdx < bpgMap.Length ? bpgMap[bpgIdx].ToString() : "reserved"));

            bool asymmetric = spd.Length > 11 && (spd[8] != 0 || spd[9] != 0 || spd[10] != 0 || spd[11] != 0);
            int secondGbits = 0;
            int secondIoWidth = 0;
            int secondLogicalRanksPerPkg = 1;
            if (asymmetric)
            {
                Section(r, "Second SDRAM (asymmetric)");
                int densIdx2 = spd[8] & 0x1F;
                secondGbits = Ddr5DensityToGbits(densIdx2);
                r.Fields.Add(new Field("DRAM Density (2nd)",
                    secondGbits > 0 ? $"{secondGbits} Gb" : $"reserved (0x{densIdx2:X})"));

                int dieIdx2 = (spd[8] >> 5) & 0x07;
                secondLogicalRanksPerPkg = Ddr5LogicalRanksPerPackage(dieIdx2);
                r.Fields.Add(new Field("Die Per Package (2nd)", $"code 0x{dieIdx2:X} -> {secondLogicalRanksPerPkg} logical rank(s) per pkg"));

                int ioIdx2 = (spd[10] >> 5) & 0x07;
                secondIoWidth = ioIdx2 < ioMap.Length ? ioMap[ioIdx2] : 0;
                r.Fields.Add(new Field("SDRAM IO Width (2nd)",
                    secondIoWidth > 0 ? $"x{secondIoWidth}" : "reserved"));
            }

            Section(r, "Voltage");
            r.Fields.Add(new Field("VDD Nominal", Ddr5Voltage(spd[16])));
            r.Fields.Add(new Field("VDDQ Nominal", Ddr5Voltage(spd[17])));
            r.Fields.Add(new Field("VPP Nominal", Ddr5Voltage(spd[18])));

            Section(r, "Timing");
            r.Fields.Add(new Field("Timing Mode",
                (spd[19] & 0x01) == 0 ? "Standard" : "Non-standard / OC"));
            int tCkMinPs = Ddr5Ps16(spd, 20);
            int tCkMaxPs = Ddr5Ps16(spd, 22);
            r.Fields.Add(new Field("tCKAVGmin", $"{tCkMinPs} ps ({Ddr5SpeedGrade(tCkMinPs)})"));
            r.Fields.Add(new Field("tCKAVGmax", $"{tCkMaxPs} ps"));

            int tCk = Math.Max(tCkMinPs, 1);
            void Add(string name, int ps) => r.Fields.Add(new Field(name, FormatNckDdr5(ps, tCk)));
            void AddWithFloor(string name, int ps, byte floor)
            {
                int calc = PsToNckDdr5(ps, tCk);
                int fin = Math.Max(calc, floor);
                r.Fields.Add(new Field(name, $"{fin} nCK ({ps} ps; floor={floor})"));
            }

            Add("tAAmin", Ddr5Ps16(spd, 30));
            Add("tRCDmin", Ddr5Ps16(spd, 32));
            Add("tRPmin", Ddr5Ps16(spd, 34));
            Add("tRASmin", Ddr5Ps16(spd, 36));
            Add("tRCmin", Ddr5Ps16(spd, 38));
            Add("tWRmin", Ddr5Ps16(spd, 40));
            Add("tRFC1min", Ddr5Ps16(spd, 42) * 1000);
            Add("tRFC2min", Ddr5Ps16(spd, 44) * 1000);
            Add("tRFCsbmin", Ddr5Ps16(spd, 46) * 1000);

            Add("tRFC1_slr_min", Ddr5Ps16(spd, 48) * 1000);
            Add("tRFC2_slr_min", Ddr5Ps16(spd, 50) * 1000);
            Add("tRFCsb_slr_min", Ddr5Ps16(spd, 52) * 1000);

            AddWithFloor("tRRD_Lmin", Ddr5Ps16(spd, 70), spd[72]);
            AddWithFloor("tCCD_Lmin", Ddr5Ps16(spd, 73), spd[75]);
            AddWithFloor("tCCD_L_WRmin", Ddr5Ps16(spd, 76), spd[78]);
            AddWithFloor("tCCD_L_WR2min", Ddr5Ps16(spd, 79), spd[81]);
            AddWithFloor("tFAWmin", Ddr5Ps16(spd, 82), spd[84]);
            AddWithFloor("tCCD_L_WTRmin", Ddr5Ps16(spd, 85), spd[87]);
            AddWithFloor("tCCD_S_WTRmin", Ddr5Ps16(spd, 88), spd[90]);
            AddWithFloor("tRTPmin", Ddr5Ps16(spd, 91), spd[93]);

            r.Fields.Add(new Field("CAS Latencies (CL)", string.Join(", ", DecodeDdr5CasLatencies(spd))));

            Section(r, "CRC");
            if (spd.Length >= 512)
            {
                ushort stored = (ushort)(spd[510] | (spd[511] << 8));
                ushort calc = Crc16(spd, 510, 0);
                r.Fields.Add(new Field("Base CRC (B0-509)",
                    $"stored=0x{stored:X4} calc=0x{calc:X4} {(stored == calc ? "OK" : "FAIL")}")
                { IsCrcStatus = true, CrcOk = stored == calc });
            }
            else
            {
                r.Fields.Add(new Field("Base CRC", "n/a (dump < 512 bytes)"));
            }

            int pkgRanksPerSubchannel = 0;
            int subchannelsPerDimm = 0;
            int primaryBusWidth = 0;
            int eccBitsExt = 0;
            bool rankMixAsymmetric = false;
            if (spd.Length >= 240)
            {
                Section(r, "Common Module Block");
                r.Fields.Add(new Field("SPD Revision (B192-447)", $"{spd[192] >> 4}.{spd[192] & 0xF}"));
                r.Fields.Add(new Field("Hashing Sequence", $"0x{spd[193]:X2}"));
                r.Fields.Add(new Field("SPD Hub MfgID", LookupManufacturer(spd[194], spd[195])));
                r.Fields.Add(new Field("SPD Device Type", Ddr5SpdDeviceType(spd[196])));
                r.Fields.Add(new Field("SPD Device Revision", $"0x{spd[197]:X2}"));

                if (spd[198] != 0 || spd[199] != 0 || spd[200] != 0)
                {
                    r.Fields.Add(new Field("PMIC0 MfgID", LookupManufacturer(spd[198], spd[199])));
                    r.Fields.Add(new Field("PMIC0 Type", Ddr5PmicType(spd[200])));
                    r.Fields.Add(new Field("PMIC0 Revision", $"0x{spd[201]:X2}"));
                }
                if (spd[202] != 0 || spd[203] != 0 || spd[204] != 0)
                {
                    r.Fields.Add(new Field("PMIC1 MfgID", LookupManufacturer(spd[202], spd[203])));
                    r.Fields.Add(new Field("PMIC1 Type", Ddr5PmicType(spd[204])));
                    r.Fields.Add(new Field("PMIC1 Revision", $"0x{spd[205]:X2}"));
                }
                if (spd[206] != 0 || spd[207] != 0 || spd[208] != 0)
                {
                    r.Fields.Add(new Field("PMIC2 MfgID", LookupManufacturer(spd[206], spd[207])));
                    r.Fields.Add(new Field("PMIC2 Type", Ddr5PmicType(spd[208])));
                    r.Fields.Add(new Field("PMIC2 Revision", $"0x{spd[209]:X2}"));
                }
                if (spd[210] != 0 || spd[211] != 0 || spd[212] != 0)
                {
                    r.Fields.Add(new Field("Thermal Sensor MfgID", LookupManufacturer(spd[210], spd[211])));
                    r.Fields.Add(new Field("Thermal Sensor Type", Ddr5ThermalSensorType(spd[212])));
                    r.Fields.Add(new Field("Thermal Sensor Revision", $"0x{spd[213]:X2}"));
                }

                int height = spd[230] & 0x1F;
                r.Fields.Add(new Field("Module Nominal Height",
                    height == 0 ? "<= 15 mm" : $"(15 + {height}) mm"));
                int frontMax = spd[231] & 0x0F;
                int backMax = (spd[231] >> 4) & 0x0F;
                r.Fields.Add(new Field("Module Max Front Thickness",
                    frontMax == 0 ? "<= 1 mm" : $"(1 + {frontMax}) mm"));
                r.Fields.Add(new Field("Module Max Back Thickness",
                    backMax == 0 ? "<= 1 mm" : $"(1 + {backMax}) mm"));

                int rcRev = (spd[232] >> 5) & 0x07;
                int rcRef = spd[232] & 0x1F;
                r.Fields.Add(new Field("Reference Raw Card",
                    rcRef == 0x1F ? "extension (see B233)" : $"{(char)('A' + rcRef)} rev {rcRev}"));

                int rows = ((spd[233] >> 2) & 0x01) << 2 | (spd[233] & 0x03);
                string rowsStr = rows == 0 ? "Undefined" : rows == 1 ? "1 row" : rows == 2 ? "2 rows" : rows == 3 ? "4 rows" : rows == 4 ? "3 rows" : $"reserved ({rows})";
                r.Fields.Add(new Field("Rows of DRAMs", rowsStr));
                r.Fields.Add(new Field("Heat Spreader", (spd[233] & 0x08) != 0 ? "Installed" : "Not installed"));
                int tempRange = (spd[233] >> 4) & 0x0F;
                r.Fields.Add(new Field("Operating Temp Range", Ddr5TempRange(tempRange)));

                pkgRanksPerSubchannel = ((spd[234] >> 3) & 0x07) + 1;
                rankMixAsymmetric = (spd[234] & 0x40) != 0;
                r.Fields.Add(new Field("Package Ranks/Subchannel", pkgRanksPerSubchannel.ToString()));
                r.Fields.Add(new Field("Rank Mix", rankMixAsymmetric ? "Asymmetrical" : "Symmetrical"));

                int subchanCode = (spd[235] >> 5) & 0x07;
                int[] subchanMap = { 1, 2, 4, 8 };
                subchannelsPerDimm = subchanCode < subchanMap.Length ? subchanMap[subchanCode] : 0;
                int eccCode = (spd[235] >> 3) & 0x03;
                int[] eccMap = { 0, 4, 8 };
                eccBitsExt = eccCode < eccMap.Length ? eccMap[eccCode] : 0;
                int primaryCode = spd[235] & 0x07;
                int[] primaryMap = { 8, 16, 32, 64 };
                primaryBusWidth = primaryCode < primaryMap.Length ? primaryMap[primaryCode] : 0;
                r.Fields.Add(new Field("Sub-channels per DIMM", subchannelsPerDimm > 0 ? subchannelsPerDimm.ToString() : "reserved"));
                r.Fields.Add(new Field("Bus Width Extension", $"{eccBitsExt} bits ({(eccBitsExt == 0 ? "no ECC" : "ECC")})"));
                r.Fields.Add(new Field("Primary Bus Width", primaryBusWidth > 0 ? $"{primaryBusWidth} bits/sub-channel" : "reserved"));
                int totalDataWidth = primaryBusWidth * subchannelsPerDimm;
                int totalChanWidth = (primaryBusWidth + eccBitsExt) * subchannelsPerDimm;
                r.Fields.Add(new Field("Total Data Width", $"{totalDataWidth} bits ({totalChanWidth} bits incl. ECC)"));
            }

            Section(r, "Computed Capacity");
            if (firstGbits > 0 && firstIoWidth > 0 && primaryBusWidth > 0
                && pkgRanksPerSubchannel > 0 && subchannelsPerDimm > 0)
            {
                int firstLogicalRanks = Ddr5LogicalRanksPerPackage(dieIdx);
                long allFirstBitsPerSub = (long)firstGbits * 1024L * 1024L * 1024L
                                        * firstLogicalRanks
                                        * (primaryBusWidth / firstIoWidth)
                                        * pkgRanksPerSubchannel;

                long totalBits;
                if (rankMixAsymmetric && secondGbits > 0 && secondIoWidth > 0)
                {
                    int halfRanks = pkgRanksPerSubchannel / 2;
                    long firstBitsPerSub = (long)firstGbits * 1024L * 1024L * 1024L
                                         * firstLogicalRanks
                                         * (primaryBusWidth / firstIoWidth)
                                         * halfRanks;
                    long secondBitsPerSub = (long)secondGbits * 1024L * 1024L * 1024L
                                          * secondLogicalRanksPerPkg
                                          * (primaryBusWidth / secondIoWidth)
                                          * halfRanks;
                    totalBits = (firstBitsPerSub + secondBitsPerSub) * subchannelsPerDimm;
                    r.Fields.Add(new Field("Module Capacity (asymmetric)",
                        $"{FormatBytes(totalBits / 8)}  "
                        + $"(1st: {FormatBytes(firstBitsPerSub * subchannelsPerDimm / 8)}, "
                        + $"2nd: {FormatBytes(secondBitsPerSub * subchannelsPerDimm / 8)})"));
                }
                else
                {
                    totalBits = allFirstBitsPerSub * subchannelsPerDimm;
                    r.Fields.Add(new Field("Module Capacity", FormatBytes(totalBits / 8)));
                }
            }
            else
            {
                r.Fields.Add(new Field("Module Capacity", "n/a (incomplete data in SPD)"));
            }

            if (spd.Length >= 555)
            {
                Section(r, "Manufacturing");
                r.Fields.Add(new Field("Module MfgID", LookupManufacturer(spd[512], spd[513])));
                r.Fields.Add(new Field("Mfg Location", $"0x{spd[514]:X2}"));
                r.Fields.Add(new Field("Mfg Date", FormatBcdDate(spd[515], spd[516])));
                r.Fields.Add(new Field("Module SN",
                    $"0x{spd[517]:X2}{spd[518]:X2}{spd[519]:X2}{spd[520]:X2}"));
                r.Fields.Add(new Field("Module PN", AsciiTrim(spd, 521, 30)));
                r.Fields.Add(new Field("Module Revision", $"0x{spd[551]:X2}"));
                r.Fields.Add(new Field("DRAM MfgID", LookupManufacturer(spd[552], spd[553])));
                r.Fields.Add(new Field("DRAM Stepping", spd[554] == 0xFF ? "N/A" : $"0x{spd[554]:X2}"));
            }
        }

        private static string Ddr5VoltageLevel(int code)
        {
            return code == 0 ? "1.1 V" : $"reserved ({code})";
        }

        private static string Ddr5Voltage(byte raw)
        {
            int nominal = (raw >> 4) & 0x0F;
            int operable = (raw >> 2) & 0x03;
            int endurant = raw & 0x03;
            return $"{Ddr5VoltageLevel(nominal)} nominal "
                 + $"(operable {Ddr5VoltageLevel(operable)}, endurant {Ddr5VoltageLevel(endurant)}; raw 0x{raw:X2})";
        }

        private static string Ddr5TempRange(int code)
        {
            switch (code)
            {
                case 0: return "A1T (-40 to +125 °C)";
                case 1: return "A2T (-40 to +105 °C)";
                case 2: return "A3T (-40 to +85 °C)";
                case 3: return "IT (-40 to +95 °C)";
                case 4: return "ST (-25 to +85 °C)";
                case 5: return "ET (-25 to +105 °C)";
                case 6: return "RT (0 to +45 °C)";
                case 7: return "NT (0 to +85 °C)";
                case 8: return "XT (0 to +95 °C)";
                default: return $"reserved ({code})";
            }
        }

        private static string Ddr5SpdDeviceType(byte raw)
        {
            int t = raw & 0x0F;
            string name = t == 0x0 ? "SPD5118" : t == 0x1 ? "ESPD5216" : $"reserved (0x{t:X})";
            return $"{name} ({((raw & 0x80) != 0 ? "installed" : "not installed")})";
        }

        private static string Ddr5PmicType(byte raw)
        {
            int t = raw & 0x0F;
            string name;
            switch (t)
            {
                case 0x0: name = "PMIC5000"; break;
                case 0x1: name = "PMIC5010"; break;
                case 0x2: name = "PMIC5100"; break;
                case 0x3: name = "PMIC5020"; break;
                case 0x4: name = "PMIC5120"; break;
                case 0x5: name = "PMIC5200"; break;
                case 0x6: name = "PMIC5030"; break;
                default: name = $"reserved (0x{t:X})"; break;
            }
            return $"{name} ({((raw & 0x80) != 0 ? "installed" : "not installed")})";
        }

        private static string Ddr5ThermalSensorType(byte raw)
        {
            int t = raw & 0x0F;
            string name;
            switch (t)
            {
                case 0x0: name = "TS5111"; break;
                case 0x1: name = "TS5110"; break;
                case 0x2: name = "TS5211"; break;
                case 0x3: name = "TS5210"; break;
                default: name = $"reserved (0x{t:X})"; break;
            }
            return $"{name} (TS0 {((raw & 0x80) != 0 ? "on" : "off")}, TS1 {((raw & 0x40) != 0 ? "on" : "off")})";
        }

        private static string Ddr5ModuleType(int code)
        {
            switch (code)
            {
                case 0x01: return "RDIMM";
                case 0x02: return "UDIMM";
                case 0x03: return "SODIMM";
                case 0x04: return "LRDIMM";
                case 0x05: return "CUDIMM";
                case 0x06: return "CSODIMM";
                case 0x07: return "MRDIMM";
                case 0x08: return "CAMM2";
                case 0x09: return "SOCAMM2";
                case 0x0A: return "DDIMM";
                case 0x0B: return "Solder-Down";
                default: return $"reserved (0x{code:X})";
            }
        }

        private static string Ddr5HybridType(int code)
        {
            switch (code)
            {
                case 0x0: return "none";
                case 0x9: return "NVDIMM-N";
                case 0xA: return "NVDIMM-P";
                default: return $"reserved (0x{code:X})";
            }
        }

        #endregion

        #region Common helpers

        public static ushort Crc16(byte[] data, int count, int startIndex)
        {
            ushort crc = 0;
            for (int i = startIndex; i < startIndex + count; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                    else crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        public static byte[] RecalcAndFixCrc(byte[] spd)
        {
            if (spd == null) return null;
            var copy = (byte[])spd.Clone();
            if (copy.Length < 128) return copy;

            byte t = copy[2];
            if (t == 0x0C || t == 0x0E)
            {
                ushort baseCrc = Crc16(copy, 126, 0);
                copy[126] = (byte)(baseCrc & 0xFF);
                copy[127] = (byte)(baseCrc >> 8);
                if (copy.Length >= 256)
                {
                    ushort blk1 = Crc16(copy, 126, 128);
                    copy[254] = (byte)(blk1 & 0xFF);
                    copy[255] = (byte)(blk1 >> 8);
                }
            }
            else if (t == 0x12 || t == 0x14)
            {
                if (copy.Length >= 512)
                {
                    ushort crc = Crc16(copy, 510, 0);
                    copy[510] = (byte)(crc & 0xFF);
                    copy[511] = (byte)(crc >> 8);
                }
            }
            return copy;
        }

        private static readonly Dictionary<int, string> Manufacturers = new Dictionary<int, string>
        {
            { (1 << 8) | 0x01, "AMD" },
            { (1 << 8) | 0x02, "AMI" },
            { (1 << 8) | 0x04, "RAMXEED Limited" },
            { (1 << 8) | 0x07, "Hitachi" },
            { (1 << 8) | 0x08, "Inmos" },
            { (1 << 8) | 0x0B, "Intersil" },
            { (1 << 8) | 0x0D, "Mostek" },
            { (1 << 8) | 0x0E, "Freescale (Motorola)" },
            { (1 << 8) | 0x10, "NEC" },
            { (1 << 8) | 0x13, "Synaptics" },
            { (1 << 8) | 0x15, "NXP (Philips)" },
            { (1 << 8) | 0x16, "Synertek" },
            { (1 << 8) | 0x19, "Xicor" },
            { (1 << 8) | 0x1A, "Zilog" },
            { (1 << 8) | 0x1C, "Mitsubishi" },
            { (1 << 8) | 0x1F, "Atmel" },
            { (1 << 8) | 0x20, "STMicroelectronics" },
            { (1 << 8) | 0x23, "Wafer Scale Integration" },
            { (1 << 8) | 0x25, "Tristar" },
            { (1 << 8) | 0x26, "Visic" },
            { (1 << 8) | 0x29, "Microchip Technology" },
            { (1 << 8) | 0x2A, "Ricoh Ltd" },
            { (1 << 8) | 0x2C, "Micron Technology" },
            { (1 << 8) | 0x2F, "ACTEL" },
            { (1 << 8) | 0x31, "Catalyst" },
            { (1 << 8) | 0x32, "Panasonic" },
            { (1 << 8) | 0x34, "Cypress" },
            { (1 << 8) | 0x37, "Zarlink (Plessey)" },
            { (1 << 8) | 0x38, "UTMC" },
            { (1 << 8) | 0x3B, "Integrated CMOS (Vertex)" },
            { (1 << 8) | 0x3D, "Tektronix" },
            { (1 << 8) | 0x3E, "Oracle Corporation" },
            { (1 << 8) | 0x40, "ProMos/Mosel Vitelic" },
            { (1 << 8) | 0x43, "Xerox" },
            { (1 << 8) | 0x45, "SanDisk Technologies Inc" },
            { (1 << 8) | 0x46, "Elan Circuit Tech." },
            { (1 << 8) | 0x49, "Xilinx" },
            { (1 << 8) | 0x4A, "Compaq" },
            { (1 << 8) | 0x4C, "SCI" },
            { (1 << 8) | 0x4F, "I3 Design System" },
            { (1 << 8) | 0x51, "Crosspoint Solutions" },
            { (1 << 8) | 0x52, "Alliance Memory Inc" },
            { (1 << 8) | 0x54, "Hewlett-Packard" },
            { (1 << 8) | 0x57, "New Media" },
            { (1 << 8) | 0x58, "MHS Electronic" },
            { (1 << 8) | 0x5B, "Kawasaki Steel" },
            { (1 << 8) | 0x5D, "TECMAR" },
            { (1 << 8) | 0x5E, "Exar" },
            { (1 << 8) | 0x61, "Northern Telecom" },
            { (1 << 8) | 0x62, "Sanyo" },
            { (1 << 8) | 0x64, "Crystal Semiconductor" },
            { (1 << 8) | 0x67, "Asparix" },
            { (1 << 8) | 0x68, "Convex Computer" },
            { (1 << 8) | 0x6B, "Transwitch" },
            { (1 << 8) | 0x6D, "Cannon" },
            { (1 << 8) | 0x6E, "Altera" },
            { (1 << 8) | 0x70, "Qualcomm" },
            { (1 << 8) | 0x73, "AMS(Austria Micro)" },
            { (1 << 8) | 0x75, "Aster Electronics" },
            { (1 << 8) | 0x76, "Bay Networks (Synoptic)" },
            { (1 << 8) | 0x79, "Thesys" },
            { (1 << 8) | 0x7A, "Solbourne Computer" },
            { (1 << 8) | 0x7C, "Dialog Semiconductor" },
            { (1 << 8) | 0x83, "Fairchild" },
            { (1 << 8) | 0x85, "GTE" },
            { (1 << 8) | 0x86, "Harris" },
            { (1 << 8) | 0x89, "Intel" },
            { (1 << 8) | 0x8A, "I.T.T." },
            { (1 << 8) | 0x8C, "Monolithic Memories" },
            { (1 << 8) | 0x8F, "National" },
            { (1 << 8) | 0x91, "RCA" },
            { (1 << 8) | 0x92, "Raytheon" },
            { (1 << 8) | 0x94, "Seeq" },
            { (1 << 8) | 0x97, "Texas Instruments" },
            { (1 << 8) | 0x98, "Kioxia Corporation" },
            { (1 << 8) | 0x9B, "Eurotechnique" },
            { (1 << 8) | 0x9D, "Lucent (AT&T)" },
            { (1 << 8) | 0x9E, "Exel" },
            { (1 << 8) | 0xA1, "Lattice Semi." },
            { (1 << 8) | 0xA2, "NCR" },
            { (1 << 8) | 0xA4, "IBM" },
            { (1 << 8) | 0xA7, "Intl. CMOS Technology" },
            { (1 << 8) | 0xA8, "SSSI" },
            { (1 << 8) | 0xAB, "VLSI" },
            { (1 << 8) | 0xAD, "SK Hynix" },
            { (1 << 8) | 0xAE, "OKI Semiconductor" },
            { (1 << 8) | 0xB0, "Sharp" },
            { (1 << 8) | 0xB3, "IDT" },
            { (1 << 8) | 0xB5, "DEC" },
            { (1 << 8) | 0xB6, "LSI Logic" },
            { (1 << 8) | 0xB9, "Thinking Machine" },
            { (1 << 8) | 0xBA, "Thomson CSF" },
            { (1 << 8) | 0xBC, "Honeywell" },
            { (1 << 8) | 0xBF, "Silicon Storage Technology" },
            { (1 << 8) | 0xC1, "Infineon (Siemens)" },
            { (1 << 8) | 0xC2, "Macronix" },
            { (1 << 8) | 0xC4, "Plus Logic" },
            { (1 << 8) | 0xC7, "European Silicon Str." },
            { (1 << 8) | 0xC8, "Apple Computer" },
            { (1 << 8) | 0xCB, "Protocol Engines" },
            { (1 << 8) | 0xCD, "ABLIC" },
            { (1 << 8) | 0xCE, "Samsung" },
            { (1 << 8) | 0xD0, "Klic" },
            { (1 << 8) | 0xD3, "Tandem" },
            { (1 << 8) | 0xD5, "Integrated Silicon Solutions" },
            { (1 << 8) | 0xD6, "Brooktree" },
            { (1 << 8) | 0xD9, "Performance Semi." },
            { (1 << 8) | 0xDA, "Winbond Electronic" },
            { (1 << 8) | 0xDC, "Bright Micro" },
            { (1 << 8) | 0xDF, "PCMCIA" },
            { (1 << 8) | 0xE0, "LG Semi (Goldstar)" },
            { (1 << 8) | 0xE3, "Array Microsystems" },
            { (1 << 8) | 0xE5, "Analog Devices" },
            { (1 << 8) | 0xE6, "PMC-Sierra" },
            { (1 << 8) | 0xE9, "Quality Semiconductor" },
            { (1 << 8) | 0xEA, "Nimbus Technology" },
            { (1 << 8) | 0xEC, "Micronas (ITT Intermetall)" },
            { (1 << 8) | 0xEF, "NEXCOM" },
            { (1 << 8) | 0xF1, "Sony" },
            { (1 << 8) | 0xF2, "Cray Research" },
            { (1 << 8) | 0xF4, "Vitesse" },
            { (1 << 8) | 0xF7, "Zentrum/ZMD" },
            { (1 << 8) | 0xF8, "TRW" },
            { (1 << 8) | 0xFB, "Allied-Signal" },
            { (1 << 8) | 0xFD, "Media Vision" },
            { (1 << 8) | 0xFE, "Numonyx Corporation" },
            { (2 << 8) | 0x01, "Cirrus Logic" },
            { (2 << 8) | 0x02, "National Instruments" },
            { (2 << 8) | 0x04, "Alcatel Mietec" },
            { (2 << 8) | 0x07, "JTAG Technologies" },
            { (2 << 8) | 0x08, "BAE Systems (Loral)" },
            { (2 << 8) | 0x0B, "Bestlink Systems" },
            { (2 << 8) | 0x0D, "GENNUM" },
            { (2 << 8) | 0x0E, "Imagination Technologies Limited" },
            { (2 << 8) | 0x10, "Chip Express" },
            { (2 << 8) | 0x13, "TCSI" },
            { (2 << 8) | 0x15, "Hughes Aircraft" },
            { (2 << 8) | 0x16, "Lanstar Semiconductor" },
            { (2 << 8) | 0x19, "Music Semi" },
            { (2 << 8) | 0x1A, "Ericsson Components" },
            { (2 << 8) | 0x1C, "Eon Silicon Devices" },
            { (2 << 8) | 0x1F, "Integ. Memories Tech." },
            { (2 << 8) | 0x20, "Corollary Inc" },
            { (2 << 8) | 0x23, "EIV(Switzerland)" },
            { (2 << 8) | 0x25, "Zarlink (Mitel)" },
            { (2 << 8) | 0x26, "Clearpoint" },
            { (2 << 8) | 0x29, "Vanguard" },
            { (2 << 8) | 0x2A, "Hagiwara Solutions Co Ltd" },
            { (2 << 8) | 0x2C, "Celestica" },
            { (2 << 8) | 0x2F, "Rohm Company Ltd" },
            { (2 << 8) | 0x31, "Libit Signal Processing" },
            { (2 << 8) | 0x32, "Mushkin Enhanced Memory" },
            { (2 << 8) | 0x34, "Adaptec Inc" },
            { (2 << 8) | 0x37, "AMIC Technology" },
            { (2 << 8) | 0x38, "Adobe Systems" },
            { (2 << 8) | 0x3B, "Newport Digital" },
            { (2 << 8) | 0x3D, "T Square" },
            { (2 << 8) | 0x3E, "Seiko Epson" },
            { (2 << 8) | 0x40, "Viking Components" },
            { (2 << 8) | 0x43, "Suwa Electronics" },
            { (2 << 8) | 0x45, "Micron CMS" },
            { (2 << 8) | 0x46, "American Computer Digital Components" },
            { (2 << 8) | 0x49, "CPU Design" },
            { (2 << 8) | 0x4A, "Price Point" },
            { (2 << 8) | 0x4C, "Tellabs" },
            { (2 << 8) | 0x4F, "Transcend Information" },
            { (2 << 8) | 0x51, "CKD Corporation Ltd" },
            { (2 << 8) | 0x52, "Capital Instruments Inc" },
            { (2 << 8) | 0x54, "Linvex Technology" },
            { (2 << 8) | 0x57, "Dynamem Inc" },
            { (2 << 8) | 0x58, "NERA ASA" },
            { (2 << 8) | 0x5B, "Acorn Computers" },
            { (2 << 8) | 0x5D, "Oak Technology Inc" },
            { (2 << 8) | 0x5E, "Itec Memory" },
            { (2 << 8) | 0x61, "Wintec Industries" },
            { (2 << 8) | 0x62, "Super PC Memory" },
            { (2 << 8) | 0x64, "Galvantech" },
            { (2 << 8) | 0x67, "GateField" },
            { (2 << 8) | 0x68, "Integrated Memory System" },
            { (2 << 8) | 0x6B, "Goldenram" },
            { (2 << 8) | 0x6D, "Cimaron Communications" },
            { (2 << 8) | 0x6E, "Nippon Steel Semi. Corp" },
            { (2 << 8) | 0x70, "AMCC" },
            { (2 << 8) | 0x73, "Digital Microwave" },
            { (2 << 8) | 0x75, "MIMOS Semiconductor" },
            { (2 << 8) | 0x76, "Advanced Fibre" },
            { (2 << 8) | 0x79, "Acbel Polytech Inc" },
            { (2 << 8) | 0x7A, "Apacer Technology" },
            { (2 << 8) | 0x7C, "FOXCONN" },
            { (2 << 8) | 0x83, "ILC Data Device" },
            { (2 << 8) | 0x85, "Micro Linear" },
            { (2 << 8) | 0x86, "Univ. of NC" },
            { (2 << 8) | 0x89, "Nchip" },
            { (2 << 8) | 0x8A, "Galileo Tech" },
            { (2 << 8) | 0x8C, "Graychip" },
            { (2 << 8) | 0x8F, "Robert Bosch" },
            { (2 << 8) | 0x91, "DATARAM" },
            { (2 << 8) | 0x92, "United Microelectronics Corp" },
            { (2 << 8) | 0x94, "Smart Modular" },
            { (2 << 8) | 0x97, "Qlogic" },
            { (2 << 8) | 0x98, "Kingston" },
            { (2 << 8) | 0x9B, "SpaSE" },
            { (2 << 8) | 0x9D, "Integrated Silicon Solution (ISSI)" },
            { (2 << 8) | 0x9E, "DoD" },
            { (2 << 8) | 0xA1, "Dallas Semiconductor" },
            { (2 << 8) | 0xA2, "Omnivision" },
            { (2 << 8) | 0xA4, "Novatel Wireless" },
            { (2 << 8) | 0xA7, "Cabletron" },
            { (2 << 8) | 0xA8, "STEC (Silicon Tech)" },
            { (2 << 8) | 0xAB, "Vantis" },
            { (2 << 8) | 0xAD, "Century" },
            { (2 << 8) | 0xAE, "Hal Computers" },
            { (2 << 8) | 0xB0, "Juniper Networks" },
            { (2 << 8) | 0xB3, "Tundra Semiconductor" },
            { (2 << 8) | 0xB5, "LightSpeed Semi." },
            { (2 << 8) | 0xB6, "ZSP Corp" },
            { (2 << 8) | 0xB9, "Dynachip" },
            { (2 << 8) | 0xBA, "PNY Technologies Inc" },
            { (2 << 8) | 0xBC, "MMC Networks" },
            { (2 << 8) | 0xBF, "Broadcom" },
            { (2 << 8) | 0xC1, "V3 Semiconductor" },
            { (2 << 8) | 0xC2, "Flextronics (Orbit Semiconductor)" },
            { (2 << 8) | 0xC4, "Transmeta" },
            { (2 << 8) | 0xC7, "Enhance 3000 Inc" },
            { (2 << 8) | 0xC8, "Tower Semiconductor" },
            { (2 << 8) | 0xCB, "Maxim Integrated Product" },
            { (2 << 8) | 0xCD, "Centaur Technology" },
            { (2 << 8) | 0xCE, "Unigen Corporation" },
            { (2 << 8) | 0xD0, "Memory Card Technology" },
            { (2 << 8) | 0xD3, "Aica Kogyo Ltd" },
            { (2 << 8) | 0xD5, "MSC Vertriebs GmbH" },
            { (2 << 8) | 0xD6, "AKM Company Ltd" },
            { (2 << 8) | 0xD9, "GSI Technology" },
            { (2 << 8) | 0xDA, "Dane-Elec (C Memory)" },
            { (2 << 8) | 0xDC, "Lara Technology" },
            { (2 << 8) | 0xDF, "Tanisys Technology" },
            { (2 << 8) | 0xE0, "Truevision" },
            { (2 << 8) | 0xE3, "MGV Memory" },
            { (2 << 8) | 0xE5, "Gadzoox Networks" },
            { (2 << 8) | 0xE6, "Multi Dimensional Cons." },
            { (2 << 8) | 0xE9, "Triscend" },
            { (2 << 8) | 0xEA, "XaQti" },
            { (2 << 8) | 0xEC, "Clear Logic" },
            { (2 << 8) | 0xEF, "Advantage Memory" },
            { (2 << 8) | 0xF1, "LeCroy" },
            { (2 << 8) | 0xF2, "Yamaha Corporation" },
            { (2 << 8) | 0xF4, "NetLogic Microsystems" },
            { (2 << 8) | 0xF7, "BF Goodrich Data." },
            { (2 << 8) | 0xF8, "Epigram" },
            { (2 << 8) | 0xFB, "Admor Memory" },
            { (2 << 8) | 0xFD, "Quadratics Superconductor" },
            { (2 << 8) | 0xFE, "3COM" },
            { (3 << 8) | 0x01, "Camintonn Corporation" },
            { (3 << 8) | 0x02, "ISOA Incorporated" },
            { (3 << 8) | 0x04, "ADMtek Incorporated" },
            { (3 << 8) | 0x07, "MOSAID Technologies" },
            { (3 << 8) | 0x08, "Ardent Technologies" },
            { (3 << 8) | 0x0B, "Allayer Technologies" },
            { (3 << 8) | 0x0D, "Oasis Semiconductor" },
            { (3 << 8) | 0x0E, "Novanet Semiconductor" },
            { (3 << 8) | 0x10, "Power General" },
            { (3 << 8) | 0x13, "Telocity" },
            { (3 << 8) | 0x15, "Symagery Microsystems" },
            { (3 << 8) | 0x16, "C-Port Corporation" },
            { (3 << 8) | 0x19, "Malleable Technologies" },
            { (3 << 8) | 0x1A, "Kendin Communications" },
            { (3 << 8) | 0x1C, "Sanmina Corporation" },
            { (3 << 8) | 0x1F, "Actrans System Inc" },
            { (3 << 8) | 0x20, "ALPHA Technologies" },
            { (3 << 8) | 0x23, "Align Manufacturing" },
            { (3 << 8) | 0x25, "Chameleon Systems" },
            { (3 << 8) | 0x26, "Aplus Flash Technology" },
            { (3 << 8) | 0x29, "ADTEC Corporation" },
            { (3 << 8) | 0x2A, "Kentron Technologies" },
            { (3 << 8) | 0x2C, "Tezzaron Semiconductor" },
            { (3 << 8) | 0x2F, "Siemens AG" },
            { (3 << 8) | 0x31, "Itautec SA" },
            { (3 << 8) | 0x32, "Radiata Inc" },
            { (3 << 8) | 0x34, "Legend" },
            { (3 << 8) | 0x37, "Enikia Incorporated" },
            { (3 << 8) | 0x38, "SwitchOn Networks" },
            { (3 << 8) | 0x3B, "ESS Technology" },
            { (3 << 8) | 0x3D, "Excess Bandwidth" },
            { (3 << 8) | 0x3E, "West Bay Semiconductor" },
            { (3 << 8) | 0x40, "Newport Communications" },
            { (3 << 8) | 0x43, "Intellitech Corporation" },
            { (3 << 8) | 0x45, "Ishoni Networks" },
            { (3 << 8) | 0x46, "Silicon Spice" },
            { (3 << 8) | 0x49, "Centillium Communications" },
            { (3 << 8) | 0x4A, "W.L. Gore" },
            { (3 << 8) | 0x4C, "GlobeSpan" },
            { (3 << 8) | 0x4F, "Saifun Semiconductors" },
            { (3 << 8) | 0x51, "MetaLink Technologies" },
            { (3 << 8) | 0x52, "Feiya Technology" },
            { (3 << 8) | 0x54, "Shikatronics" },
            { (3 << 8) | 0x57, "Com-Tier" },
            { (3 << 8) | 0x58, "Malaysia Micro Solutions" },
            { (3 << 8) | 0x5B, "Anadigm (Anadyne)" },
            { (3 << 8) | 0x5D, "Mellanox Technologies" },
            { (3 << 8) | 0x5E, "Tenx Technologies" },
            { (3 << 8) | 0x61, "Skyup Technology" },
            { (3 << 8) | 0x62, "HiNT Corporation" },
            { (3 << 8) | 0x64, "MDT Technologies GmbH" },
            { (3 << 8) | 0x67, "AVED Memory" },
            { (3 << 8) | 0x68, "Legerity" },
            { (3 << 8) | 0x6B, "nCUBE" },
            { (3 << 8) | 0x6D, "FDK Corporation" },
            { (3 << 8) | 0x6E, "High Bandwidth Access" },
            { (3 << 8) | 0x70, "BRECIS" },
            { (3 << 8) | 0x73, "Chicory Systems" },
            { (3 << 8) | 0x75, "Fast-Chip" },
            { (3 << 8) | 0x76, "Zucotto Wireless" },
            { (3 << 8) | 0x79, "eSilicon" },
            { (3 << 8) | 0x7A, "Morphics Technology" },
            { (3 << 8) | 0x7C, "Silicon Wave" },
            { (3 << 8) | 0x83, "Agate Semiconductor" },
            { (3 << 8) | 0x85, "HYPERTEC" },
            { (3 << 8) | 0x86, "Adhoc Technologies" },
            { (3 << 8) | 0x89, "Switchcore" },
            { (3 << 8) | 0x8A, "Cisco Systems Inc" },
            { (3 << 8) | 0x8C, "WorkX AG (Wichman)" },
            { (3 << 8) | 0x8F, "E-M Solutions" },
            { (3 << 8) | 0x91, "Advanced Hardware Arch." },
            { (3 << 8) | 0x92, "Inova Semiconductors GmbH" },
            { (3 << 8) | 0x94, "Delkin Devices" },
            { (3 << 8) | 0x97, "SiberCore Technologies" },
            { (3 << 8) | 0x98, "Southland Microsystems" },
            { (3 << 8) | 0x9B, "Great Technology Microcomputer" },
            { (3 << 8) | 0x9D, "HADCO Corporation" },
            { (3 << 8) | 0x9E, "Corsair" },
            { (3 << 8) | 0xA1, "Silicon Laboratories Inc (Cygnal)" },
            { (3 << 8) | 0xA2, "Artesyn Technologies" },
            { (3 << 8) | 0xA4, "Peregrine Semiconductor" },
            { (3 << 8) | 0xA7, "MIPS Technologies" },
            { (3 << 8) | 0xA8, "Chrysalis ITS" },
            { (3 << 8) | 0xAB, "Win Technologies" },
            { (3 << 8) | 0xAD, "Extreme Packet Devices" },
            { (3 << 8) | 0xAE, "RF Micro Devices" },
            { (3 << 8) | 0xB0, "Sarnoff Corporation" },
            { (3 << 8) | 0xB3, "Benchmark Elect. (AVEX)" },
            { (3 << 8) | 0xB5, "SpecTek Incorporated" },
            { (3 << 8) | 0xB6, "Hi/fn" },
            { (3 << 8) | 0xB9, "AANetcom Incorporated" },
            { (3 << 8) | 0xBA, "Micro Memory Bank" },
            { (3 << 8) | 0xBC, "Virata Corporation" },
            { (3 << 8) | 0xBF, "DSP Group" },
            { (3 << 8) | 0xC1, "Chip2Chip Incorporated" },
            { (3 << 8) | 0xC2, "Phobos Corporation" },
            { (3 << 8) | 0xC4, "Nordic VLSI ASA" },
            { (3 << 8) | 0xC7, "Alchemy Semiconductor" },
            { (3 << 8) | 0xC8, "Agilent Technologies" },
            { (3 << 8) | 0xCB, "HanBit Electronics" },
            { (3 << 8) | 0xCD, "Element 14" },
            { (3 << 8) | 0xCE, "Pycon" },
            { (3 << 8) | 0xD0, "Sibyte Incorporated" },
            { (3 << 8) | 0xD3, "I & C Technology" },
            { (3 << 8) | 0xD5, "Elektrobit" },
            { (3 << 8) | 0xD6, "Megic" },
            { (3 << 8) | 0xD9, "Hyperchip" },
            { (3 << 8) | 0xDA, "Gemstone Communications" },
            { (3 << 8) | 0xDC, "3ParData" },
            { (3 << 8) | 0xDF, "Helix AG" },
            { (3 << 8) | 0xE0, "Domosys" },
            { (3 << 8) | 0xE3, "Chiaro" },
            { (3 << 8) | 0xE5, "Exbit Technology A/S" },
            { (3 << 8) | 0xE6, "Integrated Technology Express" },
            { (3 << 8) | 0xE9, "Jasmine Networks" },
            { (3 << 8) | 0xEA, "Caspian Networks" },
            { (3 << 8) | 0xEC, "Silicon Access Networks" },
            { (3 << 8) | 0xEF, "MultiLink Technology" },
            { (3 << 8) | 0xF1, "World Wide Packets" },
            { (3 << 8) | 0xF2, "APW" },
            { (3 << 8) | 0xF4, "Xstream Logic" },
            { (3 << 8) | 0xF7, "Realchip" },
            { (3 << 8) | 0xF8, "Galaxy Power" },
            { (3 << 8) | 0xFB, "Accelerant Networks" },
            { (3 << 8) | 0xFD, "SandCraft" },
            { (3 << 8) | 0xFE, "Elpida" },
            { (4 << 8) | 0x01, "Solectron" },
            { (4 << 8) | 0x02, "Optosys Technologies" },
            { (4 << 8) | 0x04, "TriMedia Technologies" },
            { (4 << 8) | 0x07, "Optillion" },
            { (4 << 8) | 0x08, "Terago Communications" },
            { (4 << 8) | 0x0B, "Nanya Technology" },
            { (4 << 8) | 0x0D, "Mysticom" },
            { (4 << 8) | 0x0E, "LightSand Communications" },
            { (4 << 8) | 0x10, "Agere Systems" },
            { (4 << 8) | 0x13, "Golden Empire" },
            { (4 << 8) | 0x15, "Tioga Technologies" },
            { (4 << 8) | 0x16, "Netlist" },
            { (4 << 8) | 0x19, "Centon Electronics" },
            { (4 << 8) | 0x1A, "Tyco Electronics" },
            { (4 << 8) | 0x1C, "Zettacom" },
            { (4 << 8) | 0x1F, "Aspex Technology" },
            { (4 << 8) | 0x20, "F5 Networks" },
            { (4 << 8) | 0x23, "Acorn Networks" },
            { (4 << 8) | 0x25, "Kingmax Semiconductor" },
            { (4 << 8) | 0x26, "BOPS" },
            { (4 << 8) | 0x29, "eMemory Technology" },
            { (4 << 8) | 0x2A, "Procket Networks" },
            { (4 << 8) | 0x2C, "Trebia Networks" },
            { (4 << 8) | 0x2F, "Ample Communications" },
            { (4 << 8) | 0x31, "Astute Networks" },
            { (4 << 8) | 0x32, "Azanda Network Devices" },
            { (4 << 8) | 0x34, "Tekmos" },
            { (4 << 8) | 0x37, "Firecron Ltd" },
            { (4 << 8) | 0x38, "Resonext Communications" },
            { (4 << 8) | 0x3B, "Concept Computer" },
            { (4 << 8) | 0x3D, "3Dlabs" },
            { (4 << 8) | 0x3E, "c’t Magazine" },
            { (4 << 8) | 0x40, "Silicon Packets" },
            { (4 << 8) | 0x43, "Semicon Devices Singapore" },
            { (4 << 8) | 0x45, "Improv Systems" },
            { (4 << 8) | 0x46, "INDUSYS GmbH" },
            { (4 << 8) | 0x49, "Ritek Corp" },
            { (4 << 8) | 0x4A, "empowerTel Networks" },
            { (4 << 8) | 0x4C, "Cavium Networks" },
            { (4 << 8) | 0x4F, "Intrinsity" },
            { (4 << 8) | 0x51, "Terawave Communications" },
            { (4 << 8) | 0x52, "IceFyre Semiconductor" },
            { (4 << 8) | 0x54, "Picochip Designs Ltd" },
            { (4 << 8) | 0x57, "Pijnenburg Securealink" },
            { (4 << 8) | 0x58, "takeMS - Ultron AG" },
            { (4 << 8) | 0x5B, "Nazomi Communications" },
            { (4 << 8) | 0x5D, "Rockwell Collins" },
            { (4 << 8) | 0x5E, "Picocel Co Ltd (Paion)" },
            { (4 << 8) | 0x61, "SiCon Video" },
            { (4 << 8) | 0x62, "NanoAmp Solutions" },
            { (4 << 8) | 0x64, "PrairieComm" },
            { (4 << 8) | 0x67, "MtekVision (Atsana)" },
            { (4 << 8) | 0x68, "Allegro Networks" },
            { (4 << 8) | 0x6B, "NVIDIA" },
            { (4 << 8) | 0x6D, "Memorysolution GmbH" },
            { (4 << 8) | 0x6E, "Litchfield Communication" },
            { (4 << 8) | 0x70, "Teradiant Networks" },
            { (4 << 8) | 0x73, "RAM Components" },
            { (4 << 8) | 0x75, "ClearSpeed" },
            { (4 << 8) | 0x76, "Matsushita Battery" },
            { (4 << 8) | 0x79, "Utron Technology" },
            { (4 << 8) | 0x7A, "Astec International" },
            { (4 << 8) | 0x7C, "Redux Communications" },
            { (4 << 8) | 0x83, "Buffalo (Formerly Melco)" },
            { (4 << 8) | 0x85, "Cyan Technologies" },
            { (4 << 8) | 0x86, "Global Locate" },
            { (4 << 8) | 0x89, "Ikanos Communications" },
            { (4 << 8) | 0x8A, "Princeton Technology" },
            { (4 << 8) | 0x8C, "Elite Flash Storage" },
            { (4 << 8) | 0x8F, "ATI Technologies" },
            { (4 << 8) | 0x91, "NeoMagic" },
            { (4 << 8) | 0x92, "AuroraNetics" },
            { (4 << 8) | 0x94, "Mushkin" },
            { (4 << 8) | 0x97, "TeraLogic" },
            { (4 << 8) | 0x98, "Cicada Semiconductor" },
            { (4 << 8) | 0x9B, "Magis Works" },
            { (4 << 8) | 0x9D, "Cogency Semiconductor" },
            { (4 << 8) | 0x9E, "Chipcon AS" },
            { (4 << 8) | 0xA1, "Programmable Silicon Solutions" },
            { (4 << 8) | 0xA2, "ChipWrights" },
            { (4 << 8) | 0xA4, "Quicklogic" },
            { (4 << 8) | 0xA7, "Flasys" },
            { (4 << 8) | 0xA8, "BitBlitz Communications" },
            { (4 << 8) | 0xAB, "Purple Ray" },
            { (4 << 8) | 0xAD, "Delta Electronics" },
            { (4 << 8) | 0xAE, "Onex Communications" },
            { (4 << 8) | 0xB0, "Memory Experts Intl" },
            { (4 << 8) | 0xB3, "Dibcom" },
            { (4 << 8) | 0xB5, "API NetWorks" },
            { (4 << 8) | 0xB6, "Bay Microsystems" },
            { (4 << 8) | 0xB9, "Tachys Technologies" },
            { (4 << 8) | 0xBA, "Equator Technology" },
            { (4 << 8) | 0xBC, "SILCOM" },
            { (4 << 8) | 0xBF, "Sanera Systems" },
            { (4 << 8) | 0xC1, "Viasystems Group" },
            { (4 << 8) | 0xC2, "Simtek" },
            { (4 << 8) | 0xC4, "Satron Handelsges" },
            { (4 << 8) | 0xC7, "Corrent" },
            { (4 << 8) | 0xC8, "Infrant Technologies" },
            { (4 << 8) | 0xCB, "Hypertec" },
            { (4 << 8) | 0xCD, "PLX Technology" },
            { (4 << 8) | 0xCE, "Massana Design" },
            { (4 << 8) | 0xD0, "Valence Semiconductor" },
            { (4 << 8) | 0xD3, "Primarion" },
            { (4 << 8) | 0xD5, "Silverback Systems" },
            { (4 << 8) | 0xD6, "Jade Star Technologies" },
            { (4 << 8) | 0xD9, "Cambridge Silicon Radio" },
            { (4 << 8) | 0xDA, "Swissbit" },
            { (4 << 8) | 0xDC, "eWave System" },
            { (4 << 8) | 0xDF, "Alphamosaic Ltd" },
            { (4 << 8) | 0xE0, "Sandburst" },
            { (4 << 8) | 0xE3, "Ericsson Technology" },
            { (4 << 8) | 0xE5, "Mitac International" },
            { (4 << 8) | 0xE6, "Layer N Networks" },
            { (4 << 8) | 0xE9, "Marvell Semiconductors" },
            { (4 << 8) | 0xEA, "Netergy Microelectronic" },
            { (4 << 8) | 0xEC, "Internet Machines" },
            { (4 << 8) | 0xEF, "Accton Technology" },
            { (4 << 8) | 0xF1, "Scaleo Chip" },
            { (4 << 8) | 0xF2, "Cortina Systems" },
            { (4 << 8) | 0xF4, "Raqia Networks" },
            { (4 << 8) | 0xF7, "Xelerated" },
            { (4 << 8) | 0xF8, "SimpleTech" },
            { (4 << 8) | 0xFB, "AVM gmbH" },
            { (4 << 8) | 0xFD, "Dot Hill Systems" },
            { (4 << 8) | 0xFE, "TeraChip" },
            { (5 << 8) | 0x01, "T-RAM Incorporated" },
            { (5 << 8) | 0x02, "Innovics Wireless" },
            { (5 << 8) | 0x04, "KeyEye Communications" },
            { (5 << 8) | 0x07, "Dotcast" },
            { (5 << 8) | 0x08, "Silicon Mountain Memory" },
            { (5 << 8) | 0x0B, "Galazar Networks" },
            { (5 << 8) | 0x0D, "Patriot Scientific" },
            { (5 << 8) | 0x0E, "Neoaxiom Corporation" },
            { (5 << 8) | 0x10, "Scaleo Chip" },
            { (5 << 8) | 0x13, "Digital Communications Technology Inc" },
            { (5 << 8) | 0x15, "Fulcrum Microsystems" },
            { (5 << 8) | 0x16, "Positivo Informatica Ltd" },
            { (5 << 8) | 0x19, "Zhiying Software" },
            { (5 << 8) | 0x1A, "ParkerVision Inc" },
            { (5 << 8) | 0x1C, "Skyworks Solutions" },
            { (5 << 8) | 0x1F, "Zensys A/S" },
            { (5 << 8) | 0x20, "Legend Silicon Corp" },
            { (5 << 8) | 0x23, "Renesas Electronics" },
            { (5 << 8) | 0x25, "Phyworks" },
            { (5 << 8) | 0x26, "MediaTek" },
            { (5 << 8) | 0x29, "Wintegra Ltd" },
            { (5 << 8) | 0x2A, "Mathstar" },
            { (5 << 8) | 0x2C, "Oplus Technologies" },
            { (5 << 8) | 0x2F, "Radia Communications" },
            { (5 << 8) | 0x31, "Emuzed" },
            { (5 << 8) | 0x32, "LOGIC Devices" },
            { (5 << 8) | 0x34, "Quake Technologies" },
            { (5 << 8) | 0x37, "Kongsberg Maritime" },
            { (5 << 8) | 0x38, "Faraday Technology" },
            { (5 << 8) | 0x3B, "ARM Ltd" },
            { (5 << 8) | 0x3D, "Vativ Technologies" },
            { (5 << 8) | 0x3E, "Endicott Interconnect Technologies" },
            { (5 << 8) | 0x40, "Bandspeed" },
            { (5 << 8) | 0x43, "Ramaxel Technology" },
            { (5 << 8) | 0x45, "Axis Communications" },
            { (5 << 8) | 0x46, "Legacy Electronics" },
            { (5 << 8) | 0x49, "MobilEye Technologies" },
            { (5 << 8) | 0x4A, "Excel Semiconductor" },
            { (5 << 8) | 0x4C, "VirtualDigm" },
            { (5 << 8) | 0x4F, "Yield Microelectronics" },
            { (5 << 8) | 0x51, "KINGBOX Technology Co Ltd" },
            { (5 << 8) | 0x52, "Ceva" },
            { (5 << 8) | 0x54, "Advance Modules" },
            { (5 << 8) | 0x57, "Goal Semiconductor" },
            { (5 << 8) | 0x58, "ARC International" },
            { (5 << 8) | 0x5B, "Key Stream" },
            { (5 << 8) | 0x5D, "Adimos" },
            { (5 << 8) | 0x5E, "SiGe Semiconductor" },
            { (5 << 8) | 0x61, "Genesis Microchip Inc" },
            { (5 << 8) | 0x62, "Vihana Inc" },
            { (5 << 8) | 0x64, "GateChange Technologies" },
            { (5 << 8) | 0x67, "Gigaram" },
            { (5 << 8) | 0x68, "Enigma Semiconductor Inc" },
            { (5 << 8) | 0x6B, "Mediaworks Integrated Systems" },
            { (5 << 8) | 0x6D, "Supreme Top Technology Ltd" },
            { (5 << 8) | 0x6E, "MicroDisplay Corporation" },
            { (5 << 8) | 0x70, "Sinett Corporation" },
            { (5 << 8) | 0x73, "SiRF Technology" },
            { (5 << 8) | 0x75, "SMaL Camera Technologies" },
            { (5 << 8) | 0x76, "Thomson SC" },
            { (5 << 8) | 0x79, "SigmaTel" },
            { (5 << 8) | 0x7A, "Arkados" },
            { (5 << 8) | 0x7C, "Eudar Technology Inc" },
            { (5 << 8) | 0x83, "Teknovus" },
            { (5 << 8) | 0x85, "Runcom Technologies" },
            { (5 << 8) | 0x86, "RedSwitch" },
            { (5 << 8) | 0x89, "Signia Technologies" },
            { (5 << 8) | 0x8A, "Pixim" },
            { (5 << 8) | 0x8C, "White Electronic Designs" },
            { (5 << 8) | 0x8F, "3Y Power Technology" },
            { (5 << 8) | 0x91, "Potentia Power Systems" },
            { (5 << 8) | 0x92, "C-guys Incorporated" },
            { (5 << 8) | 0x94, "Silicon-Based Technology" },
            { (5 << 8) | 0x97, "XIOtech Corporation" },
            { (5 << 8) | 0x98, "PortalPlayer" },
            { (5 << 8) | 0x9B, "Phonex Broadband" },
            { (5 << 8) | 0x9D, "Entropic Communications" },
            { (5 << 8) | 0x9E, "I’M Intelligent Memory Ltd" },
            { (5 << 8) | 0xA1, "Sci-worx GmbH" },
            { (5 << 8) | 0xA2, "SMSC (Standard Microsystems)" },
            { (5 << 8) | 0xA4, "Raza Microelectronics" },
            { (5 << 8) | 0xA7, "Non-cents Productions" },
            { (5 << 8) | 0xA8, "US Modular" },
            { (5 << 8) | 0xAB, "StarCore" },
            { (5 << 8) | 0xAD, "Mindspeed" },
            { (5 << 8) | 0xAE, "Just Young Computer" },
            { (5 << 8) | 0xB0, "OCZ" },
            { (5 << 8) | 0xB3, "Inphi Corporation" },
            { (5 << 8) | 0xB5, "Vixel" },
            { (5 << 8) | 0xB6, "SolusTek" },
            { (5 << 8) | 0xB9, "Altium Ltd" },
            { (5 << 8) | 0xBA, "Insyte" },
            { (5 << 8) | 0xBC, "DigiVision" },
            { (5 << 8) | 0xBF, "Pericom" },
            { (5 << 8) | 0xC1, "LeWiz Communications" },
            { (5 << 8) | 0xC2, "CPU Technology" },
            { (5 << 8) | 0xC4, "DSP Group" },
            { (5 << 8) | 0xC7, "Chrontel" },
            { (5 << 8) | 0xC8, "Powerchip Semiconductor" },
            { (5 << 8) | 0xCB, "A-DATA Technology" },
            { (5 << 8) | 0xCD, "G Skill Intl" },
            { (5 << 8) | 0xCE, "Quanta Computer" },
            { (5 << 8) | 0xD0, "Afa Technologies" },
            { (5 << 8) | 0xD3, "iStor Networks" },
            { (5 << 8) | 0xD5, "Microsoft" },
            { (5 << 8) | 0xD6, "Open-Silicon" },
            { (5 << 8) | 0xD9, "Simmtec" },
            { (5 << 8) | 0xDA, "Metanoia" },
            { (5 << 8) | 0xDC, "Lowrance Electronics" },
            { (5 << 8) | 0xDF, "Fodus Communications" },
            { (5 << 8) | 0xE0, "Credence Systems Corp" },
            { (5 << 8) | 0xE3, "WIS Technologies" },
            { (5 << 8) | 0xE5, "High Density Devices AS" },
            { (5 << 8) | 0xE6, "Synopsys" },
            { (5 << 8) | 0xE9, "Century Micro Inc" },
            { (5 << 8) | 0xEA, "Icera Semiconductor" },
            { (5 << 8) | 0xEC, "O’Neil Product Development" },
            { (5 << 8) | 0xEF, "Team Group Inc" },
            { (5 << 8) | 0xF1, "Toshiba Corporation" },
            { (5 << 8) | 0xF2, "Tensilica" },
            { (5 << 8) | 0xF4, "Bacoc Inc" },
            { (5 << 8) | 0xF7, "Airgo Networks" },
            { (5 << 8) | 0xF8, "Wisair Ltd" },
            { (5 << 8) | 0xFB, "Compete IT gmbH Co KG" },
            { (5 << 8) | 0xFD, "Focus Enhancements" },
            { (5 << 8) | 0xFE, "Xyratex" },
            { (6 << 8) | 0x01, "Specular Networks" },
            { (6 << 8) | 0x02, "Patriot Memory (PDP Systems)" },
            { (6 << 8) | 0x04, "Silicon Optix" },
            { (6 << 8) | 0x07, "Stargen Inc" },
            { (6 << 8) | 0x08, "NetCell Corporation" },
            { (6 << 8) | 0x0B, "Xsigo Systems Inc" },
            { (6 << 8) | 0x0D, "Tier 1 Multichip Solutions" },
            { (6 << 8) | 0x0E, "CWRL Labs" },
            { (6 << 8) | 0x10, "Gigaram Inc" },
            { (6 << 8) | 0x13, "P.A. Semi Inc" },
            { (6 << 8) | 0x15, "c2 Microsystems Inc" },
            { (6 << 8) | 0x16, "Level5 Networks" },
            { (6 << 8) | 0x19, "02IC Co Ltd" },
            { (6 << 8) | 0x1A, "Tabula Inc" },
            { (6 << 8) | 0x1C, "Chelsio Communications" },
            { (6 << 8) | 0x1F, "EADS Astrium" },
            { (6 << 8) | 0x20, "Terra Semiconductor Inc" },
            { (6 << 8) | 0x23, "Tzero" },
            { (6 << 8) | 0x25, "Power-One" },
            { (6 << 8) | 0x26, "Pulse~LINK Inc" },
            { (6 << 8) | 0x29, "Telegent Systems USA Inc" },
            { (6 << 8) | 0x2A, "Atrua Technologies Inc" },
            { (6 << 8) | 0x2C, "eRide Inc" },
            { (6 << 8) | 0x2F, "neoOne Technology Inc" },
            { (6 << 8) | 0x31, "Stream Processors Inc" },
            { (6 << 8) | 0x32, "Focus Enhancements" },
            { (6 << 8) | 0x34, "uNav Microelectronics" },
            { (6 << 8) | 0x37, "Newport Media Inc" },
            { (6 << 8) | 0x38, "VMTS" },
            { (6 << 8) | 0x3B, "Solid State System Co Ltd" },
            { (6 << 8) | 0x3D, "Artimi" },
            { (6 << 8) | 0x3E, "Power Quotient International" },
            { (6 << 8) | 0x40, "ADTechnology" },
            { (6 << 8) | 0x43, "Ventura Technology Group" },
            { (6 << 8) | 0x45, "M.H.S. SAS" },
            { (6 << 8) | 0x46, "Micro Star International" },
            { (6 << 8) | 0x49, "Broad Reach Engineering Co" },
            { (6 << 8) | 0x4A, "Semiconductor Mfg Intl Corp" },
            { (6 << 8) | 0x4C, "FCI USA Inc" },
            { (6 << 8) | 0x4F, "Spans Logic" },
            { (6 << 8) | 0x51, "Qimonda" },
            { (6 << 8) | 0x52, "New Japan Radio Co Ltd" },
            { (6 << 8) | 0x54, "Montalvo Systems" },
            { (6 << 8) | 0x57, "AENEON" },
            { (6 << 8) | 0x58, "Lorom Industrial Co Ltd" },
            { (6 << 8) | 0x5B, "Nethra Imaging" },
            { (6 << 8) | 0x5D, "CompuStocx (CSX)" },
            { (6 << 8) | 0x5E, "Methode Electronics Inc" },
            { (6 << 8) | 0x61, "Septentrio NV" },
            { (6 << 8) | 0x62, "Goldenmars Technology Inc" },
            { (6 << 8) | 0x64, "Cochlear Ltd" },
            { (6 << 8) | 0x67, "Spansion Inc" },
            { (6 << 8) | 0x68, "Taiwan Semiconductor Mfg" },
            { (6 << 8) | 0x6B, "Mobilygen Corporation" },
            { (6 << 8) | 0x6D, "Cswitch Corporation" },
            { (6 << 8) | 0x6E, "Haier (Beijing) IC Design Co" },
            { (6 << 8) | 0x70, "Axel Electronics Co Ltd" },
            { (6 << 8) | 0x73, "Vivace Semiconductor" },
            { (6 << 8) | 0x75, "Octalica" },
            { (6 << 8) | 0x76, "InterDigital Communications" },
            { (6 << 8) | 0x79, "Availink" },
            { (6 << 8) | 0x7A, "Quartics Inc" },
            { (6 << 8) | 0x7C, "Innovaciones Microelectronicas" },
            { (6 << 8) | 0x83, "U-Chip Technology Corp" },
            { (6 << 8) | 0x85, "Greenfield Networks" },
            { (6 << 8) | 0x86, "CompuRAM GmbH" },
            { (6 << 8) | 0x89, "Excalibrus Technologies Ltd" },
            { (6 << 8) | 0x8A, "SCM Microsystems" },
            { (6 << 8) | 0x8C, "CHIPS & Systems Inc" },
            { (6 << 8) | 0x8F, "Teradici" },
            { (6 << 8) | 0x91, "g2 Microsystems" },
            { (6 << 8) | 0x92, "PowerFlash Semiconductor" },
            { (6 << 8) | 0x94, "NovaTech Solutions S.A." },
            { (6 << 8) | 0x97, "COS Memory AG" },
            { (6 << 8) | 0x98, "Innovasic Semiconductor" },
            { (6 << 8) | 0x9B, "Crucial Technology" },
            { (6 << 8) | 0x9D, "Solarflare Communications" },
            { (6 << 8) | 0x9E, "Xambala Inc" },
            { (6 << 8) | 0xA1, "Imaging Works Inc" },
            { (6 << 8) | 0xA2, "Astute Networks Inc" },
            { (6 << 8) | 0xA4, "Emulex" },
            { (6 << 8) | 0xA7, "Hon Hai Precision Industry" },
            { (6 << 8) | 0xA8, "White Rock Networks Inc" },
            { (6 << 8) | 0xAB, "Acbel Polytech Inc" },
            { (6 << 8) | 0xAD, "ULi Electronics Inc" },
            { (6 << 8) | 0xAE, "Magnum Semiconductor Inc" },
            { (6 << 8) | 0xB0, "Connex Technology Inc" },
            { (6 << 8) | 0xB3, "Telecis Wireless Inc" },
            { (6 << 8) | 0xB5, "Tarari Inc" },
            { (6 << 8) | 0xB6, "Ambric Inc" },
            { (6 << 8) | 0xB9, "Enuclia Semiconductor Inc" },
            { (6 << 8) | 0xBA, "Virtium Technology Inc" },
            { (6 << 8) | 0xBC, "Kian Tech LLC" },
            { (6 << 8) | 0xBF, "Avago Technologies" },
            { (6 << 8) | 0xC1, "Sigma Designs" },
            { (6 << 8) | 0xC2, "SiCortex Inc" },
            { (6 << 8) | 0xC4, "eASIC" },
            { (6 << 8) | 0xC7, "Rapport Inc" },
            { (6 << 8) | 0xC8, "Makway International" },
            { (6 << 8) | 0xCB, "SiConnect" },
            { (6 << 8) | 0xCD, "Validity Sensors" },
            { (6 << 8) | 0xCE, "Coney Technology Co Ltd" },
            { (6 << 8) | 0xD0, "Neterion Inc" },
            { (6 << 8) | 0xD3, "Velogix" },
            { (6 << 8) | 0xD5, "iVivity Inc" },
            { (6 << 8) | 0xD6, "Walton Chaintech" },
            { (6 << 8) | 0xD9, "Radiospire Networks" },
            { (6 << 8) | 0xDA, "Sensio Technologies Inc" },
            { (6 << 8) | 0xDC, "Hexon Technology Pte Ltd" },
            { (6 << 8) | 0xDF, "Connect One Ltd" },
            { (6 << 8) | 0xE0, "Opulan Technologies" },
            { (6 << 8) | 0xE3, "Kreton Corporation" },
            { (6 << 8) | 0xE5, "Altair Semiconductor" },
            { (6 << 8) | 0xE6, "NetEffect Inc" },
            { (6 << 8) | 0xE9, "Emphany Systems Inc" },
            { (6 << 8) | 0xEA, "ApaceWave Technologies" },
            { (6 << 8) | 0xEC, "Tego" },
            { (6 << 8) | 0xEF, "MetaRAM" },
            { (6 << 8) | 0xF1, "Tilera Corporation" },
            { (6 << 8) | 0xF2, "Aquantia" },
            { (6 << 8) | 0xF4, "Redpine Signals" },
            { (6 << 8) | 0xF7, "Avant Technology" },
            { (6 << 8) | 0xF8, "Asrock Inc" },
            { (6 << 8) | 0xFB, "Element CXI" },
            { (6 << 8) | 0xFD, "VeriSilicon Microelectronics" },
            { (6 << 8) | 0xFE, "W5 Networks" },
            { (7 << 8) | 0x01, "MOVEKING" },
            { (7 << 8) | 0x02, "Mavrix Technology Inc" },
            { (7 << 8) | 0x04, "Faraday Technology" },
            { (7 << 8) | 0x07, "Octasic" },
            { (7 << 8) | 0x08, "Molex Incorporated" },
            { (7 << 8) | 0x0B, "Netxen" },
            { (7 << 8) | 0x0D, "DisplayLink" },
            { (7 << 8) | 0x0E, "ZMOS Technology" },
            { (7 << 8) | 0x10, "Multigig Inc" },
            { (7 << 8) | 0x13, "BRN Phoenix" },
            { (7 << 8) | 0x15, "Ember Corporation" },
            { (7 << 8) | 0x16, "Avexir Technologies Corporation" },
            { (7 << 8) | 0x19, "XMOS Semiconductor Ltd" },
            { (7 << 8) | 0x1A, "GENUSION Inc" },
            { (7 << 8) | 0x1C, "SiliconBlue Technologies" },
            { (7 << 8) | 0x1F, "Coronis Systems" },
            { (7 << 8) | 0x20, "Achronix Semiconductor" },
            { (7 << 8) | 0x23, "Pixelworks Inc" },
            { (7 << 8) | 0x25, "Teranetics" },
            { (7 << 8) | 0x26, "Toppan Printing Co Ltd" },
            { (7 << 8) | 0x29, "I-O Data Device Inc" },
            { (7 << 8) | 0x2A, "NDS Americas Inc" },
            { (7 << 8) | 0x2C, "On Demand Microelectronics" },
            { (7 << 8) | 0x2F, "Comsys Communication Ltd" },
            { (7 << 8) | 0x31, "Javad GNSS Inc" },
            { (7 << 8) | 0x32, "Montage Technology Group" },
            { (7 << 8) | 0x34, "Super Talent" },
            { (7 << 8) | 0x37, "SiBEAM Inc" },
            { (7 << 8) | 0x38, "InicoreInc" },
            { (7 << 8) | 0x3B, "ZeroG Wireless Inc" },
            { (7 << 8) | 0x3D, "Space Micro Inc" },
            { (7 << 8) | 0x3E, "Wilocity" },
            { (7 << 8) | 0x40, "iKoa Corporation" },
            { (7 << 8) | 0x43, "Plato Networks Inc" },
            { (7 << 8) | 0x45, "Infinite-Memories" },
            { (7 << 8) | 0x46, "Parade Technologies Inc" },
            { (7 << 8) | 0x49, "Modu Ltd" },
            { (7 << 8) | 0x4A, "CEITEC" },
            { (7 << 8) | 0x4C, "XRONET Corporation" },
            { (7 << 8) | 0x4F, "TOPRAM Technology" },
            { (7 << 8) | 0x51, "Kinglife" },
            { (7 << 8) | 0x52, "Ability Industries Ltd" },
            { (7 << 8) | 0x54, "Augusta Technology Inc" },
            { (7 << 8) | 0x57, "Quixant Ltd" },
            { (7 << 8) | 0x58, "Percello Ltd" },
            { (7 << 8) | 0x5B, "FS-Semi Company Ltd" },
            { (7 << 8) | 0x5D, "SandForce Inc" },
            { (7 << 8) | 0x5E, "Lexar Media" },
            { (7 << 8) | 0x61, "Suzhou Smartek Electronics" },
            { (7 << 8) | 0x62, "Avantium Corporation" },
            { (7 << 8) | 0x64, "Valens Semiconductor Ltd" },
            { (7 << 8) | 0x67, "Zenverge Inc" },
            { (7 << 8) | 0x68, "N-trig Ltd" },
            { (7 << 8) | 0x6B, "TwinMOS" },
            { (7 << 8) | 0x6D, "V-Color Technology Inc" },
            { (7 << 8) | 0x6E, "Certicom Corporation" },
            { (7 << 8) | 0x70, "PhotoFast Global Inc" },
            { (7 << 8) | 0x73, "Energy Micro" },
            { (7 << 8) | 0x75, "CopperGate Communications" },
            { (7 << 8) | 0x76, "Holtek Semiconductor Inc" },
            { (7 << 8) | 0x79, "Red Digital Cinema" },
            { (7 << 8) | 0x7A, "Densbits Technology" },
            { (7 << 8) | 0x7C, "MoSys" },
            { (7 << 8) | 0x83, "CellGuide Ltd" },
            { (7 << 8) | 0x85, "Diablo Technologies Inc" },
            { (7 << 8) | 0x86, "Jennic" },
            { (7 << 8) | 0x89, "3Leaf Networks" },
            { (7 << 8) | 0x8A, "Bright Micron Technology" },
            { (7 << 8) | 0x8C, "NextWave Broadband Inc" },
            { (7 << 8) | 0x8F, "Tec-Hill" },
            { (7 << 8) | 0x91, "Amimon" },
            { (7 << 8) | 0x92, "Euphonic Technologies Inc" },
            { (7 << 8) | 0x94, "InSilica" },
            { (7 << 8) | 0x97, "Echelon Corporation" },
            { (7 << 8) | 0x98, "Edgewater Computer Systems" },
            { (7 << 8) | 0x9B, "Memory Corp NV" },
            { (7 << 8) | 0x9D, "Rambus Inc" },
            { (7 << 8) | 0x9E, "Andes Technology Corporation" },
            { (7 << 8) | 0xA1, "Siano Mobile Silicon Ltd" },
            { (7 << 8) | 0xA2, "Semtech Corporation" },
            { (7 << 8) | 0xA4, "Gaisler Research AB" },
            { (7 << 8) | 0xA7, "Kingxcon" },
            { (7 << 8) | 0xA8, "Silicon Integrated Systems" },
            { (7 << 8) | 0xAB, "Solomon Systech Limited" },
            { (7 << 8) | 0xAD, "Amicus Wireless Inc" },
            { (7 << 8) | 0xAE, "SMARDTV SNC" },
            { (7 << 8) | 0xB0, "Movidia Ltd" },
            { (7 << 8) | 0xB3, "Trident Microsystems" },
            { (7 << 8) | 0xB5, "Optichron Inc" },
            { (7 << 8) | 0xB6, "Future Waves UK Ltd" },
            { (7 << 8) | 0xB9, "Virident Systems" },
            { (7 << 8) | 0xBA, "M2000 Inc" },
            { (7 << 8) | 0xBC, "Gingle Technology Co Ltd" },
            { (7 << 8) | 0xBF, "Novafora Inc" },
            { (7 << 8) | 0xC1, "ASint Technology" },
            { (7 << 8) | 0xC2, "Ramtron" },
            { (7 << 8) | 0xC4, "IPtronics AS" },
            { (7 << 8) | 0xC7, "Dune Networks" },
            { (7 << 8) | 0xC8, "GigaDevice Semiconductor" },
            { (7 << 8) | 0xCB, "Northrop Grumman" },
            { (7 << 8) | 0xCD, "Sicon Semiconductor AB" },
            { (7 << 8) | 0xCE, "Atla Electronics Co Ltd" },
            { (7 << 8) | 0xD0, "Silego Technology Inc" },
            { (7 << 8) | 0xD3, "Silicon Power Computer &" },
            { (7 << 8) | 0xD5, "Nantronics Semiconductors" },
            { (7 << 8) | 0xD6, "Hilscher Gesellschaft" },
            { (7 << 8) | 0xD9, "NextIO Inc" },
            { (7 << 8) | 0xDA, "Scanimetrics Inc" },
            { (7 << 8) | 0xDC, "Infinera Corporation" },
            { (7 << 8) | 0xDF, "Teradyne Inc" },
            { (7 << 8) | 0xE0, "Memory Exchange Corp" },
            { (7 << 8) | 0xE3, "ATP Electronics Inc" },
            { (7 << 8) | 0xE5, "Agate Logic Inc" },
            { (7 << 8) | 0xE6, "Netronome" },
            { (7 << 8) | 0xE9, "SanMax Technologies Inc" },
            { (7 << 8) | 0xEA, "Contour Semiconductor Inc" },
            { (7 << 8) | 0xEC, "Silicon Systems Inc" },
            { (7 << 8) | 0xEF, "JSC ICC Milandr" },
            { (7 << 8) | 0xF1, "InnoDisk Corporation" },
            { (7 << 8) | 0xF2, "Muscle Power" },
            { (7 << 8) | 0xF4, "Innofidei" },
            { (7 << 8) | 0xF7, "Myson Century Inc" },
            { (7 << 8) | 0xF8, "FIDELIX" },
            { (7 << 8) | 0xFB, "Zempro" },
            { (7 << 8) | 0xFD, "Provigent" },
            { (7 << 8) | 0xFE, "Triad Semiconductor Inc" },
            { (8 << 8) | 0x01, "Siklu Communication Ltd" },
            { (8 << 8) | 0x02, "A Force Manufacturing Ltd" },
            { (8 << 8) | 0x04, "ALi Corp (Abilis Systems)" },
            { (8 << 8) | 0x07, "Unifosa Corporation" },
            { (8 << 8) | 0x08, "Stretch Inc" },
            { (8 << 8) | 0x0B, "EKMemory" },
            { (8 << 8) | 0x0D, "u-blox AG" },
            { (8 << 8) | 0x0E, "Carry Technology Co Ltd" },
            { (8 << 8) | 0x10, "King Tiger Technology" },
            { (8 << 8) | 0x13, "Albatron Technology Co Ltd" },
            { (8 << 8) | 0x15, "BroadLight" },
            { (8 << 8) | 0x16, "AEXEA" },
            { (8 << 8) | 0x19, "Design Art Networks" },
            { (8 << 8) | 0x1A, "Mach Xtreme Technology Ltd" },
            { (8 << 8) | 0x1C, "Ramsta" },
            { (8 << 8) | 0x1F, "Antec Hadron" },
            { (8 << 8) | 0x20, "NavCom Technology Inc" },
            { (8 << 8) | 0x23, "JSC EDC Electronics" },
            { (8 << 8) | 0x25, "Ramos Technology" },
            { (8 << 8) | 0x26, "Goldenmars Technology" },
            { (8 << 8) | 0x29, "ShenZhen MercyPower Tech" },
            { (8 << 8) | 0x2A, "Nanjing Yihuo Technology" },
            { (8 << 8) | 0x2C, "SiTel Semiconductor BV" },
            { (8 << 8) | 0x2F, "Wilocity" },
            { (8 << 8) | 0x31, "Gerad Technologies" },
            { (8 << 8) | 0x32, "Ritek Corporation" },
            { (8 << 8) | 0x34, "Memoright Corporation" },
            { (8 << 8) | 0x37, "Syndiant Inc." },
            { (8 << 8) | 0x38, "Enverv Inc" },
            { (8 << 8) | 0x3B, "Ultron AG" },
            { (8 << 8) | 0x3D, "AIM Corporation" },
            { (8 << 8) | 0x3E, "Lifetime Memory Products" },
            { (8 << 8) | 0x40, "Recore Systems B.V." },
            { (8 << 8) | 0x43, "Adesto Technologies" },
            { (8 << 8) | 0x45, "HMD Electronics AG" },
            { (8 << 8) | 0x46, "Gloway International (HK)" },
            { (8 << 8) | 0x49, "Accord Software & Systems Pvt. Ltd" },
            { (8 << 8) | 0x4A, "Active-Semi Inc" },
            { (8 << 8) | 0x4C, "TLSI Inc" },
            { (8 << 8) | 0x4F, "Orca Systems" },
            { (8 << 8) | 0x51, "GigaDevice Semiconductor (Beijing)" },
            { (8 << 8) | 0x52, "Memphis Electronic" },
            { (8 << 8) | 0x54, "Harmony Semiconductor Corp" },
            { (8 << 8) | 0x57, "Eorex Corporation" },
            { (8 << 8) | 0x58, "Xingtera" },
            { (8 << 8) | 0x5B, "Baysand Inc" },
            { (8 << 8) | 0x5D, "Wilk Elektronik S.A." },
            { (8 << 8) | 0x5E, "AAI" },
            { (8 << 8) | 0x61, "ASSIA Inc" },
            { (8 << 8) | 0x62, "Visiontek Products LLC" },
            { (8 << 8) | 0x64, "Welink Solution Inc" },
            { (8 << 8) | 0x67, "R&D Center ELVEES OJSC" },
            { (8 << 8) | 0x68, "KingboMars Technology Co Ltd" },
            { (8 << 8) | 0x6B, "Everspin Technologies" },
            { (8 << 8) | 0x6D, "Smart Storage Systems" },
            { (8 << 8) | 0x6E, "Toumaz Group" },
            { (8 << 8) | 0x70, "Panram International Corporation" },
            { (8 << 8) | 0x73, "Inuitive" },
            { (8 << 8) | 0x75, "BittWare Inc" },
            { (8 << 8) | 0x76, "GLOBALFOUNDRIES" },
            { (8 << 8) | 0x79, "AcSiP Technology Corporation" },
            { (8 << 8) | 0x7A, "Idea! Electronic Systems" },
            { (8 << 8) | 0x7C, "Hermes Testing Solutions Inc" },
            { (8 << 8) | 0x83, "Strontium" },
            { (8 << 8) | 0x85, "Siglead Inc" },
            { (8 << 8) | 0x86, "Ubicom Inc" },
            { (8 << 8) | 0x89, "Lantiq Deutschland GmbH" },
            { (8 << 8) | 0x8A, "Visipro." },
            { (8 << 8) | 0x8C, "Microelectronics Institute ZTE" },
            { (8 << 8) | 0x8F, "Nokia" },
            { (8 << 8) | 0x91, "Sierra Wireless" },
            { (8 << 8) | 0x92, "HT Micron" },
            { (8 << 8) | 0x94, "Leica Geosystems AG" },
            { (8 << 8) | 0x97, "ClariPhy Communications Inc" },
            { (8 << 8) | 0x98, "Green Plug" },
            { (8 << 8) | 0x9B, "ATO Solutions Co Ltd" },
            { (8 << 8) | 0x9D, "Greenliant Systems Ltd" },
            { (8 << 8) | 0x9E, "Teikon" },
            { (8 << 8) | 0xA1, "Shanghai Fudan Microelectronics" },
            { (8 << 8) | 0xA2, "Calxeda Inc" },
            { (8 << 8) | 0xA4, "Kandit Technology Co Ltd" },
            { (8 << 8) | 0xA7, "XeL Technology Inc" },
            { (8 << 8) | 0xA8, "Newzone Corporation" },
            { (8 << 8) | 0xAB, "Nethra Imaging Inc" },
            { (8 << 8) | 0xAD, "SolidGear Corporation" },
            { (8 << 8) | 0xAE, "Topower Computer Ind Co Ltd" },
            { (8 << 8) | 0xB0, "Profichip GmbH" },
            { (8 << 8) | 0xB3, "Gomos Technology Limited" },
            { (8 << 8) | 0xB5, "D-Broad Inc" },
            { (8 << 8) | 0xB6, "HiSilicon Technologies" },
            { (8 << 8) | 0xB9, "Cognex" },
            { (8 << 8) | 0xBA, "Xinnova Technology Inc" },
            { (8 << 8) | 0xBC, "Concord Idea Corporation" },
            { (8 << 8) | 0xBF, "Ramsway" },
            { (8 << 8) | 0xC1, "Haotian Jinshibo Science Tech" },
            { (8 << 8) | 0xC2, "Being Advanced Memory" },
            { (8 << 8) | 0xC4, "Giantec Semiconductor Inc" },
            { (8 << 8) | 0xC7, "Kingcore" },
            { (8 << 8) | 0xC8, "Anucell Technology Holding" },
            { (8 << 8) | 0xCB, "Denso Corporation" },
            { (8 << 8) | 0xCD, "Qidan" },
            { (8 << 8) | 0xCE, "Mustang" },
            { (8 << 8) | 0xD0, "Passif Semiconductor" },
            { (8 << 8) | 0xD3, "Beckhoff Automation GmbH" },
            { (8 << 8) | 0xD5, "Air Computers SRL" },
            { (8 << 8) | 0xD6, "TMT Memory" },
            { (8 << 8) | 0xD9, "Netsol" },
            { (8 << 8) | 0xDA, "Bestdon Technology Co Ltd" },
            { (8 << 8) | 0xDC, "Uroad Technology Co Ltd" },
            { (8 << 8) | 0xDF, "Harman" },
            { (8 << 8) | 0xE0, "Berg Microelectronics Inc" },
            { (8 << 8) | 0xE3, "OCMEMORY" },
            { (8 << 8) | 0xE5, "Shark Gaming" },
            { (8 << 8) | 0xE6, "Avalanche Technology" },
            { (8 << 8) | 0xE9, "High Bridge Solutions Industria" },
            { (8 << 8) | 0xEA, "Transcend Technology Co Ltd" },
            { (8 << 8) | 0xEC, "Hon-Hai Precision" },
            { (8 << 8) | 0xEF, "Zentel Electronics Corporation" },
            { (8 << 8) | 0xF1, "Silicon Space Technology" },
            { (8 << 8) | 0xF2, "LITE-ON IT Corporation" },
            { (8 << 8) | 0xF4, "HMicro" },
            { (8 << 8) | 0xF7, "ACPI Digital Co Ltd" },
            { (8 << 8) | 0xF8, "Annapurna Labs" },
            { (8 << 8) | 0xFB, "Gowe Technology Co Ltd" },
            { (8 << 8) | 0xFD, "Positivo BGH" },
            { (8 << 8) | 0xFE, "Intelligence Silicon Technology" },
            { (9 << 8) | 0x01, "3D PLUS" },
            { (9 << 8) | 0x02, "Diehl Aerospace" },
            { (9 << 8) | 0x04, "Mercury Systems" },
            { (9 << 8) | 0x07, "Shenzhen Jinge Information Co Ltd" },
            { (9 << 8) | 0x08, "SCWW" },
            { (9 << 8) | 0x0B, "King Kong" },
            { (9 << 8) | 0x0D, "Gowin Semiconductor Corp" },
            { (9 << 8) | 0x0E, "Fremont Micro Devices Ltd" },
            { (9 << 8) | 0x10, "Exelis" },
            { (9 << 8) | 0x13, "Gloway International Co Ltd" },
            { (9 << 8) | 0x15, "Smart Energy Instruments" },
            { (9 << 8) | 0x16, "Approved Memory Corporation" },
            { (9 << 8) | 0x19, "Phytium" },
            { (9 << 8) | 0x1A, "UniIC Semiconductors Co Ltd" },
            { (9 << 8) | 0x1C, "eveRAM Technology Inc" },
            { (9 << 8) | 0x1F, "Shenzhen City Gcai Electronics" },
            { (9 << 8) | 0x20, "Stack Devices Corporation" },
            { (9 << 8) | 0x23, "HighX" },
            { (9 << 8) | 0x25, "XinKai/Silicon Kaiser" },
            { (9 << 8) | 0x26, "Google Inc" },
            { (9 << 8) | 0x29, "HIMA Paul Hildebrandt GmbH Co KG" },
            { (9 << 8) | 0x2A, "Keysight Technologies" },
            { (9 << 8) | 0x2C, "Ancore Technology Corporation" },
            { (9 << 8) | 0x2F, "Ikegami Tsushinki Co Ltd" },
            { (9 << 8) | 0x31, "Baikal Electronics" },
            { (9 << 8) | 0x32, "Nemostech Inc" },
            { (9 << 8) | 0x34, "Silicon Integrated Systems Corporation" },
            { (9 << 8) | 0x37, "Flash Chi" },
            { (9 << 8) | 0x38, "Jone" },
            { (9 << 8) | 0x3B, "Unimemory Technology(s) Pte Ltd" },
            { (9 << 8) | 0x3D, "Kuso" },
            { (9 << 8) | 0x3E, "Uniquify Inc" },
            { (9 << 8) | 0x40, "Core Chance Co Ltd" },
            { (9 << 8) | 0x43, "Hong Kong Gaia Group Co Limited" },
            { (9 << 8) | 0x45, "V2 Technologies" },
            { (9 << 8) | 0x46, "TLi" },
            { (9 << 8) | 0x49, "Shenzhen Zhongteng Electronic Corp Ltd" },
            { (9 << 8) | 0x4A, "Compound Photonics" },
            { (9 << 8) | 0x4C, "Shenzhen Pango Microsystems Co Ltd" },
            { (9 << 8) | 0x4F, "Eyenix Co Ltd" },
            { (9 << 8) | 0x51, "Accelerated Memory Production Inc" },
            { (9 << 8) | 0x52, "INVECAS Inc" },
            { (9 << 8) | 0x54, "Douqi Technology" },
            { (9 << 8) | 0x57, "Socionext Inc" },
            { (9 << 8) | 0x58, "HGST" },
            { (9 << 8) | 0x5B, "EpicGear" },
            { (9 << 8) | 0x5D, "Foxtronn International Corporation" },
            { (9 << 8) | 0x5E, "Bretelon Inc" },
            { (9 << 8) | 0x61, "MaxLinear Inc" },
            { (9 << 8) | 0x62, "ETA Devices" },
            { (9 << 8) | 0x64, "IMS Electronics Co Ltd" },
            { (9 << 8) | 0x67, "Shenzhen Mic Electronics Technolog" },
            { (9 << 8) | 0x68, "Boya Microelectronics Inc" },
            { (9 << 8) | 0x6B, "Kingred Electronic Technology Ltd" },
            { (9 << 8) | 0x6D, "Guangzhou Si Nuo Electronic" },
            { (9 << 8) | 0x6E, "Crocus Technology Inc" },
            { (9 << 8) | 0x70, "GE Aviation Systems LLC." },
            { (9 << 8) | 0x73, "TriCor Technologies" },
            { (9 << 8) | 0x75, "JUHOR" },
            { (9 << 8) | 0x76, "Zhuhai Douke Commerce Co Ltd" },
            { (9 << 8) | 0x79, "Realtek" },
            { (9 << 8) | 0x7A, "AltoBeam" },
            { (9 << 8) | 0x7C, "Beijing TrustNet Technology Co Ltd" },
            { (9 << 8) | 0x83, "Fairchild" },
            { (9 << 8) | 0x85, "Sonics Inc" },
            { (9 << 8) | 0x86, "Emerson Automation Solutions" },
            { (9 << 8) | 0x89, "Silicon Motion Inc" },
            { (9 << 8) | 0x8A, "Anurag" },
            { (9 << 8) | 0x8C, "FROM30 Co Ltd" },
            { (9 << 8) | 0x8F, "Ericsson Modems" },
            { (9 << 8) | 0x91, "Satixfy Ltd" },
            { (9 << 8) | 0x92, "Galaxy Microsystems Ltd" },
            { (9 << 8) | 0x94, "Lab" },
            { (9 << 8) | 0x97, "Axell Corporation" },
            { (9 << 8) | 0x98, "Essencore Limited" },
            { (9 << 8) | 0x9B, "Ambiq Micro" },
            { (9 << 8) | 0x9D, "Infomax" },
            { (9 << 8) | 0x9E, "Butterfly Network Inc" },
            { (9 << 8) | 0xA1, "ADK Media Group" },
            { (9 << 8) | 0xA2, "TSP Global Co Ltd" },
            { (9 << 8) | 0xA4, "Shenzhen Elicks Technology" },
            { (9 << 8) | 0xA7, "Dasima International Development" },
            { (9 << 8) | 0xA8, "Leahkinn Technology Limited" },
            { (9 << 8) | 0xAB, "Techcomp International (Fastable)" },
            { (9 << 8) | 0xAD, "Nuvoton" },
            { (9 << 8) | 0xAE, "Korea Uhbele International Group Ltd" },
            { (9 << 8) | 0xB0, "RelChip Inc" },
            { (9 << 8) | 0xB3, "Memorysolution GmbH" },
            { (9 << 8) | 0xB5, "Xiede" },
            { (9 << 8) | 0xB6, "BRC" },
            { (9 << 8) | 0xB9, "GCT Semiconductor Inc" },
            { (9 << 8) | 0xBA, "Hong Kong Zetta Device Technology" },
            { (9 << 8) | 0xBC, "Cuso" },
            { (9 << 8) | 0xBF, "Skymedi Corporation" },
            { (9 << 8) | 0xC1, "Tekism Co Ltd" },
            { (9 << 8) | 0xC2, "Seagate Technology PLC" },
            { (9 << 8) | 0xC4, "Gigacom Semiconductor LLC" },
            { (9 << 8) | 0xC7, "Neotion" },
            { (9 << 8) | 0xC8, "Lenovo" },
            { (9 << 8) | 0xCB, "in2H2 inc" },
            { (9 << 8) | 0xCD, "Vasekey" },
            { (9 << 8) | 0xCE, "Cal-Comp Industria de" },
            { (9 << 8) | 0xD0, "Heoriady" },
            { (9 << 8) | 0xD3, "AP Memory" },
            { (9 << 8) | 0xD5, "Etron Technology Inc" },
            { (9 << 8) | 0xD6, "Indie Semiconductor" },
            { (9 << 8) | 0xD9, "EVGA" },
            { (9 << 8) | 0xDA, "Audience Inc" },
            { (9 << 8) | 0xDC, "Vitesse Enterprise Co" },
            { (9 << 8) | 0xDF, "Graphcore" },
            { (9 << 8) | 0xE0, "Eoplex Inc" },
            { (9 << 8) | 0xE3, "LOKI" },
            { (9 << 8) | 0xE5, "Dosilicon Co Ltd" },
            { (9 << 8) | 0xE6, "Dolphin Integration" },
            { (9 << 8) | 0xE9, "Geniachip (Roche)" },
            { (9 << 8) | 0xEA, "Axign" },
            { (9 << 8) | 0xEC, "Chao Yue Zhuo Computer Business Dept." },
            { (9 << 8) | 0xEF, "Creative Chips GmbH" },
            { (9 << 8) | 0xF1, "Asgard" },
            { (9 << 8) | 0xF2, "Good Wealth Technology Ltd" },
            { (9 << 8) | 0xF4, "Nova-Systems GmbH" },
            { (9 << 8) | 0xF7, "DSL Memory" },
            { (9 << 8) | 0xF8, "Anvo-Systems Dresden GmbH" },
            { (9 << 8) | 0xFB, "Wave Computing" },
            { (9 << 8) | 0xFD, "Innovium Inc" },
            { (9 << 8) | 0xFE, "Starsway Technology Limited" },
            { (10 << 8) | 0x01, "Weltronics Co LTD" },
            { (10 << 8) | 0x02, "VMware Inc" },
            { (10 << 8) | 0x04, "INTENSO" },
            { (10 << 8) | 0x07, "MSC Technologies GmbH" },
            { (10 << 8) | 0x08, "Txrui" },
            { (10 << 8) | 0x0B, "XTX Technology Limited" },
            { (10 << 8) | 0x0D, "Shenzhen Yong Sheng Technology" },
            { (10 << 8) | 0x0E, "SNOAMOO (Shenzhen Kai Zhuo Yue)" },
            { (10 << 8) | 0x10, "Shenzhen XinRuiYan Electronics" },
            { (10 << 8) | 0x13, "Raspberry Pi Trading Ltd" },
            { (10 << 8) | 0x15, "Silicon Mobility" },
            { (10 << 8) | 0x16, "IQ-Analog Corporation" },
            { (10 << 8) | 0x19, "DEPO Computers" },
            { (10 << 8) | 0x1A, "Nespeed Sysems" },
            { (10 << 8) | 0x1C, "MemxPro Inc" },
            { (10 << 8) | 0x20, "XMC" },
            { (10 << 8) | 0x23, "Haiguang Integrated Circuit Design" },
            { (10 << 8) | 0x25, "Phison Electronics Corporation" },
            { (10 << 8) | 0x26, "Guizhou Huaxintong Semi-Conductor" },
            { (10 << 8) | 0x29, "Guangzhou Huayan Suning Electronic" },
            { (10 << 8) | 0x2A, "Guangzhou Zhouji Electronic Co Ltd" },
            { (10 << 8) | 0x2C, "Shenzhen Yilong Innovative Co Ltd" },
            { (10 << 8) | 0x2F, "Shanghai Kuxin Microelectronics Ltd" },
            { (10 << 8) | 0x31, "Qbit Semiconductor Ltd" },
            { (10 << 8) | 0x32, "Insignis Technology Corporation" },
            { (10 << 8) | 0x34, "Shenzhen Superway Electronics Co Ltd" },
            { (10 << 8) | 0x37, "Shenzhen City Parker Baking Electronics" },
            { (10 << 8) | 0x38, "Shenzhen Baihong Technology Co Ltd" },
            { (10 << 8) | 0x3B, "Artery Technology Co Ltd" },
            { (10 << 8) | 0x3D, "ShenzhenYing Chi Technology Development" },
            { (10 << 8) | 0x3E, "Shenzhen Pengcheng Xin Technology" },
            { (10 << 8) | 0x40, "Mythic Inc" },
            { (10 << 8) | 0x43, "Shenzhen Winconway Technology" },
            { (10 << 8) | 0x45, "Gold Key Technology Co Ltd" },
            { (10 << 8) | 0x46, "Habana Labs Ltd" },
            { (10 << 8) | 0x49, "OM Nanotech Pvt. Ltd" },
            { (10 << 8) | 0x4A, "Shenzhen Zhifeng Weiye Technology" },
            { (10 << 8) | 0x4C, "Guangzhou Zhong Hao Tian Electronic" },
            { (10 << 8) | 0x4F, "Puya Semiconductor (Shenzhen)" },
            { (10 << 8) | 0x51, "Antec Memory" },
            { (10 << 8) | 0x52, "Cortus SAS" },
            { (10 << 8) | 0x54, "MyWo AS" },
            { (10 << 8) | 0x57, "Heidelberg University" },
            { (10 << 8) | 0x58, "Flexxon PTE Ltd" },
            { (10 << 8) | 0x5B, "Aquarius Production Company LLC" },
            { (10 << 8) | 0x5D, "Intelimem" },
            { (10 << 8) | 0x5E, "Zbit Semiconductor Inc" },
            { (10 << 8) | 0x61, "Shenzen Recadata Storage Technology" },
            { (10 << 8) | 0x62, "Hyundai Technology" },
            { (10 << 8) | 0x64, "Aixi Technology" },
            { (10 << 8) | 0x67, "Jinshen" },
            { (10 << 8) | 0x68, "Kimtigo Semiconductor (HK) Limited" },
            { (10 << 8) | 0x6B, "Hefei Core Storage Electronic Limited" },
            { (10 << 8) | 0x6D, "Visenta (Xiamen) Technology Co Ltd" },
            { (10 << 8) | 0x6E, "Roa Logic BV" },
            { (10 << 8) | 0x70, "Hong Kong Hyunion Electronics" },
            { (10 << 8) | 0x73, "Terabyte Co Ltd" },
            { (10 << 8) | 0x75, "EXCELERAM" },
            { (10 << 8) | 0x76, "PsiKick" },
            { (10 << 8) | 0x79, "Jiangsu Huacun Electronic Technology" },
            { (10 << 8) | 0x7A, "Shenzhen Micro Innovation Industry" },
            { (10 << 8) | 0x7C, "XZN Storage Technology" },
            { (10 << 8) | 0x83, "Hewlett Packard Enterprise" },
            { (10 << 8) | 0x85, "Puya Semiconductor" },
            { (10 << 8) | 0x86, "MEMORFI" },
            { (10 << 8) | 0x89, "SiFive Inc" },
            { (10 << 8) | 0x8A, "Spreadtrum Communications" },
            { (10 << 8) | 0x8C, "UMAX Technology" },
            { (10 << 8) | 0x8F, "Daten Tecnologia LTDA" },
            { (10 << 8) | 0x91, "Eta Compute" },
            { (10 << 8) | 0x92, "Energous" },
            { (10 << 8) | 0x94, "Shenzhen Chixingzhe Tech Co Ltd" },
            { (10 << 8) | 0x97, "Uhnder Inc" },
            { (10 << 8) | 0x98, "Impinj" },
            { (10 << 8) | 0x9B, "Yangtze Memory Technologies Co Ltd" },
            { (10 << 8) | 0x9D, "Tammuz Co Ltd" },
            { (10 << 8) | 0x9E, "Allwinner Technology" },
            { (10 << 8) | 0xA1, "Teclast" },
            { (10 << 8) | 0xA2, "Maxsun" },
            { (10 << 8) | 0xA4, "RamCENTER Technology" },
            { (10 << 8) | 0xA7, "Network Intelligence" },
            { (10 << 8) | 0xA8, "Continental Technology (Holdings)" },
            { (10 << 8) | 0xAB, "Shenzhen Giant Hui Kang Tech Co Ltd" },
            { (10 << 8) | 0xAD, "Neo Forza" },
            { (10 << 8) | 0xAE, "Lyontek Inc" },
            { (10 << 8) | 0xB0, "Shenzhen Larix Technology Co Ltd" },
            { (10 << 8) | 0xB3, "Lanson Memory Co Ltd" },
            { (10 << 8) | 0xB5, "Canaan-Creative Co Ltd" },
            { (10 << 8) | 0xB6, "Black Diamond Memory" },
            { (10 << 8) | 0xB9, "GEO Semiconductors" },
            { (10 << 8) | 0xBA, "OCPC" },
            { (10 << 8) | 0xBC, "Jinyu" },
            { (10 << 8) | 0xBF, "Pegasus Semiconductor (Shanghai) Co" },
            { (10 << 8) | 0xC1, "Elmos Semiconductor AG" },
            { (10 << 8) | 0xC2, "Kllisre" },
            { (10 << 8) | 0xC4, "Shenzhen Xingmem Technology Corp" },
            { (10 << 8) | 0xC7, "Hoodisk Electronics Co Ltd" },
            { (10 << 8) | 0xC8, "SemsoTai (SZ) Technology Co Ltd" },
            { (10 << 8) | 0xCB, "Xinshirui (Shenzhen) Electronics Co" },
            { (10 << 8) | 0xCD, "Shenzhen Longsys Electronics Co Ltd" },
            { (10 << 8) | 0xCE, "Deciso B.V." },
            { (10 << 8) | 0xD0, "Shenzhen Veineda Technology Co Ltd" },
            { (10 << 8) | 0xD3, "Dust Leopard" },
            { (10 << 8) | 0xD5, "J&A Information Inc" },
            { (10 << 8) | 0xD6, "Shenzhen JIEPEI Technology Co Ltd" },
            { (10 << 8) | 0xD9, "Wiliot" },
            { (10 << 8) | 0xDA, "Raysun Electronics International Ltd" },
            { (10 << 8) | 0xDC, "MACNICA DHW LTDA" },
            { (10 << 8) | 0xDF, "Shenzhen Technology Co Ltd" },
            { (10 << 8) | 0xE0, "Signalchip" },
            { (10 << 8) | 0xE3, "Shanghai Fudi Investment Development" },
            { (10 << 8) | 0xE5, "Tecon MT" },
            { (10 << 8) | 0xE6, "Onda Electric Co Ltd" },
            { (10 << 8) | 0xE9, "IIT Madras" },
            { (10 << 8) | 0xEA, "Shenshan (Shenzhen) Electronic" },
            { (10 << 8) | 0xEC, "Colorful Technology Ltd" },
            { (10 << 8) | 0xEF, "NSITEXE Inc" },
            { (10 << 8) | 0xF1, "ASK Technology Group Limited" },
            { (10 << 8) | 0xF2, "GIGA-BYTE Technology Co Ltd" },
            { (10 << 8) | 0xF4, "Hyundai Inc" },
            { (10 << 8) | 0xF7, "Netac Technology Co Ltd" },
            { (10 << 8) | 0xF8, "PCCOOLER" },
            { (10 << 8) | 0xFB, "Beijing Tongfang Microelectronics Co" },
            { (10 << 8) | 0xFD, "ChipCraft Sp. z.o.o." },
            { (10 << 8) | 0xFE, "ALLFLASH Technology Limited" },
            { (11 << 8) | 0x01, "Foerd Technology Co Ltd" },
            { (11 << 8) | 0x02, "KingSpec" },
            { (11 << 8) | 0x04, "SL Link Co Ltd" },
            { (11 << 8) | 0x07, "Kyokuto Electronic Inc" },
            { (11 << 8) | 0x08, "Warrior Technology" },
            { (11 << 8) | 0x0B, "Shenzhen Futian District Bo Yueda Elec" },
            { (11 << 8) | 0x0D, "Shenzhen LianTeng Electronics Co Ltd" },
            { (11 << 8) | 0x0E, "AITC Memory" },
            { (11 << 8) | 0x10, "Shenzhen Huafeng Science Technology" },
            { (11 << 8) | 0x13, "SambaNova Systems" },
            { (11 << 8) | 0x15, "Jump Trading" },
            { (11 << 8) | 0x16, "Ampere Computing" },
            { (11 << 8) | 0x19, "Tri-Tech International" },
            { (11 << 8) | 0x1A, "Silicon Intergrated Systems Corporation" },
            { (11 << 8) | 0x1C, "Plexton Holdings Limited" },
            { (11 << 8) | 0x1F, "Axia Memory Technology" },
            { (11 << 8) | 0x20, "Chipset Technology Holding Limited" },
            { (11 << 8) | 0x23, "Guangzhou MiaoYuanJi Technology" },
            { (11 << 8) | 0x25, "Shenzhen Qianhai Weishengda" },
            { (11 << 8) | 0x26, "Guangzhou Guang Xie Cheng Trading" },
            { (11 << 8) | 0x29, "UltraMemory Inc" },
            { (11 << 8) | 0x2A, "New Coastline Global Tech Industry Co" },
            { (11 << 8) | 0x2C, "Diamond" },
            { (11 << 8) | 0x2F, "Ming Xin Limited" },
            { (11 << 8) | 0x31, "Biwin Semiconductor (HK) Co Ltd" },
            { (11 << 8) | 0x32, "UD INFO Corporation" },
            { (11 << 8) | 0x34, "Xiamen Kingblaze Technology Co Ltd" },
            { (11 << 8) | 0x37, "Wuhan Xun Zhan Electronic Technology" },
            { (11 << 8) | 0x38, "Shenzhen Ingacom Semiconductor Ltd" },
            { (11 << 8) | 0x3B, "Shenzhen Farasia Science Technology" },
            { (11 << 8) | 0x3D, "Hua Nan San Xian Technology Co Ltd" },
            { (11 << 8) | 0x3E, "Goldtech Electronics Co Ltd" },
            { (11 << 8) | 0x40, "Shenzhen Zhongguang Yunhe Trading" },
            { (11 << 8) | 0x43, "Shenzhen O’Yang Maile Technology Ltd" },
            { (11 << 8) | 0x45, "Chun Well Technology Holding Limited" },
            { (11 << 8) | 0x46, "Astera Labs Inc" },
            { (11 << 8) | 0x49, "Chengdu Fengcai Electronic Technology" },
            { (11 << 8) | 0x4A, "The Boeing Company" },
            { (11 << 8) | 0x4C, "Ramonster Technology Co Ltd" },
            { (11 << 8) | 0x4F, "Yourlyon" },
            { (11 << 8) | 0x51, "Shenzhen Yikesheng Technology Co Ltd" },
            { (11 << 8) | 0x52, "NOR-MEM" },
            { (11 << 8) | 0x54, "Bitmain Technologies Inc." },
            { (11 << 8) | 0x57, "Guangzhou Siye Electronic Technology" },
            { (11 << 8) | 0x58, "Silergy" },
            { (11 << 8) | 0x5B, "Shenzhen King Power Electronics" },
            { (11 << 8) | 0x5D, "Shenzhen SKIHOTAR Semiconductor" },
            { (11 << 8) | 0x5E, "PulseRain Technology" },
            { (11 << 8) | 0x61, "Shenzhen Yze Technology Co Ltd" },
            { (11 << 8) | 0x62, "Shenzhen Jieshuo Electronic Commerce" },
            { (11 << 8) | 0x64, "Hua Wei Technology Co Ltd" },
            { (11 << 8) | 0x67, "Shenzhen Shi Bolunshuai Technology" },
            { (11 << 8) | 0x68, "Shanghai Ruixuan Information Tech" },
            { (11 << 8) | 0x6B, "Acer" },
            { (11 << 8) | 0x6D, "Gstar Semiconductor Co Ltd" },
            { (11 << 8) | 0x6E, "ShineDisk" },
            { (11 << 8) | 0x70, "UnionChip Semiconductor Co Ltd" },
            { (11 << 8) | 0x73, "MCLogic Inc" },
            { (11 << 8) | 0x75, "Arm Technology (China) Co Ltd" },
            { (11 << 8) | 0x76, "Lexar Co Limited" },
            { (11 << 8) | 0x79, "Hong Kong Hyunion Electronics Co Ltd" },
            { (11 << 8) | 0x7A, "Shenzhen Banghong Electronics Co Ltd" },
            { (11 << 8) | 0x7C, "Hex Five Security Inc" },
            { (11 << 8) | 0x83, "Codasip GmbH" },
            { (11 << 8) | 0x85, "Shenzhen Kefu Technology Co Limited" },
            { (11 << 8) | 0x86, "Shenzhen ZST Electronics Technology" },
            { (11 << 8) | 0x89, "TRINAMIC Motion Control GmbH & Co" },
            { (11 << 8) | 0x8A, "PixelDisplay Inc" },
            { (11 << 8) | 0x8C, "Richtek Power" },
            { (11 << 8) | 0x8F, "UNIC Memory Technology Co Ltd" },
            { (11 << 8) | 0x91, "CXMT" },
            { (11 << 8) | 0x92, "Guangzhou Xinyi Heng Computer" },
            { (11 << 8) | 0x94, "V-GEN" },
            { (11 << 8) | 0x97, "Shenzhen Zhongshi Technology Co Ltd" },
            { (11 << 8) | 0x98, "Shenzhen Zhongtian Bozhong Technology" },
            { (11 << 8) | 0x9B, "Shenzhen HongDingChen Information" },
            { (11 << 8) | 0x9D, "AMS (Jiangsu Advanced Memory Semi)" },
            { (11 << 8) | 0x9E, "Wuhan Jing Tian Interconnected Tech Co" },
            { (11 << 8) | 0xA1, "Shenzhen Xinshida Technology Co Ltd" },
            { (11 << 8) | 0xA2, "Shenzhen Chuangshifeida Technology" },
            { (11 << 8) | 0xA4, "ADVAN Inc" },
            { (11 << 8) | 0xA7, "StarRam International Co Ltd" },
            { (11 << 8) | 0xA8, "Shen Zhen XinShenHua Tech Co Ltd" },
            { (11 << 8) | 0xAB, "Sinker" },
            { (11 << 8) | 0xAD, "PUSKILL" },
            { (11 << 8) | 0xAE, "Guangzhou Hao Jia Ye Technology Co" },
            { (11 << 8) | 0xB0, "Barefoot Networks" },
            { (11 << 8) | 0xB3, "Trek Technology (S) PTE Ltd" },
            { (11 << 8) | 0xB5, "Shenzhen Lomica Technology Co Ltd" },
            { (11 << 8) | 0xB6, "Nuclei System Technology Co Ltd" },
            { (11 << 8) | 0xB9, "Zotac Technology Ltd" },
            { (11 << 8) | 0xBA, "Foxline" },
            { (11 << 8) | 0xBC, "Efinix Inc" },
            { (11 << 8) | 0xBF, "Shanghai Han Rong Microelectronics Co" },
            { (11 << 8) | 0xC1, "Smart Shine(QingDao) Microelectronics" },
            { (11 << 8) | 0xC2, "Thermaltake Technology Co Ltd" },
            { (11 << 8) | 0xC4, "UPMEM" },
            { (11 << 8) | 0xC7, "Winconway" },
            { (11 << 8) | 0xC8, "Advantech Co Ltd" },
            { (11 << 8) | 0xCB, "Blaize Inc" },
            { (11 << 8) | 0xCD, "Wuhan Naonongmai Technology Co Ltd" },
            { (11 << 8) | 0xCE, "Shenzhen Hui ShingTong Technology" },
            { (11 << 8) | 0xD0, "Fabu Technology" },
            { (11 << 8) | 0xD3, "Cervoz Co Ltd" },
            { (11 << 8) | 0xD5, "Facebook Inc" },
            { (11 << 8) | 0xD6, "Shenzhen Longsys Electronics Co Ltd" },
            { (11 << 8) | 0xD9, "Adamway" },
            { (11 << 8) | 0xDA, "PZG" },
            { (11 << 8) | 0xDC, "Guangzhou ZiaoFu Tranding Co Ltd" },
            { (11 << 8) | 0xDF, "Seeker Technology Limited" },
            { (11 << 8) | 0xE0, "Shenzhen OSCOO Tech Co Ltd" },
            { (11 << 8) | 0xE3, "Gazda" },
            { (11 << 8) | 0xE5, "Esperanto Technologies" },
            { (11 << 8) | 0xE6, "JinSheng Electronic (Shenzhen) Co Ltd" },
            { (11 << 8) | 0xE9, "Fraunhofer IIS" },
            { (11 << 8) | 0xEA, "Kandou Bus SA" },
            { (11 << 8) | 0xEC, "Artmem Technology Co Ltd" },
            { (11 << 8) | 0xEF, "Shenzhen CHN Technology Co Ltd" },
            { (11 << 8) | 0xF1, "Tanbassh" },
            { (11 << 8) | 0xF2, "Shenzhen Tianyu Jieyun Intl Logistics" },
            { (11 << 8) | 0xF4, "Eorex Corporation" },
            { (11 << 8) | 0xF7, "QinetiQ Group plc" },
            { (11 << 8) | 0xF8, "Exascend" },
            { (11 << 8) | 0xFB, "MBit Wireless Inc" },
            { (11 << 8) | 0xFD, "ShenZhen Juhor Precision Tech Co Ltd" },
            { (11 << 8) | 0xFE, "Shenzhen Reeinno Technology Co Ltd" },
            { (12 << 8) | 0x01, "ABIT Electronics (Shenzhen) Co Ltd" },
            { (12 << 8) | 0x02, "Semidrive" },
            { (12 << 8) | 0x04, "Wxilicon Technology Co Ltd" },
            { (12 << 8) | 0x07, "LiSion Technologies Inc" },
            { (12 << 8) | 0x08, "Power Active Co Ltd" },
            { (12 << 8) | 0x0B, "Shenzhen Chuangshifeida Technology" },
            { (12 << 8) | 0x0D, "Jiangsu Xinsheng Intelligent Technology" },
            { (12 << 8) | 0x0E, "MLOONG" },
            { (12 << 8) | 0x10, "Anpec Electronics" },
            { (12 << 8) | 0x13, "ITRenew Inc" },
            { (12 << 8) | 0x15, "Jazer" },
            { (12 << 8) | 0x16, "Xiamen Semiconductor Investment Group" },
            { (12 << 8) | 0x19, "Allegro Microsystems LLC" },
            { (12 << 8) | 0x1A, "Hunan RunCore Innovation Technology" },
            { (12 << 8) | 0x1C, "Zhuhai Chuangfeixin Technology Co Ltd" },
            { (12 << 8) | 0x1F, "Shenzhen Pengxiong Technology Co Ltd" },
            { (12 << 8) | 0x20, "Dongguan Yingbang Commercial Trading Co" },
            { (12 << 8) | 0x23, "Apex Microelectronics Co Ltd" },
            { (12 << 8) | 0x25, "Ling Rui Technology (Shenzhen) Co Ltd" },
            { (12 << 8) | 0x26, "Hongkong Hyunion Electronics Co Ltd" },
            { (12 << 8) | 0x29, "Dongguan Crown Code Electronic Commerce" },
            { (12 << 8) | 0x2A, "Monolithic Power Systems Inc" },
            { (12 << 8) | 0x2C, "Hangzhou Hikstorage Technology Co" },
            { (12 << 8) | 0x2F, "Hefei Konsemi Storage Technology Co Ltd" },
            { (12 << 8) | 0x31, "DSIN" },
            { (12 << 8) | 0x32, "Blu Wireless Technology" },
            { (12 << 8) | 0x34, "Acacia Communications" },
            { (12 << 8) | 0x37, "C-SKY Microsystems Co Ltd (XuanTie)" },
            { (12 << 8) | 0x38, "Shenzhen Hystou Technology Co Ltd" },
            { (12 << 8) | 0x3B, "Qingdao Thunderobot Technology Co Ltd" },
            { (12 << 8) | 0x3D, "Shenzhen Envida Technology Co Ltd" },
            { (12 << 8) | 0x3E, "UDStore Solution Limited" },
            { (12 << 8) | 0x40, "Shenzhen Xin Hong Rui Tech Ltd" },
            { (12 << 8) | 0x43, "Xiamen Pengpai Microelectronics Co Ltd" },
            { (12 << 8) | 0x45, "Shenzhen WODPOSIT Technology Co" },
            { (12 << 8) | 0x46, "Unistar" },
            { (12 << 8) | 0x49, "Shenzhen SOVERECA Technology Co" },
            { (12 << 8) | 0x4A, "Dire Wolf" },
            { (12 << 8) | 0x4C, "CSI Halbleiter GmbH" },
            { (12 << 8) | 0x4F, "Shenzhen Chengyi Qingdian Electronic" },
            { (12 << 8) | 0x51, "Vayyar Imaging Ltd" },
            { (12 << 8) | 0x52, "Paisen Network Technology Co Ltd" },
            { (12 << 8) | 0x54, "Caplink Technology Limited" },
            { (12 << 8) | 0x57, "Shenzhen KingDisk Century Technology" },
            { (12 << 8) | 0x58, "SOYO" },
            { (12 << 8) | 0x5B, "Aril Computer Company" },
            { (12 << 8) | 0x5D, "Shenzhen Ruiyingtong Technology Co" },
            { (12 << 8) | 0x5E, "HANA Micron" },
            { (12 << 8) | 0x61, "Tesla Corporation" },
            { (12 << 8) | 0x62, "Pingtouge (Shanghai) Semiconductor Co" },
            { (12 << 8) | 0x64, "Integrated Silicon Solution Israel Ltd" },
            { (12 << 8) | 0x67, "Guangzhou Shuvrwine Technology Co" },
            { (12 << 8) | 0x68, "Shenzhen Hangshun Chip Technology" },
            { (12 << 8) | 0x6B, "Euronet Technology Inc" },
            { (12 << 8) | 0x6D, "Shenzhen Xinhongyusheng Electrical" },
            { (12 << 8) | 0x6E, "PICOCOM" },
            { (12 << 8) | 0x70, "VLSI Solution" },
            { (12 << 8) | 0x73, "Inspur Electronic Information Industry" },
            { (12 << 8) | 0x75, "Beijing Welldisk Electronics Co Ltd" },
            { (12 << 8) | 0x76, "Suzhou EP Semicon Co Ltd" },
            { (12 << 8) | 0x79, "Datotek International Co Ltd" },
            { (12 << 8) | 0x7A, "Telecom and Microelectronics Industries" },
            { (12 << 8) | 0x7C, "APEX-INFO" },
            { (12 << 8) | 0x83, "MyTek Electronics Corp" },
            { (12 << 8) | 0x85, "Shenzhen Meixin Electronics Ltd" },
            { (12 << 8) | 0x86, "Ghost Wolf" },
            { (12 << 8) | 0x89, "Pioneer High Fidelity Taiwan Co. Ltd" },
            { (12 << 8) | 0x8A, "LuoSilk" },
            { (12 << 8) | 0x8C, "Black Sesame Technologies Inc" },
            { (12 << 8) | 0x8F, "Quadratica LLC" },
            { (12 << 8) | 0x91, "Xi’an Morebeck Semiconductor Tech Co" },
            { (12 << 8) | 0x92, "Kingbank Technology Co Ltd" },
            { (12 << 8) | 0x94, "Shenzhen Eaget Innovation Tech Ltd" },
            { (12 << 8) | 0x97, "Guangzhou Longdao Network Tech Co" },
            { (12 << 8) | 0x98, "Shenzhen Futian SEC Electronic Market" },
            { (12 << 8) | 0x9B, "C-Corsa Technology" },
            { (12 << 8) | 0x9D, "Beijing InnoMem Technologies Co Ltd" },
            { (12 << 8) | 0x9E, "YooTin" },
            { (12 << 8) | 0xA1, "Shenzhen Ronisys Electronics Co Ltd" },
            { (12 << 8) | 0xA2, "Hongkong Xinlan Guangke Co Ltd" },
            { (12 << 8) | 0xA4, "Beijing Hongda Jinming Technology Co Ltd" },
            { (12 << 8) | 0xA7, "Starsystems Inc" },
            { (12 << 8) | 0xA8, "Shenzhen Yingjiaxun Industrial Co Ltd" },
            { (12 << 8) | 0xAB, "WuHan SenNaiBo E-Commerce Co Ltd" },
            { (12 << 8) | 0xAD, "Shenzhen Goodix Technology Co Ltd" },
            { (12 << 8) | 0xAE, "Aigo Electronic Technology Co Ltd" },
            { (12 << 8) | 0xB0, "Cactus Technologies Limited" },
            { (12 << 8) | 0xB3, "Nanjing UCUN Technology Inc" },
            { (12 << 8) | 0xB5, "Beijinjinshengyihe Technology Co Ltd" },
            { (12 << 8) | 0xB6, "Zyzyx" },
            { (12 << 8) | 0xB9, "Syzexion" },
            { (12 << 8) | 0xBA, "Kembona" },
            { (12 << 8) | 0xBC, "Morse Micro" },
            { (12 << 8) | 0xBF, "Shunlie" },
            { (12 << 8) | 0xC1, "Shenzhen Yze Technology Co Ltd" },
            { (12 << 8) | 0xC2, "Shenzhen Huang Pu He Xin Technology" },
            { (12 << 8) | 0xC4, "JISHUN" },
            { (12 << 8) | 0xC7, "UNICORE Electronic (Suzhou) Co Ltd" },
            { (12 << 8) | 0xC8, "Axonne Inc" },
            { (12 << 8) | 0xCB, "Whampoa Core Technology Co Ltd" },
            { (12 << 8) | 0xCD, "ONE Semiconductor" },
            { (12 << 8) | 0xCE, "SimpleMachines Inc" },
            { (12 << 8) | 0xD0, "Shenzhen Xinlianxin Network Technology" },
            { (12 << 8) | 0xD3, "Shenzhen Fengwensi Technology Co Ltd" },
            { (12 << 8) | 0xD5, "JJT Solution Co Ltd" },
            { (12 << 8) | 0xD6, "HOSIN Global Electronics Co Ltd" },
            { (12 << 8) | 0xD9, "DIT Technology Co Ltd" },
            { (12 << 8) | 0xDA, "iFound" },
            { (12 << 8) | 0xDC, "ASUS" },
            { (12 << 8) | 0xDF, "RANSOR" },
            { (12 << 8) | 0xE0, "Axiado Corporation" },
            { (12 << 8) | 0xE3, "S3Plus Technologies SA" },
            { (12 << 8) | 0xE5, "GreenWaves Technologies" },
            { (12 << 8) | 0xE6, "NUVIA Inc" },
            { (12 << 8) | 0xE9, "Chengboliwei Electronic Business" },
            { (12 << 8) | 0xEA, "Kowin Technology HK Limited" },
            { (12 << 8) | 0xEC, "SCY" },
            { (12 << 8) | 0xEF, "Shenzhen Toooogo Memory Technology" },
            { (12 << 8) | 0xF1, "Costar Electronics Inc" },
            { (12 << 8) | 0xF2, "Shenzhen Huatop Technology Co Ltd" },
            { (12 << 8) | 0xF4, "Shenzhen Boyuan Computer Technology" },
            { (12 << 8) | 0xF7, "Zhejiang Dahua Memory Technology" },
            { (12 << 8) | 0xF8, "Virtu Financial" },
            { (12 << 8) | 0xFB, "Echow Technology Ltd" },
            { (12 << 8) | 0xFD, "Yingpark" },
            { (12 << 8) | 0xFE, "Shenzhen Bigway Tech Co Ltd" },
            { (13 << 8) | 0x01, "Beijing Haawking Technology Co Ltd" },
            { (13 << 8) | 0x02, "Open HW Group" },
            { (13 << 8) | 0x04, "ncoder AG" },
            { (13 << 8) | 0x07, "Biao Ram Technology Co Ltd" },
            { (13 << 8) | 0x08, "Shenzhen Kaizhuoyue Electronics Co Ltd" },
            { (13 << 8) | 0x0B, "Wink Semiconductor (Shenzhen) Co Ltd" },
            { (13 << 8) | 0x0D, "Palma Ceia SemiDesign" },
            { (13 << 8) | 0x0E, "EM Microelectronic-Marin SA" },
            { (13 << 8) | 0x10, "Reliance Memory Inc" },
            { (13 << 8) | 0x13, "Shenzhen Sati Smart Technology Co Ltd" },
            { (13 << 8) | 0x15, "Lifelong" },
            { (13 << 8) | 0x16, "Beijing Oitech Technology Co Ltd" },
            { (13 << 8) | 0x19, "swordbill" },
            { (13 << 8) | 0x1A, "YIREN" },
            { (13 << 8) | 0x1C, "PoweV Electronic Technology Co Ltd" },
            { (13 << 8) | 0x1F, "Ventana Micro Systems" },
            { (13 << 8) | 0x20, "Hefei Guangxin Microelectronics Co Ltd" },
            { (13 << 8) | 0x23, "Tangem AG" },
            { (13 << 8) | 0x25, "RC Module" },
            { (13 << 8) | 0x26, "Timetec International Inc" },
            { (13 << 8) | 0x29, "Guangzhou Taisupanke Computer Equipment" },
            { (13 << 8) | 0x2A, "Ceremorphic Inc" },
            { (13 << 8) | 0x2C, "Beijing ESWIN Computing Technology" },
            { (13 << 8) | 0x2F, "Unisoc" },
            { (13 << 8) | 0x31, "GUANCUN" },
            { (13 << 8) | 0x32, "IPASON" },
            { (13 << 8) | 0x34, "Amazon" },
            { (13 << 8) | 0x37, "Ubilite Inc" },
            { (13 << 8) | 0x38, "Shenzhen Quanxing Technology Co Ltd" },
            { (13 << 8) | 0x3B, "Shenzhen RuiRen Technology Co Ltd" },
            { (13 << 8) | 0x3D, "RWA (Hong Kong) Ltd" },
            { (13 << 8) | 0x3E, "Genesys Logic Inc" },
            { (13 << 8) | 0x40, "Biostar Microtech International Corp" },
            { (13 << 8) | 0x43, "Zhixin Semicoducotor Co Ltd" },
            { (13 << 8) | 0x45, "Aigo Data Security Technology Co. Ltd" },
            { (13 << 8) | 0x46, ".GXore Technologies" },
            { (13 << 8) | 0x49, "PRIME" },
            { (13 << 8) | 0x4A, "Shenzhen Juyang Innovative Technology" },
            { (13 << 8) | 0x4C, "SiEngine Technology Co., Ltd." },
            { (13 << 8) | 0x4F, "Credo Technology Group Ltd" },
            { (13 << 8) | 0x51, "Nucleu Semiconductor" },
            { (13 << 8) | 0x52, "Shenzhen Guangshuo Electronics Co Ltd" },
            { (13 << 8) | 0x54, "Suzhou Mainshine Electronic Co Ltd." },
            { (13 << 8) | 0x57, "ROG" },
            { (13 << 8) | 0x58, "Perceive" },
            { (13 << 8) | 0x5B, "Shenzhen Daxinlang Electronic Tech Co" },
            { (13 << 8) | 0x5D, "OLOy Technology" },
            { (13 << 8) | 0x5E, "Wuhan P&S Semiconductor Co Ltd" },
            { (13 << 8) | 0x61, "Rochester Electronics" },
            { (13 << 8) | 0x62, "Wuxi Smart Memories Technologies Co" },
            { (13 << 8) | 0x64, "Agile Memory Technology Co Ltd" },
            { (13 << 8) | 0x67, "Dongguan Guanma e-commerce Co Ltd" },
            { (13 << 8) | 0x68, "Rayson Hi-Tech (SZ) Limited" },
            { (13 << 8) | 0x6B, "Shenzhen Cwinner Technology Co Ltd" },
            { (13 << 8) | 0x6D, "Shenzhen Suhuicun Technology Co Ltd" },
            { (13 << 8) | 0x6E, "Vickter Electronics Co. Ltd." },
            { (13 << 8) | 0x70, "EXEGate FZE" },
            { (13 << 8) | 0x73, "Starsway" },
            { (13 << 8) | 0x75, "AirDisk" },
            { (13 << 8) | 0x76, "Shenzhen Speedmobile Technology Co" },
            { (13 << 8) | 0x79, "Shangxin Technology Co Ltd" },
            { (13 << 8) | 0x7A, "Shanghai Zhaoxin Semiconductor Co" },
            { (13 << 8) | 0x7C, "Hangzhou Hikstorage Technology Co" },
            { (13 << 8) | 0x83, "JHICC" },
            { (13 << 8) | 0x85, "ThinkTech Information Technology Co" },
            { (13 << 8) | 0x86, "Shenzhen Chixingzhe Technology Co Ltd" },
            { (13 << 8) | 0x89, "Shenzhen YC Storage Technology Co Ltd" },
            { (13 << 8) | 0x8A, "Shenzhen Chixingzhe Technology Co" },
            { (13 << 8) | 0x8C, "AISTOR" },
            { (13 << 8) | 0x8F, "Shenzhen Monarch Memory Technology" },
            { (13 << 8) | 0x91, "Jesis" },
            { (13 << 8) | 0x92, "Espressif Systems (Shanghai) Co Ltd" },
            { (13 << 8) | 0x94, "NeuMem Co Ltd" },
            { (13 << 8) | 0x97, "Groupe LDLC" },
            { (13 << 8) | 0x98, "Semidynamics Technology Services SLU" },
            { (13 << 8) | 0x9B, "Shenzhen Yinxiang Technology Co Ltd" },
            { (13 << 8) | 0x9D, "LEORICE" },
            { (13 << 8) | 0x9E, "Waymo LLC" },
            { (13 << 8) | 0xA1, "Shenzhen Sooner Industrial Co Ltd" },
            { (13 << 8) | 0xA2, "Horizon Robotics" },
            { (13 << 8) | 0xA4, "FuturePath Technology (Shenzhen) Co" },
            { (13 << 8) | 0xA7, "ICMAX Technologies Co Limited" },
            { (13 << 8) | 0xA8, "Lynxi Technologies Ltd Co" },
            { (13 << 8) | 0xAB, "Biwin Storage Technology Co Ltd" },
            { (13 << 8) | 0xAD, "WeForce Co Ltd" },
            { (13 << 8) | 0xAE, "Shenzhen Fanxiang Information Technology" },
            { (13 << 8) | 0xB0, "YingChu" },
            { (13 << 8) | 0xB3, "Ayar Labs" },
            { (13 << 8) | 0xB5, "Shenzhen Xinxinshun Technology Co" },
            { (13 << 8) | 0xB6, "Galois Inc" },
            { (13 << 8) | 0xB9, "Group RZX Technology LTDA" },
            { (13 << 8) | 0xBA, "Yottac Technology (XI’AN) Cooperation" },
            { (13 << 8) | 0xBC, "Group Star Technology Co Ltd" },
            { (13 << 8) | 0xBF, "T3 Robotics Inc." },
            { (13 << 8) | 0xC1, "Shenzhen SXmicro Technology Co Ltd" },
            { (13 << 8) | 0xC2, "Shanghai Yili Computer Technology Co" },
            { (13 << 8) | 0xC4, "uFound" },
            { (13 << 8) | 0xC7, "Shenzhen Pradeon Intelligent Technology" },
            { (13 << 8) | 0xC8, "Power LSI" },
            { (13 << 8) | 0xCB, "CERVO" },
            { (13 << 8) | 0xCD, "Beijing Unigroup Tsingteng MicroSystem" },
            { (13 << 8) | 0xCE, "Brainsao GmbH" },
            { (13 << 8) | 0xD0, "Shanghai Biren Technology Co Ltd" },
            { (13 << 8) | 0xD3, "ZhongsihangTechnology Co Ltd" },
            { (13 << 8) | 0xD5, "Guangzhou Riss Electronic Technology" },
            { (13 << 8) | 0xD6, "Shenzhen Cloud Security Storage Co" },
            { (13 << 8) | 0xD9, "e-peas" },
            { (13 << 8) | 0xDA, "Fraunhofer IPMS" },
            { (13 << 8) | 0xDC, "Abacus Peripherals Private Limited" },
            { (13 << 8) | 0xDF, "Sitrus Technology" },
            { (13 << 8) | 0xE0, "AnHui Conner Storage Co Ltd" },
            { (13 << 8) | 0xE3, "Star Memory" },
            { (13 << 8) | 0xE5, "MEJEC" },
            { (13 << 8) | 0xE6, "Rockchip Electronics Co Ltd" },
            { (13 << 8) | 0xE9, "MINRES Technologies GmbH" },
            { (13 << 8) | 0xEA, "Himax Technologies Inc" },
            { (13 << 8) | 0xEC, "Tecmiyo" },
            { (13 << 8) | 0xEF, "lowRISC" },
            { (13 << 8) | 0xF1, "Shenzhen 9 Chapter Technologies Co" },
            { (13 << 8) | 0xF2, "Addlink" },
            { (13 << 8) | 0xF4, "Pensando Systems Inc." },
            { (13 << 8) | 0xF7, "PEZY Computing" },
            { (13 << 8) | 0xF8, "Extreme Engineering Solutions Inc" },
            { (13 << 8) | 0xFB, "Xsight Labs Ltd" },
            { (13 << 8) | 0xFD, "Dell Technologies" },
            { (13 << 8) | 0xFE, "Guangdong StarFive Technology Co" },
            { (14 << 8) | 0x01, "TECOTON" },
            { (14 << 8) | 0x02, "Abko Co Ltd" },
            { (14 << 8) | 0x04, "Shenzhen Sunhome Electronics Co Ltd" },
            { (14 << 8) | 0x07, "Shenzhen Cooyes Technology Co Ltd" },
            { (14 << 8) | 0x08, "ShenZhen ChaoYing ZhiNeng Technology" },
            { (14 << 8) | 0x0B, "Shenzhen Quanji Technology Co Ltd" },
            { (14 << 8) | 0x0D, "Maxell Corporation of America" },
            { (14 << 8) | 0x0E, "Shenshen Xinxintao Electronics Co Ltd" },
            { (14 << 8) | 0x10, "Groq Inc" },
            { (14 << 8) | 0x13, "All Bit Semiconductor" },
            { (14 << 8) | 0x15, "Shenzhen Sipeed Technology Co Ltd" },
            { (14 << 8) | 0x16, "Linzhi Hong Kong Co Limited" },
            { (14 << 8) | 0x19, "Hefei Laiku Technology Co Ltd" },
            { (14 << 8) | 0x1A, "Zord" },
            { (14 << 8) | 0x1C, "Regent Sharp International Limited" },
            { (14 << 8) | 0x1F, "Base Creation International Limited" },
            { (14 << 8) | 0x20, "Shenzhen Zhixin Chuanglian Technology" },
            { (14 << 8) | 0x23, "Union Memory" },
            { (14 << 8) | 0x25, "Ingenic Semiconductor Co Ltd" },
            { (14 << 8) | 0x26, "SiPearl" },
            { (14 << 8) | 0x29, "Shenzhen Sunny Technology Co Ltd" },
            { (14 << 8) | 0x2A, "Cott Electronics Ltd" },
            { (14 << 8) | 0x2C, "Shenzhen Jintang Fuming Optoelectronics" },
            { (14 << 8) | 0x2F, "Ehiway Microelectronic Science Tech Co" },
            { (14 << 8) | 0x31, "GDRAMARS" },
            { (14 << 8) | 0x32, "Meminsights Technology" },
            { (14 << 8) | 0x34, "Luminous Computing Inc" },
            { (14 << 8) | 0x37, "ORICO Technologies Co. Ltd." },
            { (14 << 8) | 0x38, "Space Exploration Technologies Corp" },
            { (14 << 8) | 0x3B, "Syntacore Ltd" },
            { (14 << 8) | 0x3D, "ONiO As" },
            { (14 << 8) | 0x3E, "Shenzhen Peladn Technology Co Ltd" },
            { (14 << 8) | 0x40, "ASTC" },
            { (14 << 8) | 0x43, "Sinh Micro Co Ltd" },
            { (14 << 8) | 0x45, "Aeva Inc" },
            { (14 << 8) | 0x46, "HongKong Hyunion Electronics Co Ltd" },
            { (14 << 8) | 0x49, "Idaho Scientific" },
            { (14 << 8) | 0x4A, "Suzhou SF Micro Electronics Co Ltd" },
            { (14 << 8) | 0x4C, "Fitipower Integrated Technology Co Ltd" },
            { (14 << 8) | 0x4F, "Rivos Inc" },
            { (14 << 8) | 0x51, "Wuhan YuXin Semiconductor Co Ltd" },
            { (14 << 8) | 0x52, "United Memory Technology (Jiangsu)" },
            { (14 << 8) | 0x54, "ArchiTek Corporation" },
            { (14 << 8) | 0x57, "Eggtronic Engineering Spa" },
            { (14 << 8) | 0x58, "Fusontai Technology" },
            { (14 << 8) | 0x5B, "Shenzhen Jiteng Network Technology Co" },
            { (14 << 8) | 0x5D, "Trilinear Technologies Inc" },
            { (14 << 8) | 0x5E, "Shenzhen Developer Microelectronics Co" },
            { (14 << 8) | 0x61, "Lyczar" },
            { (14 << 8) | 0x62, "QJTEK" },
            { (14 << 8) | 0x64, "Han Stor" },
            { (14 << 8) | 0x67, "Shanghai Ningyuan Electronic Technology" },
            { (14 << 8) | 0x68, "Auradine" },
            { (14 << 8) | 0x6B, "SiMa Technologies" },
            { (14 << 8) | 0x6D, "Suzhou Comay Information Co Ltd" },
            { (14 << 8) | 0x6E, "Yentek" },
            { (14 << 8) | 0x70, "Shenzhen Youzhi Computer Technology" },
            { (14 << 8) | 0x73, "Siliconwaves Technologies Co Ltd" },
            { (14 << 8) | 0x75, "Shenzhen Xinxinzhitao Electronics Business" },
            { (14 << 8) | 0x76, "Shenzhen HenQi Electronic Commerce Co" },
            { (14 << 8) | 0x79, "Shenzhen Dalu Semiconductor Technology" },
            { (14 << 8) | 0x7A, "Shenzhen Ninespeed Electronics Co Ltd" },
            { (14 << 8) | 0x7C, "Shenzhen Jaguar Microsystems Co Ltd" },
            { (14 << 8) | 0x83, "Shenzhen Feisrike Technology Co Ltd" },
            { (14 << 8) | 0x85, "Global Mixed-mode Technology Inc" },
            { (14 << 8) | 0x86, "Shenzhen Weien Electronics Co Ltd." },
            { (14 << 8) | 0x89, "E-Rockic Technology Company Limited" },
            { (14 << 8) | 0x8A, "Aerospace Science Memory Shenzhen" },
            { (14 << 8) | 0x8C, "Dukosi" },
            { (14 << 8) | 0x8F, "Zhuhai Sanxia Semiconductor Co Ltd" },
            { (14 << 8) | 0x91, "AstraTek" },
            { (14 << 8) | 0x92, "Shenzhen Xinyuze Technology Co Ltd" },
            { (14 << 8) | 0x94, "ACFlow" },
            { (14 << 8) | 0x97, "Supreme Wise Limited" },
            { (14 << 8) | 0x98, "Blue Cheetah Analog Design Inc" },
            { (14 << 8) | 0x9B, "SBO Hearing A/S" },
            { (14 << 8) | 0x9D, "Permanent Potential Limited" },
            { (14 << 8) | 0x9E, "Creative World International Limited" },
            { (14 << 8) | 0xA1, "Protected Logic Corporation" },
            { (14 << 8) | 0xA2, "Sabrent" },
            { (14 << 8) | 0xA4, "NEUCHIPS Corporation" },
            { (14 << 8) | 0xA7, "Shenzhen Actseno Information Technology" },
            { (14 << 8) | 0xA8, "RIVAI Technologies (Shenzhen) Co Ltd" },
            { (14 << 8) | 0xAB, "Shanghai Synsense Technologies Co Ltd" },
            { (14 << 8) | 0xAD, "CloudBEAR LLC" },
            { (14 << 8) | 0xAE, "Emzior, LLC" },
            { (14 << 8) | 0xB0, "UNIM Innovation Technology (Wu XI)" },
            { (14 << 8) | 0xB3, "Zhuzhou Hongda Electronics Corp Ltd" },
            { (14 << 8) | 0xB5, "PROXMEM" },
            { (14 << 8) | 0xB6, "Draper Labs" },
            { (14 << 8) | 0xB9, "AONDEVICES Inc" },
            { (14 << 8) | 0xBA, "Shenzhen Netforward Micro Electronic" },
            { (14 << 8) | 0xBC, "Shenzhen Secmem Microelectronics Co" },
            { (14 << 8) | 0xBF, "O-Cubes Shanghai Microelectronics" },
            { (14 << 8) | 0xC1, "UMIS" },
            { (14 << 8) | 0xC2, "Paradromics" },
            { (14 << 8) | 0xC4, "Metorage Semiconductor Technology Co" },
            { (14 << 8) | 0xC7, "China Flash Co Ltd" },
            { (14 << 8) | 0xC8, "Sunplus Technology Co Ltd" },
            { (14 << 8) | 0xCB, "IMEX Cap AG" },
            { (14 << 8) | 0xCD, "ShenzhenWooacme Technology Co Ltd" },
            { (14 << 8) | 0xCE, "KeepData Original Chips" },
            { (14 << 8) | 0xD0, "Big Innovation Company Limited" },
            { (14 << 8) | 0xD3, "PQShield Ltd" },
            { (14 << 8) | 0xD5, "ShenZhen AZW Technology Co Ltd" },
            { (14 << 8) | 0xD6, "Hengchi Zhixin (Dongguan) Technology" },
            { (14 << 8) | 0xD9, "PULP Platform" },
            { (14 << 8) | 0xDA, "Koitek Electronic Technology (Shenzhen) Co" },
            { (14 << 8) | 0xDC, "Aviva Links Inc" },
            { (14 << 8) | 0xDF, "Guangdong OPPO Mobile Telecommunication" },
            { (14 << 8) | 0xE0, "Akeana" },
            { (14 << 8) | 0xE3, "Shenzhen Shangzhaoyuan Technology" },
            { (14 << 8) | 0xE5, "China Micro Semicon Co., Ltd." },
            { (14 << 8) | 0xE6, "Shenzhen Zhuqin Technology Co Ltd" },
            { (14 << 8) | 0xE9, "Suzhou Yishuo Electronics Co Ltd" },
            { (14 << 8) | 0xEA, "Faurecia Clarion Electronics" },
            { (14 << 8) | 0xEC, "CFD Sales Inc" },
            { (14 << 8) | 0xEF, "Qorvo Inc" },
            { (14 << 8) | 0xF1, "Sychw Technology (Shenzhen) Co Ltd" },
            { (14 << 8) | 0xF2, "MK Founder Technology Co Ltd" },
            { (14 << 8) | 0xF4, "Hongkong Hyunion Electronics Co Ltd" },
            { (14 << 8) | 0xF7, "Shenzhen Jingyi Technology Co Ltd" },
            { (14 << 8) | 0xF8, "Xiaohua Semiconductor Co. Ltd." },
            { (14 << 8) | 0xFB, "ICYC Semiconductor Co Ltd" },
            { (14 << 8) | 0xFD, "Beijing EC-Founder Co Ltd" },
            { (14 << 8) | 0xFE, "Shenzhen Taike Industrial Automation Co" },
            { (15 << 8) | 0x01, "Kalray SA" },
            { (15 << 8) | 0x02, "Shanghai Iluvatar CoreX Semiconductor Co" },
            { (15 << 8) | 0x04, "Song Industria E Comercio de Eletronicos" },
            { (15 << 8) | 0x07, "Fusontai Technology" },
            { (15 << 8) | 0x08, "Endress Hauser AG" },
            { (15 << 8) | 0x0B, "Shenzhen Jing Da Kang Technology Co Ltd" },
            { (15 << 8) | 0x0D, "Pliops Ltd" },
            { (15 << 8) | 0x0E, "Cix Technology (Shanghai) Co Ltd" },
            { (15 << 8) | 0x10, "SpacemiT (Hangzhou)Technology Co Ltd" },
            { (15 << 8) | 0x13, "Yunhight Microelectronics" },
            { (15 << 8) | 0x15, "HKC Storage Co Ltd" },
            { (15 << 8) | 0x16, "Chiplego Technology (Shanghai) Co Ltd" },
            { (15 << 8) | 0x19, "Guangdong LeapFive Technology Limited" },
            { (15 << 8) | 0x1A, "Jin JuQuan" },
            { (15 << 8) | 0x1C, "Gigastone Corporation" },
            { (15 << 8) | 0x1F, "Shenzhen Xunhi Technology Co Ltd" },
            { (15 << 8) | 0x20, "FOXX Storage Inc" },
            { (15 << 8) | 0x23, "Sahasra Semiconductors Pvt Ltd" },
            { (15 << 8) | 0x25, "Shenzhen Zhixing Intelligent Manufacturing" },
            { (15 << 8) | 0x26, "Ethernovia" },
            { (15 << 8) | 0x29, "JGINYUE" },
            { (15 << 8) | 0x2A, "Shenzhen Xinwei Semiconductor Co Ltd" },
            { (15 << 8) | 0x2C, "B LKE" },
            { (15 << 8) | 0x2F, "Enphase Energy Inc" },
            { (15 << 8) | 0x31, "Shenzhen Sinomos Semiconductor Technology" },
            { (15 << 8) | 0x32, "O2micro International Limited" },
            { (15 << 8) | 0x34, "Silicon Legend Technology (Suzhou) Co Ltd" },
            { (15 << 8) | 0x37, "Yangtze MasonSemi" },
            { (15 << 8) | 0x38, "Shanghai Yunsilicon Technology Co Ltd" },
            { (15 << 8) | 0x3B, "Shenzhen Visions Chip Electronic Technology" },
            { (15 << 8) | 0x3D, "Shenzhen Aboison Technology Co Ltd" },
            { (15 << 8) | 0x3E, "Shenzhen JingSheng Semiconducto Co Ltd" },
            { (15 << 8) | 0x40, "EVAS Intelligence Co Ltd" },
            { (15 << 8) | 0x43, "Shenzhen Xinrui Renhe Technology" },
            { (15 << 8) | 0x45, "Silicon Innovation Technologies Co Ltd" },
            { (15 << 8) | 0x46, "Shenzhen Zhengxinda Technology Co Ltd" },
            { (15 << 8) | 0x49, "CEC Huada Electronic Design Co Ltd" },
            { (15 << 8) | 0x4A, "Westberry Technology Inc" },
            { (15 << 8) | 0x4C, "UNIM Semiconductor (Shang Hai) Co Ltd" },
            { (15 << 8) | 0x4F, "Enfabrica Corporation" },
            { (15 << 8) | 0x51, "Xiaoli AI Electronics (Shenzhen) Co Ltd" },
            { (15 << 8) | 0x52, "Silicon Mitus" },
            { (15 << 8) | 0x54, "HomeNet" },
            { (15 << 8) | 0x57, "Synology" },
            { (15 << 8) | 0x58, "Trium Elektronik Bilgi Islem San Ve Dis" },
            { (15 << 8) | 0x5B, "Sichuan Heentai Semiconductor Co Ltd" },
            { (15 << 8) | 0x5D, "www.shingroup.cn" },
            { (15 << 8) | 0x5E, "Suzhou Nano Mchip Technology Company" },
            { (15 << 8) | 0x61, "Golden Memory" },
            { (15 << 8) | 0x62, "Qingdao Thunderobot Technology Co Ltd" },
            { (15 << 8) | 0x64, "HYPHY USA" },
            { (15 << 8) | 0x67, "Hainan Zhongyuncun Technology Co Ltd" },
            { (15 << 8) | 0x68, "Shenzhen Yousheng Bona Technology Co" },
            { (15 << 8) | 0x6B, "iStarChip CA LLC" },
            { (15 << 8) | 0x6D, "Novatek Microelectronics Corp" },
            { (15 << 8) | 0x6E, "Chemgdu EG Technology Co Ltd" },
            { (15 << 8) | 0x70, "Syntiant" },
            { (15 << 8) | 0x73, "Yibai Electronic Technologies" },
            { (15 << 8) | 0x75, "HOGE Technology Co Ltd" },
            { (15 << 8) | 0x76, "United Micro Technology (Shenzhen) Co" },
            { (15 << 8) | 0x79, "Elitestek" },
            { (15 << 8) | 0x7A, "Cornelis Networks Inc" },
            { (15 << 8) | 0x7C, "ForwardEdge ASIC" },
            { (15 << 8) | 0x83, "Fungible Inc" },
            { (15 << 8) | 0x85, "DreamBig Semiconductor Inc" },
            { (15 << 8) | 0x86, "ChampTek Electronics Corp" },
            { (15 << 8) | 0x89, "altec ComputerSysteme GmbH" },
            { (15 << 8) | 0x8A, "UltraRISC Technology (Shanghai) Co Ltd" },
            { (15 << 8) | 0x8C, "Hangzhou Hongjun Microelectronics Co Ltd" },
            { (15 << 8) | 0x8F, "TeraDevices Inc" },
            { (15 << 8) | 0x91, "InnoPhase loT Inc" },
            { (15 << 8) | 0x92, "InnoPhase loT Inc" },
            { (15 << 8) | 0x94, "Samnix" },
            { (15 << 8) | 0x97, "StoreSkill" },
            { (15 << 8) | 0x98, "Shenzhen Astou Technology Company" },
            { (15 << 8) | 0x9B, "Huaxuan Technology (Shenzhen) Co Ltd" },
            { (15 << 8) | 0x9D, "Kinsotin" },
            { (15 << 8) | 0x9E, "PengYing" },
            { (15 << 8) | 0xA1, "Shanghai Belling Corporation Ltd" },
            { (15 << 8) | 0xA2, "Glenfy Tech Co Ltd" },
            { (15 << 8) | 0xA4, "Chongqing SeekWave Technology Co Ltd" },
            { (15 << 8) | 0xA7, "Shenzhen Xinrongda Technology Co Ltd" },
            { (15 << 8) | 0xA8, "Hangzhou Clounix Technology Limited" },
            { (15 << 8) | 0xAB, "COLORFIRE Technology Co Ltd" },
            { (15 << 8) | 0xAD, "ZHUDIAN" },
            { (15 << 8) | 0xAE, "REECHO" },
            { (15 << 8) | 0xB0, "Shenzhen Yingrui Storage Technology Co Ltd" },
            { (15 << 8) | 0xB3, "Axelera AI BV" },
            { (15 << 8) | 0xB5, "Suzhou Novosense Microelectronics Co Ltd" },
            { (15 << 8) | 0xB6, "Pirateman" },
            { (15 << 8) | 0xB9, "Rayson" },
            { (15 << 8) | 0xBA, "Alphawave IP" },
            { (15 << 8) | 0xBC, "KYO Group" },
            { (15 << 8) | 0xBF, "Shenzhen Dingsheng Technology Co Ltd" },
            { (15 << 8) | 0xC1, "Kaibright Electronic Technologies" },
            { (15 << 8) | 0xC2, "Fraunhofer IMS" },
            { (15 << 8) | 0xC4, "Beijing Vcore Technology Co Ltd" },
            { (15 << 8) | 0xC7, "Shenzhen Remai Electronics Co Lttd" },
            { (15 << 8) | 0xC8, "Shenzhen Xinruiyan Electronics Co Ltd" },
            { (15 << 8) | 0xCB, "Tongxin Microelectronics Co Ltd" },
            { (15 << 8) | 0xCD, "Shenzhen Qiaowenxingyu Industrial Co Ltd" },
            { (15 << 8) | 0xCE, "ICC" },
            { (15 << 8) | 0xD0, "Niobium Microsystems Inc" },
            { (15 << 8) | 0xD3, "Ajiatek Inc" },
            { (15 << 8) | 0xD5, "Shenzhen Shubang Technology Co Ltd" },
            { (15 << 8) | 0xD6, "Exacta Technologies Ltd" },
            { (15 << 8) | 0xD9, "Wuxi HippStor Technology Co Ltd" },
            { (15 << 8) | 0xDA, "SSCT" },
            { (15 << 8) | 0xDC, "Zhejiang University" },
            { (15 << 8) | 0xDF, "Feature Integration Technology Inc" },
            { (15 << 8) | 0xE0, "d-Matrix" },
            { (15 << 8) | 0xE3, "Shenzhen Tianxiang Chuangxin Technology" },
            { (15 << 8) | 0xE5, "Valkyrie" },
            { (15 << 8) | 0xE6, "Suzhou Hesetc Electronic Technology Co" },
            { (15 << 8) | 0xE9, "Shenzhen Xinle Chuang Technology Co" },
            { (15 << 8) | 0xEA, "DEEPX" },
            { (15 << 8) | 0xEC, "Shenzhen Vinreada Technology Co Ltd" },
            { (15 << 8) | 0xEF, "AGI Technology" },
            { (15 << 8) | 0xF1, "AOC" },
            { (15 << 8) | 0xF2, "GamePP" },
            { (15 << 8) | 0xF4, "Hangzhou Rencheng Trading Co Ltd" },
            { (15 << 8) | 0xF7, "Fabric of Truth Inc" },
            { (15 << 8) | 0xF8, "Elpitech" },
            { (15 << 8) | 0xFB, "WingSemi Technologies Co Ltd" },
            { (15 << 8) | 0xFD, "Beijing Future Signet Technology Co Ltd" },
            { (15 << 8) | 0xFE, "Fine Made Microelectronics Group Co Ltd" },
            { (16 << 8) | 0x01, "CXSH" },
            { (16 << 8) | 0x02, "Synconv" },
            { (16 << 8) | 0x04, "Zero ASIC Corporation" },
            { (16 << 8) | 0x07, "Shenzhen South Electron Co Ltd" },
            { (16 << 8) | 0x08, "Iontra Inc" },
            { (16 << 8) | 0x0B, "Anhui SunChip Semiconductor Technology" },
            { (16 << 8) | 0x0D, "AUTOTALKS" },
            { (16 << 8) | 0x0E, "Shenzhen Ranshuo Technology Co Limited" },
            { (16 << 8) | 0x10, "XCMemory Co Ltd" },
            { (16 << 8) | 0x13, "Milli-Centi Intelligence Technology Jiangsu" },
            { (16 << 8) | 0x15, "Incore Semiconductors" },
            { (16 << 8) | 0x16, "Kinetic Technologies" },
            { (16 << 8) | 0x19, "Shenzhen Techwinsemi Technology Co Ltd" },
            { (16 << 8) | 0x1A, "Pure Array Technology (Shanghai) Co Ltd" },
            { (16 << 8) | 0x1C, "RISE MODE" },
            { (16 << 8) | 0x1F, "Senscomm Semiconductor Co Ltd" },
            { (16 << 8) | 0x20, "Holt Integrated Circuits" },
            { (16 << 8) | 0x23, "Guangzhou Kaishile Trading Co Ltd" },
            { (16 << 8) | 0x25, "Memoritek" },
            { (16 << 8) | 0x26, "Zhejiang Hikstor Technology Co Ltd" },
            { (16 << 8) | 0x29, "LX Semicon" },
            { (16 << 8) | 0x2A, "Shenzhen Techwinsemi Technology Co Ltd" },
            { (16 << 8) | 0x2C, "GOEPEL Electronic GmbH" },
            { (16 << 8) | 0x2F, "EA Semi Shangahi Limited" },
            { (16 << 8) | 0x31, "Shenzhen MicroBT Electronics Technology" },
            { (16 << 8) | 0x32, "Shanghai Simor Chip Semiconductor Co" },
            { (16 << 8) | 0x34, "Guangzhou Maidite Electronics Co Ltd." },
            { (16 << 8) | 0x37, "SSTC Technology and Distribution Inc" },
            { (16 << 8) | 0x38, "Shenzhen Panmin Technology Co Ltd" },
            { (16 << 8) | 0x3B, "Powerchip Micro Device" },
            { (16 << 8) | 0x3D, "Shenzhen Titan Micro Electronics Co Ltd" },
            { (16 << 8) | 0x3E, "Shenzhen Macroflash Technology Co Ltd" },
            { (16 << 8) | 0x40, "Shenzhen Xingjiachen Electronics Co Ltd" },
            { (16 << 8) | 0x43, "Shenzhen Miuman Technology Co Ltd" },
            { (16 << 8) | 0x45, "Encharge AI Inc" },
            { (16 << 8) | 0x46, "Shenzhen Zhenchuang Electronics Co Ltd" },
            { (16 << 8) | 0x49, "Scalinx" },
            { (16 << 8) | 0x4A, "Shenzhen Lanqi Electronics Co Ltd" },
            { (16 << 8) | 0x4C, "DLI Memory" },
            { (16 << 8) | 0x4F, "Flastor" },
            { (16 << 8) | 0x51, "Barrie Technologies Co Ltd" },
            { (16 << 8) | 0x52, "Dynacard Co Ltd" },
            { (16 << 8) | 0x54, "Shenzhen Fidat Technology Co Ltd" },
            { (16 << 8) | 0x57, "Duvonn Electronic Technology Co Ltd" },
            { (16 << 8) | 0x58, "Shenzhen Xinchang Technology Co Ltd" },
            { (16 << 8) | 0x5B, "Applied Brain Research Inc" },
            { (16 << 8) | 0x5D, "HK DCHIP Technology Limited" },
            { (16 << 8) | 0x5E, "Hitachi-LG Data Storage" },
            { (16 << 8) | 0x61, "Shenzhen Think Future Semiconductor Co" },
            { (16 << 8) | 0x62, "Innosilicon" },
            { (16 << 8) | 0x64, "Agrade Storage (Shenzhen) Co Ltd" },
            { (16 << 8) | 0x67, "BYD Semiconductor Co Ltd" },
            { (16 << 8) | 0x68, "Chipsine Semiconductor (Suzhou) Co Ltd" },
            { (16 << 8) | 0x6B, "Shenzhen Baina Haichuan Technology Co" },
            { (16 << 8) | 0x6D, "Beijing Boyu Tuxian Technology Co Ltd" },
            { (16 << 8) | 0x6E, "China Chips Star Semiconductor Co Ltd" },
            { (16 << 8) | 0x70, "Kinara Inc" },
            { (16 << 8) | 0x73, "Shenzhen YYF Info Tech Co Ltd" },
            { (16 << 8) | 0x75, "AptCore Limited" },
            { (16 << 8) | 0x76, "Uchampion Semiconductor Co Ltd" },
            { (16 << 8) | 0x79, "Hefei CLT Microelectronics Co LTD" },
            { (16 << 8) | 0x7A, "Smart Technologies (BD) Ltd" },
            { (16 << 8) | 0x7C, "Silicon Xpandas Electronics Co Ltd" },
            { (16 << 8) | 0x83, "MULTIUNIT" },
            { (16 << 8) | 0x85, "NTT Innovative Devices Corporation" },
            { (16 << 8) | 0x86, "Xbstor" },
            { (16 << 8) | 0x89, "SIEFFI Inc" },
            { (16 << 8) | 0x8A, "HK Winston Electronics Co Limited" },
            { (16 << 8) | 0x8C, "HaiLa Technologies Inc" },
            { (16 << 8) | 0x8F, "ScaleFlux" },
            { (16 << 8) | 0x91, "Guangzhou Beimu Technology Co Ltd" },
            { (16 << 8) | 0x92, "Rays Semiconductor Nanjing Co Ltd" },
            { (16 << 8) | 0x94, "Zilia Technologies" },
            { (16 << 8) | 0x97, "Nanjing Houmo Technology Co Ltd" },
            { (16 << 8) | 0x98, "Suzhou Yige Technology Co Ltd" },
            { (16 << 8) | 0x9B, "Shenzhen Techwinsemi Technology Udstore" },
            { (16 << 8) | 0x9D, "NEWREESTAR" },
            { (16 << 8) | 0x9E, "Hangzhou Hualan Microeletronique Co Ltd" },
            { (16 << 8) | 0xA1, "Tenstorrent Inc" },
            { (16 << 8) | 0xA2, "SkyeChip" },
            { (16 << 8) | 0xA4, "Jing Pai Digital Technology (Shenzhen) Co" },
            { (16 << 8) | 0xA7, "Memoritek PTE Ltd" },
            { (16 << 8) | 0xA8, "Longsailing Semiconductor Co Ltd" },
            { (16 << 8) | 0xAB, "AOC" },
            { (16 << 8) | 0xAD, "Shenzhen G-Bong Technology Co Ltd" },
            { (16 << 8) | 0xAE, "Openedges Technology Inc" },
            { (16 << 8) | 0xB0, "EMBCORF" },
            { (16 << 8) | 0xB3, "Xllbyte" },
            { (16 << 8) | 0xB5, "Zhejiang Changchun Technology Co Ltd" },
            { (16 << 8) | 0xB6, "Beijing Cloud Security Technology Co Ltd" },
            { (16 << 8) | 0xB9, "ITE Tech Inc" },
            { (16 << 8) | 0xBA, "Beijing Zettastone Technology Co Ltd" },
            { (16 << 8) | 0xBC, "Shenzhen Ysemi Computing Co Ltd" },
            { (16 << 8) | 0xBF, "Advantech Group" },
            { (16 << 8) | 0xC1, "CHUQI" },
            { (16 << 8) | 0xC2, "Dongguan Liesun Trading Co Ltd" },
            { (16 << 8) | 0xC4, "Shenzhen Techwinsemi Technology Twsc" },
            { (16 << 8) | 0xC7, "Giant Chip Co. Ltd" },
            { (16 << 8) | 0xC8, "Shenzhen Runner Semiconductor Co Ltd" },
            { (16 << 8) | 0xCB, "CoreComm Technology Co Ltd" },
            { (16 << 8) | 0xCD, "Shenzhen Fidat Technology Co Ltd" },
            { (16 << 8) | 0xCE, "Hubei Yangtze Mason Semiconductor Tech" },
            { (16 << 8) | 0xD0, "PIRATEMAN" },
            { (16 << 8) | 0xD3, "Rivian Automotive" },
            { (16 << 8) | 0xD5, "Zhejang Weiming Semiconductor Co Ltd" },
            { (16 << 8) | 0xD6, "Shenzhen Xinhua Micro Technology Co Ltd" },
            { (16 << 8) | 0xD9, "Leidos" },
            { (16 << 8) | 0xDA, "Keepixo" },
            { (16 << 8) | 0xDC, "Maxio Technology (Hangzhou) Co Ltd" },
            { (16 << 8) | 0xDF, "Shenzhen Huadian Communication Co Ltd" },
            { (16 << 8) | 0xE0, "Achieve Memory Technology (Suzhou) Co" },
            { (16 << 8) | 0xE3, "Shenzhen Weilida Technology Co Ltd" },
            { (16 << 8) | 0xE5, "Shenzhen Worldshine Data Technology Co" },
            { (16 << 8) | 0xE6, "Mindgrove Technologies" },
            { (16 << 8) | 0xE9, "Shen Zhen Shi Xun He Shi Ji Dian Zi You" },
            { (16 << 8) | 0xEA, "Shenzhen Jindacheng Computer Co Ltd" },
            { (16 << 8) | 0xEC, "Shanghai Hengshi Electronic Technology" },
            { (16 << 8) | 0xEF, "Shenzhen Shenghuacan Technology Co" },
            { (16 << 8) | 0xF1, "TRASNA Semiconductor" },
            { (16 << 8) | 0xF2, "KEYSOM" },
            { (16 << 8) | 0xF4, "Sharetronics Data Technology Co Ltd" },
            { (16 << 8) | 0xF7, "YCT Semiconductor" },
            { (16 << 8) | 0xF8, "FADU Inc" },
            { (16 << 8) | 0xFB, "Zhangdian District Qunyuan Computer Firm" },
            { (16 << 8) | 0xFD, "PC Components Y Multimedia S" },
            { (16 << 8) | 0xFE, "Shenzhen Tanlr Technology Group Co Ltd" },
            { (17 << 8) | 0x01, "Shenzhen JIEQING Technology Co Ltd" },
            { (17 << 8) | 0x02, "Orionix" },
            { (17 << 8) | 0x04, "Tenstorrent" },
            { (17 << 8) | 0x07, "Ardor Gaming" },
            { (17 << 8) | 0x08, "QuanZhou KunFang Semiconductor Co Ltd" },
            { (17 << 8) | 0x0B, "Shenzhen Hancun Technology Co Ltd" },
            { (17 << 8) | 0x0D, "Shenzhen Storgon Technology Co Ltd" },
            { (17 << 8) | 0x0E, "YUNTU Microelectronics" },
            { (17 << 8) | 0x10, "Shenzhen Xingyun Lianchuang Computer Tech" },
            { (17 << 8) | 0x13, "BOS Semiconductors" },
            { (17 << 8) | 0x15, "Hangzhou Lishu Technology Co Ltd" },
            { (17 << 8) | 0x16, "Tier IV Inc" },
            { (17 << 8) | 0x19, "Tech Vision Information Technology Co" },
            { (17 << 8) | 0x1A, "Zhihe Computing Technology" },
            { (17 << 8) | 0x1C, "Yemas Holdingsl Limited" },
            { (17 << 8) | 0x1F, "Beijing Qixin Gongli Technology Co Ltd" },
            { (17 << 8) | 0x20, "M.RED" },
            { (17 << 8) | 0x23, "EmBestor Technology Inc" },
            { (17 << 8) | 0x25, "Flagchip" },
            { (17 << 8) | 0x26, "CUNNUC" },
            { (17 << 8) | 0x29, "FuturePlus Systems LLC" },
            { (17 << 8) | 0x2A, "Shenzhen Jielong Storage Technology Co" },
            { (17 << 8) | 0x2C, "Sichuan ZeroneStor Microelectronics Tech" },
            { (17 << 8) | 0x2F, "Bytera Memory Inc" },
            { (17 << 8) | 0x31, "Cloud Ridge Ltd" },
            { (17 << 8) | 0x32, "Shenzhen XinChiTai Technology Co Ltd" },
            { (17 << 8) | 0x34, "Shenzhen ShineKing Electronics Co Ltd." },
            { (17 << 8) | 0x37, "Beijing Ronghua Kangweiye Technology" },
            { (17 << 8) | 0x38, "Shanghai Yunsilicon Technology Co Ltd" },
            { (17 << 8) | 0x3B, "HRDWYR Ventures Private Limited" },
            { (17 << 8) | 0x3D, "GOFATOO" },
            { (17 << 8) | 0x3E, "Shenzhen Jingchu Technology Co Ltd" },
            { (17 << 8) | 0x40, "RZX" },
            { (17 << 8) | 0x43, "Shenzhen Shanghaowang Electronic Technology" },
            { (17 << 8) | 0x45, "Great Wall Global Information Technology" },
            { (17 << 8) | 0x46, "Beijing Memsilicon Technology Co Ltd" },
            { (17 << 8) | 0x49, "Shenzhen JinShanDe Technology Co Ltd" },
            { (17 << 8) | 0x4A, "RUIBOHU" },
            { (17 << 8) | 0x4C, "L&T Semiconductor Technologies Limited" },
            { (17 << 8) | 0x4F, "Shenzhen Ruiyuanchuangxin Technology" },
            { (17 << 8) | 0x51, "Jeju Semiconductor Corp" },
            { (17 << 8) | 0x52, "Origin Code LLC" },
            { (17 << 8) | 0x54, "Shenzhen Whalekom Technology Co Ltd" },
            { (17 << 8) | 0x57, "Shenzhenshijimokkejiyouxiangongsi" },
            { (17 << 8) | 0x58, "DEGUA" },
            { (17 << 8) | 0x5B, "GUANGSUJIE" },
            { (17 << 8) | 0x5D, "Theo End (Shenzhen) Computing Tech Co" },
            { (17 << 8) | 0x5E, "Shenzhen Heroje Electronics Co Ltd" },
            { (17 << 8) | 0x61, "JinTech Semiconductor Co Limited" },
            { (17 << 8) | 0x62, "Shenzhen Hongred Information Technology Co" },
            { (17 << 8) | 0x64, "Veris Danismanlik Limited Sirketi" },
            { (17 << 8) | 0x67, "Djelec" },
            { (17 << 8) | 0x68, "Ambarella Corporation" },
            { (17 << 8) | 0x6B, "Shenzhen Econo Electronic Co Ltd (China)" },
            { (17 << 8) | 0x6D, "Shenzhen Kimviking Technology Co Ltd" },
            { (17 << 8) | 0x6E, "Shenzhen Touch Think Intelligence Co Ltd" },
            { (17 << 8) | 0x70, "BYTE International Co Ltd" },
            { (17 << 8) | 0x73, "Overlord Labs" },
            { (17 << 8) | 0x75, "Shenzhen Yuqi Electronics Co Ltd" },
            { (17 << 8) | 0x76, "Transsemi Miceoelectronics Co Ltd" },
            { (17 << 8) | 0x79, "Shenzhen Xingjiachen Electronics Co Ltd" },
            { (17 << 8) | 0x7A, "Shenyang Lianxin Chuangxiang Technology" },
            { (17 << 8) | 0x7C, "ChongQing YuJia Smart Storage Digital" },
            { (17 << 8) | 0x83, "JoulWatt Technology Co Ltd" },
            { (17 << 8) | 0x85, "Unis Flash Memory Technology (Chengdu)" },
            { (17 << 8) | 0x86, "Huatu Stars" },
            { (17 << 8) | 0x89, "EIAI PLANET" },
            { (17 << 8) | 0x8A, "Ningbo Lingkai Semiconductor Technology Inc" },
            { (17 << 8) | 0x8C, "Hongkong Manyi Technology Co Limited" },
            { (17 << 8) | 0x8F, "Essencore" },
            { (17 << 8) | 0x91, "ShenZhen Aoscar Digital Tech Co Ltd" },
            { (17 << 8) | 0x92, "XOC Technologies Inc" },
            { (17 << 8) | 0x94, "Eliyan Corp" },
            { (17 << 8) | 0x97, "Wuhan Xuanluzhe Network Technology Co" },
            { (17 << 8) | 0x98, "EA Semi (Shanghai) Limited" },
            { (17 << 8) | 0x9B, "Beijing Apexichips Tech" },
            { (17 << 8) | 0x9D, "Eluktronics" },
            { (17 << 8) | 0x9E, "Walton Digi-Tech Industries Ltd" },
            { (17 << 8) | 0xA1, "Shenzhen Damay Semiconductor Co Ltd" },
            { (17 << 8) | 0xA2, "Corelab Tech Singapore Holding PTE LTD" },
            { (17 << 8) | 0xA4, "XConn Technologies" },
            { (17 << 8) | 0xA7, "SGMicro" },
            { (17 << 8) | 0xA8, "Lanxin Computing (Shenzhen) Technology" },
            { (17 << 8) | 0xAB, "Precision Planting LLC" },
            { (17 << 8) | 0xAD, "The University of Tokyo" },
            { (17 << 8) | 0xAE, "Aodu (Fujian) Information Technology Co" },
            { (17 << 8) | 0xB0, "XSemitron Technology Inc" },
            { (17 << 8) | 0xB3, "Shenzhen Xinxin Semiconductor Co Ltd" },
            { (17 << 8) | 0xB5, "Shenzhen Shande Semiconductor Co. Ltd." },
            { (17 << 8) | 0xB6, "AheadComputing" },
            { (17 << 8) | 0xB9, "Shenzhen Wolongtai Technology Co Ltd." },
            { (17 << 8) | 0xBA, "Vervesemi Microelectronics" },
            { (17 << 8) | 0xBC, "ENE Technology Inc" },
            { (17 << 8) | 0xBF, "NEVETA" },
            { (17 << 8) | 0xC1, "Shenzhen Zhongcheng Qingcong Technology Co" },
            { (17 << 8) | 0xC2, "Shenzhen Heyloo Electronic Technology Co" },
            { (17 << 8) | 0xC4, "Shanghai Qimingxin Semiconductor Technology" },
            { (17 << 8) | 0xC7, "Openchip & Software Technologies S L" },
            { (17 << 8) | 0xC8, "FlashLeap Tech (Shenzhen) Co Ltd" },
            { (17 << 8) | 0xCB, "ETA Semiconductor Ltd" },
            { (17 << 8) | 0xCD, "RAYSMEM" },
            { (17 << 8) | 0xCE, "Sichuan Zhongzhao Yongye Semiconductor" },
            { (17 << 8) | 0xD0, "CSSI South Africa (Pty) Ltd" },
            { (17 << 8) | 0xD3, "Chengdu Masscore Microelectronics Tech" },
            { (17 << 8) | 0xD5, "femtoAI" },
            { (17 << 8) | 0xD6, "Shanghai Chip4Tao Technology Co Ltd" },
            { (17 << 8) | 0xD9, "TENGYIN CELESTIALSTORAGE" },
            { (17 << 8) | 0xDA, "Shenzhen Minder Semiconductor Co Ltd" },
            { (17 << 8) | 0xDC, "HVLANYN Technology" },
            { (17 << 8) | 0xDF, "Guangdong Yuecun Microelectronics Co" },
            { (17 << 8) | 0xE0, "Foxin Technology" },
            { (17 << 8) | 0xE3, "ZST Inc" },
            { (17 << 8) | 0xE5, "Starblaze" },
            { (17 << 8) | 0xE6, "Siptechx" },
            { (17 << 8) | 0xE9, "SiliconX" },
            { (17 << 8) | 0xEA, "Shenzhen Tencent Computer System Co" },
            { (17 << 8) | 0xEC, "Pu Sl Technology (Shenzhen) Co Limited" },
            { (17 << 8) | 0xEF, "Beijing Institute of Open Source Chip" },
            { (17 << 8) | 0xF1, "Shenzhen ITZR Technology Co Ltd" },
            { (17 << 8) | 0xF2, "Centre Development Advanced Computing" },
            { (17 << 8) | 0xF4, "Shenzhen Chaocun Technology Co Ltd" },
            { (17 << 8) | 0xF7, "Pu Sl Technology (Shenzhen) Co Limited" },
            { (17 << 8) | 0xF8, "Shenzhen Xingjiachen Electronics Co Ltd" },
            { (17 << 8) | 0xFB, "Plusetech Technology (Shenzhen) Co Ltd" },
            { (17 << 8) | 0xFD, "CTST Co Ltd" },
            { (17 << 8) | 0xFE, "SSK Corporation" },
            { (18 << 8) | 0x01, "Shenzhen Xinruiyun Storage Technology" },
            { (18 << 8) | 0x02, "JSC Megapolis-Telecom Region" },
            { (18 << 8) | 0x04, "Shenzhen Yingzhong Technology Co Ltd" },
            { (18 << 8) | 0x07, "Shenzhen Dawei Microelectronics Technology Co" },
            { (18 << 8) | 0x08, "Shenzhen Zhuoran Electronics Co Ltd" },
            { (18 << 8) | 0x0B, "Shanghai RuiPan Electronic Information Tech" },
            { (18 << 8) | 0x0D, "Jiyi Semiconductor Co Ltd" },
            { (18 << 8) | 0x0E, "Shenzhen Silicon Hermit Technology Ltd" },
            { (18 << 8) | 0x10, "ALBSEMI (Hangzhou) Co Ltd" },
            { (18 << 8) | 0x13, "Shenzhen Yunyue Semiconductor Co Ltd." },
            { (18 << 8) | 0x15, "Shenzhen Tianchuang Weiye Technology" },
            { (18 << 8) | 0x16, "Shanghai Simchip Technology Group" },
            { (18 << 8) | 0x19, "Zhejiang Xinhan Intelligent Manufacturing" },
            { (18 << 8) | 0x1A, "Shenzhen Tranvita Technology Co Ltd" },
            { (18 << 8) | 0x1C, "Shenzhen Yunmemory AI Technology Co" },
            { (18 << 8) | 0x1F, "Beijing ZhanChuang ZhenHua Technology Co" },
            { (18 << 8) | 0x20, "Sichuan Huakun Zhenyu Intelligent Technology" },
            { (18 << 8) | 0x23, "Shenzhen Coosure Electronics Co Ltd" },
            { (18 << 8) | 0x25, "ShenZhen Goodtimes Technology Co Ltd" },
            { (18 << 8) | 0x26, "Shanxi Bluestone Guangzhi Technology Co Ltd" },
            { (18 << 8) | 0x29, "Aivres Systems Inc" },
            { (18 << 8) | 0x2A, "Liveseek" },
            { (18 << 8) | 0x2C, "Shenzhen Qiben Electronics Co Ltd" },
            { (18 << 8) | 0x2F, "Shenzhen Powerleader Storage Technology" },
            { (18 << 8) | 0x31, "Shenzhen Xbnow Technology Co Ltd" },
            { (18 << 8) | 0x32, "YCHIPWAY Semiconductor Technology" },
            { (18 << 8) | 0x34, "ARIA Sensing Srl" },
            { (18 << 8) | 0x37, "North Magna Limited" },
            { (18 << 8) | 0x38, "Unisemi Power Inc" },
            { (18 << 8) | 0x3B, "Shenzhen DTX Space Technology Co Ltd" },
            { (18 << 8) | 0x83, "Minpo Corporation" },
            { (18 << 8) | 0x85, "Moment Semiconductor Inc" },
            { (18 << 8) | 0x86, "Yiweihong (Shenzhen) Electronic Technology Co" },
            { (18 << 8) | 0x89, "Mengxw Electron Ltd" },
            { (18 << 8) | 0x8A, "Origin Storage Ltd" },
            { (18 << 8) | 0x8C, "Fortell Research Inc" },
            { (18 << 8) | 0x8F, "HuiRong Electronic System Engineering Co LTD" },
            { (18 << 8) | 0x91, "ULX" },
            { (18 << 8) | 0x92, "Sichuan Huazhixincun Technology Co Ltd" },
            { (18 << 8) | 0x94, "Fortune Sky Enterprise Limited" },
            { (18 << 8) | 0x97, "SVOD Project SRO" },
            { (18 << 8) | 0x98, "Shenzhen Xunnaying Technology Co Ltd" },
            { (18 << 8) | 0x9B, "Zhongke Zhichun (Zhuhai) Technology Co" },
            { (18 << 8) | 0x9D, "Shenzhen KEHB Electronics Co Ltd" },
            { (18 << 8) | 0x9E, "Shenzhen EVOC Storage Technology Co" },
            { (18 << 8) | 0xA1, "Hunan Hongen Electronics Co Ltd" },
            { (18 << 8) | 0xA2, "Shenzhen Youjing Microelectronics Technology" },
            { (18 << 8) | 0xA4, "Seavo Future Technology (Shenzhen) Co Ltd" },
            { (18 << 8) | 0xA7, "Shenzhen Lianrunfeng Electronic Technology Co" },
            { (18 << 8) | 0xA8, "Shenzhen Seapiy Technology Co Ltd" },
            { (18 << 8) | 0xAB, "Shenzhen KingSpec Electronics Technology" },
            { (18 << 8) | 0xAD, "Guangdong Shunwei Microelectronics Tech" },
            { (18 << 8) | 0xAE, "Efficient Computer" },
            { (18 << 8) | 0xB0, "Shenzhen Chuangzhan Semiconductor Co Ltd" },
            { (18 << 8) | 0xB3, "Shenzhen Yansen Industrial Storage Co Ltd" },
            { (18 << 8) | 0xB5, "EverModule Technology Co Ltd" },
            { (18 << 8) | 0xB6, "The AIO" },
            { (18 << 8) | 0xB9, "Shenzhen RongPei Technology Co Ltd" },
            { (18 << 8) | 0xBA, "HKIC International Electronics Limited" },
            { (18 << 8) | 0xBC, "Ranxin Technology" },
        };

        public static string LookupManufacturer(byte bankByte, byte idByte)
        {
            int bankNumber = (bankByte & 0x7F) + 1;
            int key = (bankNumber << 8) | idByte;
            string name;
            return Manufacturers.TryGetValue(key, out name)
                ? name
                : $"Unknown (bank {bankNumber}, ID 0x{idByte:X2})";
        }

        private static string FormatNck(int timingPs, int tCkPs)
        {
            int nck = PsToNckDdr4(timingPs, tCkPs);
            return $"{nck} nCK ({timingPs} ps)";
        }

        private static string FormatNckDdr5(int timingPs, int tCkPs)
        {
            int nck = PsToNckDdr5(timingPs, tCkPs);
            return $"{nck} nCK ({timingPs} ps)";
        }

        private static string FormatMb(long mb)
        {
            if (mb <= 0) return "n/a";
            if (mb >= 1024) return $"{mb / 1024.0:F1} GB";
            return $"{mb} MB";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "n/a";
            const long KB = 1024L;
            long MB = KB * KB;
            long GB = MB * KB;
            if (bytes >= GB) return $"{bytes / (double)GB:F1} GB";
            if (bytes >= MB) return $"{bytes / (double)MB:F0} MB";
            return $"{bytes} B";
        }

        private static string FormatBcdDate(byte yearBcd, byte weekBcd)
        {
            int year = ((yearBcd >> 4) * 10) + (yearBcd & 0x0F);
            int week = ((weekBcd >> 4) * 10) + (weekBcd & 0x0F);
            return $"Wk {week:D2} / 20{year:D2}";
        }

        private static string AsciiTrim(byte[] spd, int offset, int length)
        {
            int end = offset + length;
            if (end > spd.Length) end = spd.Length;
            int actualEnd = end;
            while (actualEnd > offset && (spd[actualEnd - 1] == 0x20 || spd[actualEnd - 1] == 0x00))
                actualEnd--;
            var sb = new StringBuilder(actualEnd - offset);
            for (int i = offset; i < actualEnd; i++)
            {
                byte b = spd[i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            return sb.ToString();
        }

        #endregion
    }
}