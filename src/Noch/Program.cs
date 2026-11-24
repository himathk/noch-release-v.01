using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Noch.Core;
using Velopack;
using Velopack.Sources;
using Forms = System.Windows.Forms;

namespace Noch;

static class Program
{
    /// Debug copies can run beside an installed one without sharing anything.
#if DEBUG
    public static readonly string Name = Environment.GetEnvironmentVariable("NOCH_DEV") is { Length: > 0 } ? "Noch-dev" : "Noch";
#else
    public const string Name = "Noch";
#endif

    [STAThread]
    static void Main(string[] args)
    {
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ => Cleanup()).OnBeforeUpdateFastCallback(_ => AskRunningToQuit()).Run();
#if DEBUG
        if (args is ["--shots", var dir]) { Shots.Run(dir); return; }
        if (args is ["--icon", var ico]) { Shots.Icon(ico); return; }
#endif
        using var only = new Mutex(true, @"Local\" + Name, out var first);
        // Opening Noch a second time shows the one that is already running.
        if (!first) { Show.Set(); return; }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; };
        app.Startup += (_, _) => new App().Start();
        app.Run();
        only.ReleaseMutex();
    }

    static readonly EventWaitHandle Show = new(false, EventResetMode.AutoReset, @"Local\" + Name + "-show"), Leave = new(false, EventResetMode.AutoReset, @"Local\" + Name + "-quit");

    static void AskRunningToQuit()
    {
        using var only = new Mutex(false, @"Local\" + Name);
        Leave.Set();
        try { if (only.WaitOne(5000)) only.ReleaseMutex(); } catch (AbandonedMutexException) { }
        Leave.Reset();
    }

    /// Runs while uninstalling: leave the agents' config files as we found them.
    static void Cleanup()
    {
        AskRunningToQuit();
        foreach (var c in new Connector[] { new ClaudeConnector(ClaudeConnector.DefaultDir, Settings.HookExe), new CursorConnector(CursorConnector.DefaultDir, Settings.HookExe) })
            try { c.Disconnect(); } catch { }
        try { Settings.StartsWithWindows = false; } catch { }
        try { Directory.Delete(Settings.Folder, true); } catch { }
    }

    public static void Log(Exception e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            var path = Path.Combine(Settings.Folder, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 200_000) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:s} {e}\n\n");
        }
        catch { }
    }

    /// Wires everything together and owns the tray icon.
    sealed class App
    {
        readonly Settings _settings = Settings.Load();
        readonly AgentHub _hub = new();
        readonly Connector _claude, _cursor;
        HookServer? _server;
        MediaService? _media;
        NotchWindow? _window;
        Forms.NotifyIcon? _tray;
        UpdateManager? _updates;
        UpdateInfo? _ready;

        public App()
        {
            _claude = new ClaudeConnector(ClaudeConnector.DefaultDir, Settings.HookExe);
            _cursor = new CursorConnector(CursorConnector.DefaultDir, Settings.HookExe);
        }

        public void Start()
        {
            Settings.InstallHook();
            _server = new HookServer(_hub, Name == "Noch" ? null : "noch-dev");
            _server.Start();
            _media = new MediaService();
            _window = new NotchWindow(_hub, _media, _settings) { SetupItems = SetupItems };
            _window.Show();
            _media.Start();
            Tray();
            if (!_settings.Welcomed) { _settings.Welcomed = true; _settings.Save(); _window.Welcome(); }
            Listen(Show, _window.Welcome);
            Listen(Leave, Quit);
#if DEBUG
            if (Environment.GetEnvironmentVariable("NOCH_DEMO") is { Length: > 0 })
            {
                var step = 0;
                var snap = Environment.GetEnvironmentVariable("NOCH_DEMO") == "snap";
                var demo = new DispatcherTimer { Interval = TimeSpan.FromSeconds(snap ? 2 : 3.5) };
                demo.Tick += (_, _) =>
                {
                    double w = SystemParameters.WorkArea.Width, h = SystemParameters.WorkArea.Height;
                    // Hard, fast pulls off the same spot, to shake out drawing that lags behind.
                    if (snap) { _window.DemoDrag((0, w / 4, 18), (.16, w / 4, 420 + step++ % 3 * 60), (.5, w / 4, 480), (.9, w / 4, 20)); return; }
                    switch (step++)
                    {
                        case 0: _window.DemoDrag((0, w / 2, 18), (.5, w / 2, 260), (1.1, w - 300, 500), (1.4, w - 60, 520)); break;
                        case 1: _window.DemoDrag((0, w - 20, 520), (.5, w - 260, 620), (1.0, w / 2, h - 300), (1.3, w / 2 + 30, h - 60)); break;
                        case 2: _window.DemoDrag((0, w / 2, h - 18), (.4, w / 2, h - 220), (1.0, 300, 300), (1.3, 40, 30)); break;
                        case 3: _window.DemoDrag((0, 150, 18), (.4, 200, 80)); break; // not far enough: springs back
                        case 4: demo.Stop(); break;
                    }
                };
                demo.Start();
            }
#endif
            StartUpdates();
        }

        static void Listen(WaitHandle signal, Action run)
        {
            var ui = Application.Current.Dispatcher;
            ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => ui.BeginInvoke(run), null, Timeout.Infinite, false);
        }

        IReadOnlyList<SetupItem> SetupItems()
        {
            var list = new List<SetupItem>();
            if (_claude.AgentInstalled && !_claude.Connected) list.Add(new("Claude Code", "Show its work and answer it from here", "Connect", () => Toggle(_claude, "Claude Code")));
            if (_cursor.AgentInstalled && !_cursor.Connected) list.Add(new("Cursor", "Show what it is doing", "Connect", () => Toggle(_cursor, "Cursor")));
            return list;
        }

        void Toggle(Connector c, string name)
        {
            try
            {
                if (c.Connected) c.Disconnect(); else c.Connect();
                _window!.Sync();
            }
            catch (Exception e)
            {
                Log(e);
                MessageBox.Show(e.Message, "Noch could not update " + name, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void Tray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Opening += (_, e) =>
            {
                menu.Items.Clear();
                Item("Open Noch", () => _window!.Welcome());
                menu.Items.Add(new Forms.ToolStripSeparator());
                Item("Connect Claude Code", () => Toggle(_claude, "Claude Code"), _claude.Connected, _claude.AgentInstalled);
                Item("Connect Cursor", () => Toggle(_cursor, "Cursor"), _cursor.Connected, _cursor.AgentInstalled);
                menu.Items.Add(new Forms.ToolStripSeparator());
                Item("Start with Windows", () => Settings.StartsWithWindows = !Settings.StartsWithWindows, Settings.StartsWithWindows);
                Item("Show the full bar over windows", () => { _settings.AlwaysFull = !_settings.AlwaysFull; _settings.Save(); _window!.Sync(); }, _settings.AlwaysFull);
                Item("Move back to the top", () => _window!.BackToTop(), enabled: _window!.Moved);
                menu.Items.Add(new Forms.ToolStripSeparator());
                if (_ready != null) Item("Restart to update", () => _updates!.ApplyUpdatesAndRestart(_ready));
                Item("Quit", Quit);
                e.Cancel = false;
            };
            void Item(string text, Action run, bool on = false, bool enabled = true) =>
                menu.Items.Add(new Forms.ToolStripMenuItem(text, null, (_, _) => Application.Current.Dispatcher.BeginInvoke(run)) { Checked = on, Enabled = enabled });

            using var icon = Application.GetResourceStream(new Uri("pack://application:,,,/noch.ico"))!.Stream;
            _tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize), Text = "Noch", Visible = true, ContextMenuStrip = menu };
            _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _window!.Welcome(); };
        }

        void Quit()
        {
            _tray!.Visible = false; _tray.Dispose();
            _server!.Dispose();
            Application.Current.Shutdown();
        }

        void StartUpdates()
        {
            var repo = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NochRepo")?.Value;
            if (string.IsNullOrEmpty(repo)) return;
            try { _updates = new UpdateManager(new GithubSource(repo, null, false)); } catch { return; }
            if (!_updates.IsInstalled) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += (_, _) => Check();
            timer.Start();
            Check();
        }

        async void Check()
        {
            try
            {
                if (_ready != null || await _updates!.CheckForUpdatesAsync() is not { } found) return;
                await _updates.DownloadUpdatesAsync(found);
                _ready = found;
                _tray!.ShowBalloonTip(5000, "Noch " + found.TargetFullRelease.Version, "An update is ready. Right-click the tray icon to restart into it.", Forms.ToolTipIcon.None);
            }
            catch { /* offline is fine */ }
        }
    }
}
