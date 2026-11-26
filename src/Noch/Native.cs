using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Noch;

static class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_MOUSEACTIVATE = 0x21, WM_HOTKEY = 0x312, MA_NOACTIVATE = 3;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MONITORINFO
    {
        public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    delegate bool MonitorProc(IntPtr m, IntPtr dc, IntPtr r, IntPtr l);

    [DllImport("user32")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc cb, IntPtr l);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO i);
    [DllImport("shcore")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);
    [DllImport("dwmapi")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    /// The button people drag with, even when the mouse is set up left-handed.
    public static int PrimaryButton => GetSystemMetrics(23) != 0 ? 2 : 1;
    [DllImport("user32")] static extern int GetSystemMetrics(int i);
    public static void Move(IntPtr h, int x, int y) => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10);

    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public int cbSize, flags; public IntPtr active, focus, capture, menuOwner, moveSize, caret; public RECT rc; }
    [DllImport("user32")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO i);
    /// True while a window is being moved or resized by hand.
    public static bool MovingWindow()
    {
        var i = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(0, ref i) && (i.flags & 2) != 0;
    }

    public static void KeepOnTop(IntPtr h) => SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);

    public static void SetExStyle(IntPtr h, int flag, bool on)
    {
        var s = GetWindowLong(h, GWL_EXSTYLE);
        var n = on ? s | flag : s & ~flag;
        if (n != s) SetWindowLong(h, GWL_EXSTYLE, n);
    }

    public static bool ProcessAlive(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
        try { return !GetExitCodeProcess(h, out var code) || code == 259; } finally { CloseHandle(h); }
    }

    /// Every monitor, in real pixels, with its own scaling.
    public static List<Core.ScreenInfo> Screens()
    {
        var list = new List<Core.ScreenInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var i = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(m, ref i)) return true;
            var dpi = GetDpiForMonitor(m, 0, out var x, out _) == 0 && x > 0 ? x : 96u;
            static Core.Px P(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
            list.Add(new(i.szDevice ?? "", P(i.rcMonitor), P(i.rcWork), dpi / 96.0, (i.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        // Never empty, even in the moment a display is being switched over.
        if (list.Count == 0) list.Add(new("", new(0, 0, 1920, 1080), new(0, 0, 1920, 1080), 1, true));
        return list;
    }

    /// Moves and sizes a window in real pixels, which stays right across screens with different scaling.
    public static void Put(IntPtr h, int x, int y, int w, int hh) => SetWindowPos(h, IntPtr.Zero, x, y, w, hh, 0x4 | 0x10);

    /// What lies under a point on monitor `m`: nothing, a normal window, or something fullscreen.
    public static (bool Covered, bool Fullscreen) Probe(IntPtr self, int cx, int cy, Core.Px m)
    {
        var result = (false, false);
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (h == self || !IsWindowVisible(h) || IsIconic(h)) return true;
            var ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
            if (DwmGetWindowAttribute(h, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(h, out var r) || cx < r.Left || cx >= r.Right || cy < r.Top || cy >= r.Bottom) return true;
            cls.Clear(); GetClassName(h, cls, 64);
            if (cls.ToString() is "Progman" or "WorkerW") return false;
            var fills = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
            var captioned = IsZoomed(h) && (GetWindowLong(h, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
            result = (true, fills && !captioned);
            return false;
        }, IntPtr.Zero);
        return result;
    }

    public static bool AnyWindowTitle(Func<string, bool> match)
    {
        var found = false;
        var sb = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            sb.Clear(); GetWindowText(h, sb, 512);
            return !(found = match(sb.ToString()));
        }, IntPtr.Zero);
        return found;
    }

    /// Brings forward the first window owned by one of these processes.
    public static bool Focus(IReadOnlyList<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.ProcessName is "explorer" || p.MainWindowHandle == IntPtr.Zero) continue;
                return Focus(p.MainWindowHandle);
            }
            catch { }
        }
        // Windows Terminal is often not an ancestor of the shells it shows.
        foreach (var p in Process.GetProcessesByName("WindowsTerminal"))
            using (p) if (p.MainWindowHandle != IntPtr.Zero) return Focus(p.MainWindowHandle);
        return false;
    }

    static bool Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);
        return SetForegroundWindow(h);
    }
}
