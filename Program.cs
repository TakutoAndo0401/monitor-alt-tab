using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MonitorAltTab;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.Run(args);
        if (args.Contains("--diagnose")) return SelfTests.Diagnose(args);
        if (args.Contains("--activation-test")) return ActivationTests.Run(args);
        if (args.Contains("--activation-fixture")) return ActivationTests.Fixture(args);
        if (args.Contains("--render-preview")) return RenderPreview(args);
        using var mutex = new Mutex(true, @"Local\MonitorAltTab.68D2D7F1", out bool first);
        if (!first)
        {
            MessageBox.Show("Monitor Alt Tabは既に起動しています。タスクトレイのアイコンから設定を開けます。", "Monitor Alt Tab"); return 0;
        }
        var app = new SwitcherApp(args.Contains("--smoke-test"));
        try { return app.Run(); }
        catch (Exception ex)
        {
            MessageBox.Show("アプリを終了しました。標準のAlt＋Tabに戻ります。\n" + ex.GetType().Name, "Monitor Alt Tab", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
        }
        finally { app.Cleanup(); }
    }
    private static int RenderPreview(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var config = new Settings { Theme = args.Contains("--light") ? "light" : "dark" };
        var window = new SwitcherWindow(config);
        Native.GetCursorPos(out var point); nint monitor = Native.MonitorFromPoint(point, 2);
        var entries = new[] { new WindowEntry(0, 0, monitor, "プロジェクトの資料", "Edge", null, false, false, 3), new WindowEntry(0, 0, monitor, "Monitor Alt Tab", "Visual Studio", null, false, false, 2), new WindowEntry(0, 0, monitor, "ドキュメント", "Explorer", null, true, false, 1), new WindowEntry(0, 0, monitor, "今日のメモ", "メモ帳", null, false, false, 0) };
        app.Startup += (_, _) =>
        {
            window.Present(new(1, monitor, 0, entries, 1), config, true);
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                window.SavePreview(SelfTests.Output(args, "preview.png")); window.Close(); app.Shutdown();
            });
        };
        return app.Run();
    }
}

