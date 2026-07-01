using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnifiedDDRFlasher",
        "settings.json");

    public static bool AutoConnect { get; set; }
    public static string LastPort { get; set; } = string.Empty;

    public static bool ShowSidebandTab { get; set; }

    private static Dictionary<string, (byte lsb, byte msb)> _pmicPasswords
        = new Dictionary<string, (byte, byte)>(StringComparer.OrdinalIgnoreCase);

    public static (byte lsb, byte msb) GetPMICPassword(string pmicType)
    {
        if (!string.IsNullOrEmpty(pmicType) && _pmicPasswords.TryGetValue(pmicType, out var pw))
            return pw;
        if (_pmicPasswords.TryGetValue("default", out var def))
            return def;
        return (0x73, 0x94);
    }

    public static void SetPMICPassword(string pmicType, byte lsb, byte msb)
    {
        string key = string.IsNullOrEmpty(pmicType) ? "default" : pmicType;
        _pmicPasswords[key] = (lsb, msb);
    }

    public static IReadOnlyDictionary<string, (byte lsb, byte msb)> GetAllPMICPasswords()
        => _pmicPasswords;


    public static void Load()
    {
        bool needsCleanupSave = false;
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);

            string[] removedFieldNames = { "RequireSignedDevice", "MasterKeyPath" };
            foreach (var name in removedFieldNames)
            {
                if (json.IndexOf("\"" + name + "\"", StringComparison.Ordinal) >= 0)
                {
                    needsCleanupSave = true;
                    break;
                }
            }

            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data == null) return;

            AutoConnect = data.AutoConnect;
            LastPort = data.LastPort ?? string.Empty;
            ShowSidebandTab = data.ShowSidebandTab;

            _pmicPasswords.Clear();
            if (data.PmicPasswords != null)
            {
                foreach (var kv in data.PmicPasswords)
                    _pmicPasswords[kv.Key] = (kv.Value.Lsb, kv.Value.Msb);
            }
        }
        catch { }

        if (needsCleanupSave)
        {
            try { Save(); } catch { }
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var pwDict = new Dictionary<string, PasswordEntry>();
            foreach (var kv in _pmicPasswords)
                pwDict[kv.Key] = new PasswordEntry { Lsb = kv.Value.lsb, Msb = kv.Value.msb };

            var data = new SettingsData
            {
                AutoConnect = AutoConnect,
                LastPort = LastPort,
                ShowSidebandTab = ShowSidebandTab,
                PmicPasswords = pwDict
            };

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data, opts));
        }
        catch { }
    }


    private class SettingsData
    {
        public bool AutoConnect { get; set; }
        public string LastPort { get; set; } = string.Empty;
        public bool ShowSidebandTab { get; set; }
        public Dictionary<string, PasswordEntry> PmicPasswords { get; set; }
            = new Dictionary<string, PasswordEntry>();
    }

    private class PasswordEntry
    {
        public byte Lsb { get; set; }
        public byte Msb { get; set; }
    }
}