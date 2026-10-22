using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
            Assert(SwitchAvailability.IsResponsive(750) && SwitchAvailability.IsResponsive(1499), "temporary UI stalls up to 1499ms remain responsive");
            Assert(!SwitchAvailability.IsResponsive(1500) && !SwitchAvailability.IsResponsive(2000), "1500ms UI timeout still falls back safely");
            Assert(SwitchAvailability.Reasons(true, 1600, true, false, false, false) ==
                (SwitchBypassReason.UiUnresponsive | SwitchBypassReason.ForegroundChanged), "fallback records simultaneous timeout and foreground mismatch");
            Assert(SwitchAvailability.Reasons(false, 100, false, true, true, true) ==
                (SwitchBypassReason.Disabled | SwitchBypassReason.CatalogUnavailable | SwitchBypassReason.ForegroundBlocked | SwitchBypassReason.FullscreenExcluded), "fallback distinguishes non-timeout reasons");
            string logPath = Path.Combine(Path.GetDirectoryName(Output(args, "test-results.txt"))!, "switch-log-test-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using (var log = new SwitchDiagnostics(logPath))
                {
                    for (int i = 0; i < 1000; i++) log.Record(SwitchBypassReason.ForegroundChanged, 42);
                    log.Flush();
                    var lines = File.ReadAllLines(logPath);
                    Assert(lines.Length == SwitchDiagnostics.MaxPending && lines.All(x => x.Contains("reason=ForegroundChanged heartbeatAgeMs=42 limitMs=1500")), "fallback logging bounds queued events and records only fixed diagnostic fields");
                    File.WriteAllText(logPath, new string('x', (int)SwitchDiagnostics.MaxFileBytes));
                    log.Record(SwitchBypassReason.UiUnresponsive, 1500); log.Flush();
                    Assert(new FileInfo(logPath + ".previous").Length == SwitchDiagnostics.MaxFileBytes && File.ReadAllLines(logPath).Length == 1, "fallback log rotates with one previous file");
                }
                using var unavailable = new SwitchDiagnostics(Path.Combine(logPath, "missing", "diagnostics.log"));
                unavailable.Record(SwitchBypassReason.Disabled, 0); unavailable.Flush();
                Assert(true, "unwritable diagnostic destination does not interrupt switching");
            }
            finally { File.Delete(logPath); File.Delete(logPath + ".previous"); }
            Assert(Marshal.SizeOf<Native.Input>() == 40, "native INPUT has correct x64 ABI size");
            var noOpInput = new Native.Input();
            Assert(noOpInput.Type == 0 && noOpInput.Data.Mouse.Flags == 0 && noOpInput.Data.Mouse.X == 0 && noOpInput.Data.Mouse.Y == 0 && noOpInput.Data.Mouse.Data == 0, "foreground fallback input has no click or movement");
            int attempts = 0, notifications = 0;
            Assert(ForegroundActivation.RequestCore(() => { attempts++; return true; }, () => { notifications++; return true; }) && attempts == 1 && notifications == 0, "foreground success does not inject input");
            attempts = notifications = 0;
            Assert(ForegroundActivation.RequestCore(() => ++attempts == 2, () => { notifications++; return true; }) && attempts == 2 && notifications == 1, "foreground denial retries once after no-action input");
            attempts = notifications = 0;
            Assert(!ForegroundActivation.RequestCore(() => { attempts++; return false; }, () => { notifications++; return false; }) && attempts == 1 && notifications == 1, "blocked input does not retry activation");
            attempts = notifications = 0;
            Assert(!ForegroundActivation.RequestCore(() => { attempts++; return false; }, () => { notifications++; return true; }) && attempts == 2 && notifications == 1, "persistent denial stops after one retry");
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
            var focusRace = new KeyboardState(); focusRace.Handle(0xA4, true, true, true, true);
            Assert(!focusRace.Handle(9, true, true, false, true).Consume &&
                !focusRace.Handle(9, true, true, true, true).Consume && !focusRace.Active, "recovered foreground policy never takes over Windows key repeat");
            focusRace.Handle(9, false, true, true, true);
            Assert(!focusRace.Handle(9, true, true, true, true).Consume, "Windows owns subsequent Tab presses until Alt is released");
            focusRace.Handle(9, false, true, true, true); focusRace.Handle(0xA4, false, false, true, true);
            focusRace.Handle(0xA4, true, true, true, true);
            Assert(focusRace.Handle(9, true, true, true, true).Gesture == Gesture.Begin, "fresh Alt chord can use custom switcher again");
            var candidate = new WindowEntry(1, 123, 1, "", "Game", null, false, true, 1);
            Assert(Rules.SameWindowCards(new[] { candidate }, new[] { candidate with { LastUsed = 999 } }), "MRU timestamp alone does not rebuild unchanged cards");
            Assert(!Rules.SameWindowCards(new[] { candidate }, new[] { candidate with { Title = "updated" } }) &&
                !Rules.SameWindowCards(new[] { candidate }, new[] { candidate with { ProcessId = 456 } }), "title change or recycled HWND refreshes card content");
            Assert(!Rules.SameWindowCards(new[] { candidate }, new[] { candidate with { Minimized = true } }), "minimized preview placeholder is refreshed");
            var secondCandidate = candidate with { Handle = 2 };
            Assert(!Rules.SameWindowCards(new[] { candidate, secondCandidate }, new[] { secondCandidate, candidate }), "candidate order changes rebuild cards in the correct order");
            Assert(SwitchAvailability.CandidateAllows(candidate, 123, new Settings { ExcludeFullscreen = false }), "recently focused known window can bypass stale UI foreground policy");
            Assert(!SwitchAvailability.CandidateAllows(candidate, 456, new Settings { ExcludeFullscreen = false }), "recycled foreground HWND is not trusted");
            Assert(!SwitchAvailability.CandidateAllows(candidate, 123, new Settings { ExcludeFullscreen = false, ExcludedApps = new[] { "game.exe" } }), "fresh exclusion still blocks cached candidate");
            Assert(!SwitchAvailability.CandidateAllows(candidate, 123, new Settings()) &&
                !SwitchAvailability.CandidateAllows(null, 123, new Settings()), "fullscreen exclusion and unknown foreground remain protected");
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
