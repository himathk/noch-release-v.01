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

    /// Kept outside the install folder, which is replaced on every update.
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.Name);
    public static string HookExe => Path.Combine(Folder, "noch-hook.exe");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
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
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Noch") != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Noch", $"\"{Environment.ProcessPath}\""); else k.DeleteValue("Noch", false);
        }
    }
}