internal sealed class SwitcherApp : Application
{
    private Settings settings = Settings.Load();
    private Catalog? catalog;
    private KeyboardHook? keyboard;
    private SwitcherWindow? overlay;
    private SettingsWindow? settingsWindow;
    private Forms.NotifyIcon? tray;
    private Forms.ToolStripMenuItem? pauseItem;
    private DispatcherTimer? timer;
    private SwitchSession? active;
    private bool paused, cleaned, desktopUnavailable;
    private long dismissedId;
    private nint lastForeground;
    private string lastForegroundName = "";
    private readonly bool smoke;
    internal SwitcherApp(bool smoke) { this.smoke = smoke; ShutdownMode = ShutdownMode.OnExplicitShutdown; Startup += Start; }
    private void Start(object sender, StartupEventArgs e)
    {
        catalog = new Catalog(settings);
        overlay = new SwitcherWindow(settings);
        overlay.Clicked = entry => { if (active is { } a) { dismissedId = a.Id; keyboard?.CancelActive(); active = null; try { Activate(entry, a); } finally { overlay.Hide(); } } };
        keyboard = new KeyboardHook(catalog, settings, signal => Dispatcher.BeginInvoke(() => Handle(signal)), () => Dispatcher.BeginInvoke(TogglePause));
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("設定", null, (_, _) => Dispatcher.BeginInvoke(OpenSettings));
        pauseItem = new Forms.ToolStripMenuItem("一時停止  Ctrl+Alt+F12", null, (_, _) => Dispatcher.BeginInvoke(TogglePause)); menu.Items.Add(pauseItem);
        menu.Items.Add("終了", null, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Monitor Alt Tab — 有効", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(OpenSettings);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; timer.Tick += Tick; timer.Start();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        OpenSettings();
        if (smoke)
        {
            var finish = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            finish.Tick += (_, _) =>
            {
                finish.Stop(); Cleanup();
                bool pass = keyboard.IsStopped && catalog.IsStopped;
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.txt"), $"{(pass ? "PASS" : "FAIL")}: startup, keyboard hook, tray, settings; keyboardStopped={keyboard.IsStopped}; catalogStopped={catalog.IsStopped}; emergencyHotkey={keyboard.EmergencyHotkeyAvailable}; windows={catalog.Snapshot.Windows.Length}; warning={catalog.Snapshot.Warning ?? "none"}"); Shutdown(pass ? 0 : 1);
            }; finish.Start();
        }
    }
    private void Tick(object? sender, EventArgs e)
    {
        if (keyboard == null) return;
        bool available = Native.InputDesktopAccessible();
        if (desktopUnavailable != !available)
        {
            desktopUnavailable = !available; Cancel(); keyboard.SetEnabled(!paused && !desktopUnavailable);
        }
        var foreground = Native.GetForegroundWindow();
        if (foreground != lastForeground)
        {
            lastForeground = foreground;
            try { Native.GetWindowThreadProcessId(foreground, out uint pid); using var p = Process.GetProcessById((int)pid); lastForegroundName = p.ProcessName; } catch { lastForegroundName = ""; }
        }
        bool blocked = Rules.Excluded(lastForegroundName, settings.ExcludedApps);
        if (settings.ExcludeFullscreen && foreground != 0 && !Native.IsIconic(foreground))
        {
            var info = Native.Monitor(Native.MonitorFromWindow(foreground, 2));
            if (Native.GetWindowRect(foreground, out var rect) && rect.Width > 0 && rect.Height > 0)
                blocked |= Rules.Fullscreen(rect, info.Monitor, false, (long)Native.GetWindowLongPtr(foreground, -16));
        }
        keyboard.BlockForeground(foreground, blocked);
        if (active is { } a && foreground != a.Original && foreground != overlay?.Handle && foreground != 0) Cancel();
        keyboard.Pulse();
    }
    private void Handle(SwitchSignal signal)
    {
        var value = signal.Session;
        if (value == null || value.Id <= dismissedId) return;
        if (paused || desktopUnavailable) { dismissedId = value.Id; return; }
        switch (signal.Gesture)
        {
            case Gesture.Begin:
                active = value;
                if (!overlay!.Present(value, settings)) { Cancel(); Notify("切り替え画面をアクティブにできませんでした。一度終了して起動し直してください。"); }
                break;
            case Gesture.Next:
                if (active?.Id == value.Id) { active = value; overlay!.Select(value.Index); } break;
            case Gesture.Cancel: Cancel(); break;
            case Gesture.Commit:
                if (active?.Id != value.Id) return;
                active = null; dismissedId = value.Id;
                try { if (value.Index >= 0 && value.Index < value.Windows.Length) Activate(value.Windows[value.Index], value); }
                finally { overlay!.Hide(); }
                break;
        }
    }
    private void Activate(WindowEntry entry, SwitchSession session)
    {
        if (!Native.IsWindow(entry.Handle)) { Notify("対象のウィンドウは既に閉じられています。"); return; }
        Native.GetWindowThreadProcessId(entry.Handle, out uint pid);
        if (pid != entry.ProcessId || Native.MonitorFromWindow(entry.Handle, 2) != session.Monitor) { Notify("ウィンドウの状態が変わったため、切り替えを中止しました。"); return; }
        IVirtualDesktopManager? desktops = null;
        try
        {
            desktops = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!;
            if (desktops.IsWindowOnCurrentVirtualDesktop(entry.Handle, out bool current) < 0 || !current) { Notify("現在の仮想デスクトップにないため、切り替えを中止しました。"); return; }
        }
        catch (COMException) { Notify("仮想デスクトップを確認できませんでした。"); return; }
        finally { if (desktops != null) Marshal.ReleaseComObject(desktops); }
        // Revalidate immediately before making the user-requested foreground transfer.
        // A different foreground means the user has already moved on to another app.
        nint before = Native.GetForegroundWindow();
        if (before != 0 && before != session.Original && before != entry.Handle && before != overlay?.Handle) return;
        if (Native.IsIconic(entry.Handle)) Native.ShowWindowAsync(entry.Handle, 9);
        bool requested = ForegroundActivation.Request(entry.Handle);
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (active != null || dismissedId != session.Id) return;
            nint actual = Native.GetForegroundWindow();
            if (!ForegroundActivation.Matches(entry.Handle, actual))
                Notify(requested ? "切り替えを確認できませんでした。対象をタスクバーから選択してください。" : "Windowsが前面への切り替えを許可しませんでした。対象をタスクバーから選択してください。");
        }; check.Start();
    }
    private void Notify(string message) { if (tray == null) return; tray.BalloonTipTitle = "Monitor Alt Tab"; tray.BalloonTipText = message; tray.ShowBalloonTip(4000); }
    private void Cancel()
    {
        if (active is { } previous)
        {
            dismissedId = Math.Max(dismissedId, previous.Id);
            if (Native.GetForegroundWindow() == overlay?.Handle && Native.IsWindow(previous.Original))
                ForegroundActivation.Request(previous.Original);
        }
        active = null; overlay?.Hide(); keyboard?.CancelActive();
    }
    private void TogglePause()
    {
        paused = !paused; Cancel(); keyboard?.SetEnabled(!paused && !desktopUnavailable);
        if (pauseItem != null) pauseItem.Text = paused ? "再開  Ctrl+Alt+F12" : "一時停止  Ctrl+Alt+F12";
        if (tray != null) tray.Text = paused ? "Monitor Alt Tab — 一時停止中" : "Monitor Alt Tab — 有効";
        if (settingsWindow != null) settingsWindow.Title = paused ? "Monitor Alt Tab — 一時停止中" : "Monitor Alt Tab — 設定";
    }
    private void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        string status = paused ? "一時停止中：標準のAlt＋Tabを使用しています。" : "有効：このモニターのウィンドウをAlt＋Tabで切り替えます。";
        if (keyboard?.EmergencyHotkeyAvailable == false) status += "\n緊急停止キーが他のアプリと競合しています。トレイから停止できます。";
        if (catalog?.Snapshot.Warning is string warning) status += "\n" + warning;
        settingsWindow = new SettingsWindow(settings, next => { settings = next; catalog?.UpdateSettings(next); keyboard?.UpdateSettings(next); Cancel(); }, Preview, TogglePause, () => Shutdown(), status);
        settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show();
    }
    private void Preview()
    {
        Cancel(); Native.GetCursorPos(out var point); nint monitor = Native.MonitorFromPoint(point, 2);
        var windows = catalog!.Snapshot.ForMonitor(monitor);
        overlay!.Present(new(long.MaxValue, monitor, Native.GetForegroundWindow(), windows, windows.Length == 0 ? -1 : 0), settings);
        var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; hide.Tick += (_, _) => { hide.Stop(); if (active == null) overlay.Hide(); }; hide.Start();
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Cancel);
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => Dispatcher.BeginInvoke(() => { Cancel(); desktopUnavailable = e.Reason == SessionSwitchReason.SessionLock; keyboard?.SetEnabled(!paused && !desktopUnavailable); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(Cancel);
    protected override void OnExit(ExitEventArgs e) { Cleanup(); base.OnExit(e); }
    internal void Cleanup()
    {
        if (cleaned) return; cleaned = true;
        timer?.Stop(); keyboard?.Dispose(); catalog?.Dispose();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.SessionSwitch -= SessionChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        overlay?.Close();
    }
}
