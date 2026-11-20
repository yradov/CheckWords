using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

namespace CheckWords;

/// <summary>Settings in %APPDATA%\CheckWords\settings.json (edit by hand, then "Перечитать настройки").</summary>
internal sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool Enabled { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool WarnMidWord { get; set; } = true;
    public string ConvertWordHotkey { get; set; } = "Pause";
    public string ConvertSelectionHotkey { get; set; } = "Shift+Pause";
    public int BeepFrequency { get; set; } = 1100;
    public int BeepDurationMs { get; set; } = 90;

    /// <summary>Process names (e.g. "mstsc.exe") where the program does nothing.</summary>
    public List<string> ExcludedProcesses { get; set; } =
        ["mstsc.exe", "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe"];

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CheckWords");
    public static string FilePath => Path.Combine(Dir, "settings.json");
    public static string UserWordsPath => Path.Combine(Dir, "user-words.txt");
    public static string DictionaryDir => Path.Combine(Dir, "dict");

    public static AppSettings Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings(); // broken file: run with defaults but don't overwrite the user's file
            }
        }
        var settings = new AppSettings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(DictionaryDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public bool IsExcluded(string processName) =>
        ExcludedProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>"Не предупреждать для этого слова" list.</summary>
internal static class UserWords
{
    public static IEnumerable<string> Load() =>
        File.Exists(AppSettings.UserWordsPath) ? File.ReadAllLines(AppSettings.UserWordsPath) : [];

    public static void Add(string word)
    {
        Directory.CreateDirectory(AppSettings.Dir);
        File.AppendAllLines(AppSettings.UserWordsPath, [word]);
    }
}

internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "CheckWords";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

/// <summary>A hotkey like "Pause", "Shift+Pause", "Ctrl+Alt+Space" (key names from System.Windows.Forms.Keys).</summary>
internal sealed record Hotkey(int Vk, bool Ctrl, bool Shift, bool Alt, bool Win)
{
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, shift = false, alt = false, win = false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win": win = true; break;
                default: return null;
            }
        }
        if (!Enum.TryParse<Keys>(parts[^1], ignoreCase: true, out var key)) return null;
        return new Hotkey((int)key & 0xFFFF, ctrl, shift, alt, win);
    }

    public bool Matches(int vk, Modifiers m) =>
        vk == Vk && m.Ctrl == Ctrl && m.Shift == Shift && m.Alt == Alt && m.Win == Win;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Shift) parts.Add("Shift");
        if (Alt) parts.Add("Alt");
        if (Win) parts.Add("Win");
        parts.Add(((Keys)Vk).ToString());
        return string.Join("+", parts);
    }
}
