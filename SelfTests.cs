using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MonitorAltTab;

internal static class SelfTests
{
    internal static string Output(string[] args, string fallback) { int i = Array.IndexOf(args, "--output"); return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : Path.Combine(AppContext.BaseDirectory, fallback); }
    internal static int Run(string[] args)
    {
        var results = new List<string>();
        void Assert(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); results.Add("PASS " + name); }
        try
        {
            Assert(Rules.InitialIndex(new nint[] { 1, 2, 3 }, 1, false) == 1, "MRU skips current window");
            Assert(Rules.InitialIndex(new nint[] { 1, 2, 3 }, 1, true) == 2, "reverse wraps from current");
            Assert(Rules.InitialIndex(new nint[] { 2, 3 }, 1, false) == 0, "other-monitor foreground starts at newest");
            Assert(Rules.InitialIndex(new nint[] { 2, 3 }, 1, true) == 1, "other-monitor reverse starts at oldest");
            Assert(Rules.InitialIndex(Array.Empty<nint>(), 1, false) == -1 && Rules.Wrap(0, 0) == -1, "empty monitor cannot select foreign window");
            Assert(Rules.Wrap(-1, 3) == 2 && Rules.Wrap(3, 3) == 0, "bidirectional cycling");
            Assert(!Rules.CandidateStyle(0x80, false, true), "tool windows excluded");
            Assert(Rules.CandidateStyle(0x40080, true, false), "APPWINDOW overrides tool/owner filtering");
            Assert(!Rules.CandidateStyle(0, true, false) && Rules.CandidateStyle(0, true, true), "owned dialog representative");
            Assert(Rules.Excluded("MSTSC", new[] { "mstsc.exe" }) && !Rules.Excluded("mstsc2", new[] { "mstsc.exe" }), "exact case-insensitive exclusion");
            var screenRect = new Native.Rect { Left = -1920, Top = 200, Right = 0, Bottom = 1280 };
            Assert(Rules.Fullscreen(screenRect, screenRect, false, 0x80000000), "borderless fullscreen on negative-coordinate monitor");
            Assert(!Rules.Fullscreen(screenRect, screenRect, false, 0x00C00000), "maximized titled window is not a fullscreen exclusion");
            Assert(!Rules.Fullscreen(screenRect, screenRect, true, 0), "minimized window cannot trigger fullscreen bypass");
            var k = new KeyboardState();
            Assert(!k.Handle(0xA4, true, true, true, true).Consume, "plain Alt remains available");
            Assert(k.Handle(9, true, true, true, true) == new KeyDecision(true, Gesture.Begin), "Alt Tab starts once");
            Assert(k.Handle(9, false, true, true, true).Consume, "owned Tab release suppressed");
            k.Handle(0xA0, true, true, true, true);
            Assert(k.Reverse && k.Handle(9, true, true, true, true).Gesture == Gesture.Next, "Shift reverses active session");
            Assert(k.Handle(0xA4, false, false, true, true) == new KeyDecision(false, Gesture.Commit) && !k.Active, "Alt release commits and remains balanced");
            var ctrl = new KeyboardState(); ctrl.Handle(0xA2, true, false, true, true);
            Assert(!ctrl.Handle(9, true, true, true, true).Consume, "Ctrl Alt Tab passes through");
            var altgr = new KeyboardState(); altgr.Handle(0xA2, true, false, true, true); altgr.Handle(0xA5, true, true, true, true);
            Assert(!altgr.Handle(9, true, true, true, true).Consume, "AltGr chord passes through");
            var esc = new KeyboardState(); esc.Handle(9, true, true, true, true);
            Assert(esc.Handle(27, true, true, true, true).Gesture == Gesture.Cancel && esc.Handle(27, false, true, true, true).Consume, "Escape cancels with balanced suppression");
            var unhealthy = new KeyboardState(); unhealthy.Handle(9, true, true, true, true);
            Assert(unhealthy.Handle(9, true, true, true, false).Gesture == Gesture.Cancel && !unhealthy.Active, "stalled UI cancels session");
            var bypass = new KeyboardState(); Assert(!bypass.Handle(9, true, true, false, true).Consume, "excluded foreground bypasses hook");
            Assert(!bypass.Handle(9, true, true, true, false).Consume, "unhealthy UI leaves system Alt Tab available");
            var both = new KeyboardState(); both.Handle(0xA4, true, true, true, true); both.Handle(0xA5, true, true, true, true); both.Handle(9, true, true, true, true);
            Assert(both.Handle(0xA4, false, true, true, true).Gesture == Gesture.None && both.Active, "one of two Alt releases does not commit");
            Assert(both.Handle(0xA5, false, false, true, true).Gesture == Gesture.Commit, "last Alt release commits");
            var refresh = new KeyboardState(); refresh.RefreshModifiers(false, true, false, false, false, false, false, false); refresh.Handle(9, true, true, true, true);
            Assert(refresh.Handle(0xA5, false, false, true, true).Gesture == Gesture.Commit, "starting while right Alt held does not stick");
            var stalledRelease = new KeyboardState(); stalledRelease.Handle(9, true, true, true, true);
            Assert(stalledRelease.Handle(9, false, true, true, false) == new KeyDecision(true, Gesture.Cancel), "stalled UI still consumes owned Tab release");
            var systemChord = new KeyboardState(); systemChord.Handle(9, true, true, true, true);
            Assert(systemChord.Handle(0xA2, true, true, true, true) == new KeyDecision(false, Gesture.Cancel), "Ctrl added during session cancels and passes through");
            var catalog = new CatalogSnapshot(new[] { new WindowEntry(1, 1, 11, "", "", null, false, false, 5), new WindowEntry(2, 1, 22, "", "", null, true, false, 10), new WindowEntry(3, 1, 11, "", "", null, true, false, 8) }, null);
            Assert(catalog.ForMonitor(11).Select(w => w.Handle).SequenceEqual(new nint[] { 3, 1 }), "monitor filter and MRU retain minimized windows");
            results.Add($"{results.Count} tests passed."); File.WriteAllLines(Output(args, "test-results.txt"), results); return 0;
        }
        catch (Exception e) { results.Add("FAIL " + e.Message); File.WriteAllLines(Output(args, "test-results.txt"), results); return 1; }
    }
    internal static int Diagnose(string[] args)
    {
        using var catalog = new Catalog(new Settings());
        for (int i = 0; i < 30 && catalog.Snapshot.Windows.Length == 0 && catalog.Snapshot.Warning == null; i++) Thread.Sleep(100);
        var snapshot = catalog.Snapshot;
        var screens = System.Windows.Forms.Screen.AllScreens.Select(s => new { s.DeviceName, s.Primary, Bounds = new { s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height }, WorkingArea = new { s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height } });
        File.WriteAllText(Output(args, "diagnostics.json"), JsonSerializer.Serialize(new { Os = Environment.OSVersion.VersionString, Runtime = Environment.Version.ToString(), Screens = screens, CandidateCount = snapshot.Windows.Length, MinimizedCount = snapshot.Windows.Count(w => w.Minimized), snapshot.Warning, InputDesktopAccessible = Native.InputDesktopAccessible(), NetworkFeatures = false, KeyLogging = false }, new JsonSerializerOptions { WriteIndented = true }));
        return snapshot.Warning == null ? 0 : 1;
    }
}
