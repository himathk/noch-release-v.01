using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Noch;

sealed class Settings
{
    public bool AlwaysFull { get; set; }
    public bool Welcomed { get; set; }
    /// Which edge or corner it was dropped on.
    public string? Dock { get; set; }
    /// The screen it was dropped on, or empty for the main screen.
    public string? Screen { get; set; }
    public bool Dragged { get; set; }
    public string? LastVersion { get; set; }
    public bool Sounds { get; set; } = true;
    public double SoundVolume { get; set; } = .5;
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
    public Core.FocusState Focus { get; set; } = new();
    /// The calendar link, encrypted for this Windows user.
    public string? CalendarSecret { get; set; }
    public bool Notifications { get; set; }
    public bool HideNotificationText { get; set; }
    public HashSet<string> MutedApps { get; set; } = [];
    public bool PowerAlerts { get; set; } = true;
    public bool WheelVolume { get; set; } = true;
    public bool Shortcut { get; set; } = true;

    /// Kept outside the install folder, which is replaced on every update.
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.Name);
    public static string HookExe => Path.Combine(Folder, "noch-hook.exe");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); } catch { s = new(); }
        // A hand-edited file can hold anything.
        s.SoundVolume = double.IsFinite(s.SoundVolume) ? Math.Clamp(s.SoundVolume, 0, 1) : .5;
        s.FocusMinutes = Math.Clamp(s.FocusMinutes, 1, 240); s.BreakMinutes = Math.Clamp(s.BreakMinutes, 1, 120);
        s.Focus ??= new(); s.MutedApps ??= [];
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            // Written beside and swapped in, so a crash cannot leave half a file.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Program.Log(e); }
    }

    /// The helper lives at a fixed path so agent configs stay valid across updates.
    public static void InstallHook()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "noch-hook.exe");
        if (!File.Exists(src)) return;
        Directory.CreateDirectory(Folder);
        if (File.Exists(HookExe) && File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(HookExe))) return;
        try { File.Copy(src, HookExe, true); } catch (IOException) { /* in use right now; the next start replaces it */ }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartsWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Program.Name) != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Program.Name, $"\"{Environment.ProcessPath}\""); else k.DeleteValue(Program.Name, false);
        }
    }
}
