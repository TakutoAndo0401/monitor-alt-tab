using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MonitorAltTab;

internal static class ActivationTests
{
    internal static int Fixture(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window { Title = "Monitor Alt Tab — 自動検証専用", Width = 400, Height = 180, ShowActivated = false, Content = new TextBlock { Text = "前面への切り替えと最小化復元を検証しています。\nこのウィンドウは自動で閉じます。", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap } };
        app.Startup += (_, _) =>
        {
            app.MainWindow = window; window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            File.WriteAllText(SelfTests.Output(args, "fixture.json"), JsonSerializer.Serialize((long)hwnd));
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timeout.Tick += (_, _) => { timeout.Stop(); window.Close(); }; timeout.Start();
        };
        return app.Run();
    }

    internal static int Run(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        string report = SelfTests.Output(args, "activation-results.txt");
        string marker = Path.Combine(Path.GetDirectoryName(report)!, "fixture-" + Guid.NewGuid().ToString("N") + ".json");
        var results = new List<string>();
        Process? fixture = null;
        SwitcherWindow? overlay = null;
        nint target = 0, original = Native.GetForegroundWindow();
        int stage = 0;
        long started = Environment.TickCount64, deadline = started;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        void Finish(bool pass)
        {
            timer.Stop();
            // Restore focus only if it is still in our own disposable test window.
            if (target != 0 && Native.GetForegroundWindow() == target && Native.IsWindow(original)) ForegroundActivation.Request(original);
            if (target != 0) Native.PostMessage(target, 0x10, 0, 0);
            overlay?.Close();
            if (fixture != null && !fixture.WaitForExit(1000)) results.Add("Fixture will close automatically within 15 seconds.");
            fixture?.Dispose();
            if (File.Exists(marker)) File.Delete(marker);
            results.Add(pass ? "PASS activation integration test" : "FAIL activation integration test");
            File.WriteAllLines(report, results); app.Shutdown(pass ? 0 : 1);
        }
        app.Startup += (_, _) =>
        {
            try
            {
                string executable = Environment.ProcessPath!;
                string prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    ? "\"" + Path.GetFullPath(Environment.GetCommandLineArgs()[0]) + "\" " : "";
                fixture = Process.Start(new ProcessStartInfo(executable, prefix + "--activation-fixture --output \"" + marker + "\"") { UseShellExecute = false, CreateNoWindow = true });
                timer.Start();
            }
            catch (Exception e) { results.Add(e.GetType().Name); Finish(false); }
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (Environment.TickCount64 - started > 10000) { results.Add("FAIL timeout"); Finish(false); return; }
                if (stage == 0)
                {
                    if (!File.Exists(marker)) return;
                    try { target = (nint)JsonSerializer.Deserialize<long>(File.ReadAllText(marker)); } catch (IOException) { return; } catch (JsonException) { return; }
                    if (!Native.IsWindow(target)) return;
                    bool direct = Native.SetForegroundWindow(target);
                    results.Add("Initial SetForegroundWindow result=" + direct);
                    bool requested = ForegroundActivation.Request(target);
                    results.Add("ForegroundActivation.Request=" + requested);
                    deadline = Environment.TickCount64 + 400; stage = 1;
                }
                else if (stage == 1 && Environment.TickCount64 >= deadline)
                {
                    bool foreground = ForegroundActivation.Matches(target, Native.GetForegroundWindow());
                    results.Add((foreground ? "PASS" : "FAIL") + " actual foreground of separate-process fixture");
                    if (!foreground) { Finish(false); return; }
                    Native.ShowWindowAsync(target, 6); deadline = Environment.TickCount64 + 250; stage = 2;
                }
                else if (stage == 2 && Environment.TickCount64 >= deadline)
                {
                    bool minimized = Native.IsIconic(target);
                    results.Add((minimized ? "PASS" : "FAIL") + " fixture minimized");
                    if (!minimized) { Finish(false); return; }
                    Native.ShowWindowAsync(target, 9);
                    ForegroundActivation.Request(target);
                    deadline = Environment.TickCount64 + 400; stage = 3;
                }
                else if (stage == 3 && Environment.TickCount64 >= deadline)
                {
                    bool restored = !Native.IsIconic(target) && ForegroundActivation.Matches(target, Native.GetForegroundWindow());
                    results.Add((restored ? "PASS" : "FAIL") + " minimized fixture restored to actual foreground");
                    if (!restored) { Finish(false); return; }
                    var config = new Settings { ShowPreviews = false };
                    overlay = new SwitcherWindow(config);
                    nint monitor = Native.MonitorFromWindow(target, 2);
                    Native.GetWindowThreadProcessId(target, out uint pid);
                    var entry = new WindowEntry(target, pid, monitor, "テスト対象", "テスト専用", null, false, false, 0);
                    bool activated = overlay.Present(new(1, monitor, target, new[] { entry }, 0), config);
                    results.Add("Switcher foreground request=" + activated);
                    deadline = Environment.TickCount64 + 200; stage = 4;
                }
                else if (stage == 4 && Environment.TickCount64 >= deadline)
                {
                    bool owns = Native.GetForegroundWindow() == overlay!.Handle;
                    results.Add((owns ? "PASS" : "FAIL") + " switcher owns foreground during selection");
                    if (!owns) { Finish(false); return; }
                    ForegroundActivation.Request(target); overlay.Hide();
                    deadline = Environment.TickCount64 + 300; stage = 5;
                }
                else if (stage == 5 && Environment.TickCount64 >= deadline)
                {
                    bool transferred = ForegroundActivation.Matches(target, Native.GetForegroundWindow());
                    results.Add((transferred ? "PASS" : "FAIL") + " switcher transfers foreground before hiding"); Finish(transferred);
                }
            }
            catch (Exception e) { results.Add(e.GetType().Name); Finish(false); }
        };
        return app.Run();
    }
}
