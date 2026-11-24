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
        if (args is ["--check", var report]) { Checks.Run(report); return; }
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
        foreach (var c in new Connector[] { new ClaudeConnector(ClaudeConnector.DefaultDir, Settings.HookExe), new CursorConnector(CursorConnector.DefaultDir, Settings.HookExe), new CodexConnector(CodexConnector.DefaultDir, Settings.HookExe) })
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
    sealed class App : SettingsWindow.ISettingsActions
    {
        readonly Settings _settings = Settings.Load();
        readonly AgentHub _hub = new();
        readonly ClaudeConnector _claude;
        readonly Connector _cursor, _codex;
        AppServices _s = null!;
        SettingsWindow? _settingsWindow;
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
            _codex = new CodexConnector(CodexConnector.DefaultDir, Settings.HookExe);
        }

        public void Start()
        {
            Settings.InstallHook();
            _media = new MediaService();
            var notices = new Notices();
            var sounds = new Sounds(_settings);
            _s = new AppServices
            {
                Settings = _settings, Hub = _hub, Media = _media, Notices = notices, Sounds = sounds, Usage = new Usage(), Timer = new FocusTimer(_settings.Focus),
                Shelf = new Shelf(Path.Combine(Settings.Folder, "shelf.json")), Stats = new Stats(Path.Combine(Settings.Folder, "stats.json")),
                Calendar = new CalendarService(_settings, notices, sounds), Notifications = new NotificationService(_settings, notices), OpenSettings = OpenSettings
            };
            _server = new HookServer(_hub, Name == "Noch" ? null : "noch-dev", _s.Usage);
            _server.Start();
            Wire();
            _window = new NotchWindow(_s) { SetupItems = SetupItems };
            _window.Show();
            _s.Calendar.Start();
            _ = _s.Notifications.Start();
            _ = new PowerWatch(notices, () => _settings.PowerAlerts);
            WhatsNew();
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

        /// Turns what happens into sounds, messages and tallies.
        void Wire()
        {
            var ui = Application.Current.Dispatcher;
            _hub.Signalled += s => ui.BeginInvoke(() =>
            {
                if (s == Signal.Finished && _s.Timer.Quiet) return;
                _s.Sounds.Play(s switch { Signal.NeedsYou => Sounds.Tone.NeedsYou, Signal.Failed => Sounds.Tone.Failed, _ => Sounds.Tone.Finished });
            });
            _hub.TurnEnded += (_, t) => ui.BeginInvoke(() => _s.Stats.Turn(t, DateTime.Now));
            _hub.Answered += () => ui.BeginInvoke(() => _s.Stats.Answered(DateTime.Now));
            _hub.Collided += (path, a, b) => ui.BeginInvoke(() => _s.Notices.Post(new Notice("clash|" + path, NoticeKind.Warning, $"Two agents are editing {Path.GetFileName(path)}", a.Project == b.Project ? $"{a.Agent} and {b.Agent} in {a.Project}" : $"{a.Project} and {b.Project}")
            {
                ActionLabel = "Open", Action = () => Native.Focus(b.Pids), Life = TimeSpan.FromSeconds(12)
            }));
            _s.Timer.Finished += wasBreak =>
            {
                if (!wasBreak) _s.Stats.Focused((int)Math.Round(_s.Timer.LastLength.TotalMinutes), DateTime.Now);
                _s.Sounds.Play(Sounds.Tone.Timer);
                _s.Notices.Post(new Notice("timer", NoticeKind.Timer, wasBreak ? "Break over" : "Focus done", wasBreak ? "Ready for another?" : "Nice work.")
                {
                    ActionLabel = wasBreak ? $"Focus {_settings.FocusMinutes} min" : $"Break {_settings.BreakMinutes} min", Life = TimeSpan.FromSeconds(30),
                    Action = () => _s.Timer.Start(TimeSpan.FromMinutes(wasBreak ? _settings.FocusMinutes : _settings.BreakMinutes), !wasBreak, DateTime.UtcNow)
                });
            };
        }

        void WhatsNew()
        {
            var now = ((SettingsWindow.ISettingsActions)this).Version;
            if (_settings.LastVersion == now) return;
            var updated = _settings.LastVersion != null || _settings.Welcomed;
            _settings.LastVersion = now;
            _settings.Save();
            if (updated) _s.Notices.Post(new Notice("new", NoticeKind.Info, "Noch " + now, "Updated") { ActionLabel = Repo is null ? null : "What's new", Action = () => AppServices.Open(Repo + "/releases/latest"), Life = TimeSpan.FromSeconds(8) });
        }

        void OpenSettings()
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow(_s, this);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
            }
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }

        // ---- settings window ----

        IReadOnlyList<(string, Connector)> SettingsWindow.ISettingsActions.Agents => [("Claude Code", _claude), ("Cursor", _cursor), ("Codex", _codex)];
        ClaudeConnector SettingsWindow.ISettingsActions.Claude => _claude;
        string? SettingsWindow.ISettingsActions.Try(Action a) { try { a(); return null; } catch (Exception e) { Log(e); return e.Message; } }
        void SettingsWindow.ISettingsActions.Refresh() { _window!.Shortcut(); _window.Sync(); }
        string SettingsWindow.ISettingsActions.Version => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        public string? Repo => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NochRepo")?.Value is { Length: > 0 } r ? r : null;

        async void SettingsWindow.ISettingsActions.CheckForUpdates(Action<string> tell)
        {
            if (_updates is null) { tell("This copy was not installed from GitHub, so it cannot update itself."); return; }
            try
            {
                if (_ready is null) await Check(quiet: false);
                tell(_ready is { } r ? $"{r.TargetFullRelease.Version} is ready. Right-click the tray icon and choose Restart to update." : "You have the newest version.");
            }
            catch { tell("Could not reach GitHub."); }
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
            if (_codex.AgentInstalled && !_codex.Connected) list.Add(new("Codex", "Show its work and answer it from here", "Connect", () => Toggle(_codex, "Codex")));
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
                Item("Focus timer", () => _window!.OpenPinned(Noch.Core.Panel.Focus));
                Item("Shelf", () => _window!.OpenPinned(Noch.Core.Panel.Shelf));
                menu.Items.Add(new Forms.ToolStripSeparator());
                Item("Connect Claude Code", () => Toggle(_claude, "Claude Code"), _claude.Connected, _claude.AgentInstalled);
                Item("Connect Cursor", () => Toggle(_cursor, "Cursor"), _cursor.Connected, _cursor.AgentInstalled);
                if (_codex.AgentInstalled) Item("Connect Codex", () => Toggle(_codex, "Codex"), _codex.Connected);
                menu.Items.Add(new Forms.ToolStripSeparator());
                Item("Start with Windows", () => Settings.StartsWithWindows = !Settings.StartsWithWindows, Settings.StartsWithWindows);
                Item("Show the full bar over windows", () => { _settings.AlwaysFull = !_settings.AlwaysFull; _settings.Save(); _window!.Sync(); }, _settings.AlwaysFull);
                Item("Move back to the top", () => _window!.BackToTop(), enabled: _window!.Moved);
                Item("Settings…", OpenSettings);
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
            if (Repo is not { } repo) return;
            try { _updates = new UpdateManager(new GithubSource(repo, null, false)); } catch { return; }
            if (!_updates.IsInstalled) { _updates = null; return; }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += (_, _) => _ = Check();
            timer.Start();
            _ = Check();
        }

        async Task Check(bool quiet = true)
        {
            try
            {
                if (_ready != null || await _updates!.CheckForUpdatesAsync() is not { } found) return;
                await _updates.DownloadUpdatesAsync(found);
                _ready = found;
                _tray!.ShowBalloonTip(5000, "Noch " + found.TargetFullRelease.Version, "An update is ready. Right-click the tray icon to restart into it.", Forms.ToolTipIcon.None);
            }
            catch when (quiet) { /* offline is fine */ }
        }
    }
}
