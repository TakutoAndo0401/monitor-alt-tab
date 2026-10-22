using System;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorAltTab;

internal sealed record SwitchSession(long Id, nint Monitor, nint Original, WindowEntry[] Windows, int Index);
internal sealed record SwitchSignal(Gesture Gesture, SwitchSession? Session);

internal sealed class KeyboardHook : IDisposable
{
    private readonly Thread worker;
    private readonly Catalog catalog;
    private readonly Action<SwitchSignal> notify;
    private readonly Action togglePause;
    private readonly ManualResetEventSlim ready = new();
    private readonly KeyboardState state = new();
    private readonly SwitchDiagnostics diagnostics = new();
    private readonly Native.HookProc callback;
    private readonly Native.WinEventProc foregroundCallback;
    private readonly Native.WinEventProc switchCallback;
    private nint hook, foregroundHook, switchEventHook;
    private uint threadId;
    private Exception? failure;
    private Settings settings;
    private SwitchSession? session;
    private sealed record PendingGesture(Gesture Gesture, nint Foreground, nint Monitor, bool Reverse, CatalogSnapshot Snapshot);
    private readonly ConcurrentQueue<PendingGesture> pendingGestures = new();
    private long nextId, heartbeat, lastModifierRefresh;
    private volatile bool enabled = true, disposed;
    internal bool EmergencyHotkeyAvailable { get; private set; }
    internal bool IsStopped => !worker.IsAlive;
    internal KeyboardHook(Catalog catalog, Settings settings, Action<SwitchSignal> notify, Action togglePause)
    {
        this.catalog = catalog; this.settings = settings; this.notify = notify; this.togglePause = togglePause;
        callback = Callback; foregroundCallback = (_, _, hwnd, _, _, _, _) => catalog.Record(hwnd);
        switchCallback = (_, evt, _, _, _, _, _) => Trace(evt == 0x14 ? SwitchDiagnosticEvent.WindowsSwitchStart : SwitchDiagnosticEvent.WindowsSwitchEnd);
        worker = new Thread(Run) { IsBackground = true, Name = "Alt Tab keyboard hook" };
        worker.SetApartmentState(ApartmentState.STA); Pulse(); worker.Start();
        if (!ready.Wait(5000)) throw new TimeoutException("キーフックを開始できませんでした。");
        if (failure != null) throw failure;
        diagnostics.Record(SwitchBypassReason.None, 0, SwitchDiagnosticEvent.HookInstalled);
    }
    internal void Pulse()
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref heartbeat, now);
        if (threadId != 0 && now - lastModifierRefresh >= 250)
        { lastModifierRefresh = now; Native.PostThreadMessage(threadId, 0x8002, 0, 0); }
    }
    internal void SetEnabled(bool value) => enabled = value;
    internal void UpdateSettings(Settings value) => Volatile.Write(ref settings, value);
    private void Run()
    {
        threadId = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, 0, 0, 0, 0);
        try
        {
            RefreshModifiers();
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, callback, Native.GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            foregroundHook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 2);
            // Observe documented shell switch events; do not alter or dismiss system windows.
            switchEventHook = Native.SetWinEventHook(0x14, 0x15, 0, switchCallback, 0, 0, 0);
            Trace(switchEventHook != 0 ? SwitchDiagnosticEvent.WindowsObserverReady : SwitchDiagnosticEvent.WindowsObserverUnavailable);
            catalog.Record(Native.GetForegroundWindow());
            EmergencyHotkeyAvailable = Native.RegisterHotKey(0, 1, 0x4003, 0x7B); // Ctrl+Alt+F12
            ready.Set();
            while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Id == Native.WM_HOTKEY && msg.WParam == 1) togglePause();
                if (msg.Id == 0x8001) { state.Cancel(); session = null; }
                if (msg.Id == 0x8002 && !state.Active) RefreshModifiers();
                if (msg.Id == 0x8003) DispatchPendingGesture();
                Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg);
            }
        }
        catch (Exception e) { failure = e; ready.Set(); }
        finally
        {
            if (hook != 0) Native.UnhookWindowsHookEx(hook);
            if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
            if (switchEventHook != 0) Native.UnhookWinEvent(switchEventHook);
            Native.UnregisterHotKey(0, 1);
        }
    }
    private static bool Down(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;
    private void RefreshModifiers() => state.RefreshModifiers(Down(0xA4), Down(0xA5), Down(0xA2), Down(0xA3), Down(0xA0), Down(0xA1), Down(0x5B), Down(0x5C));
    private nint Callback(int code, nint wp, nint lp)
    {
        if (code < 0) return Native.CallNextHookEx(hook, code, wp, lp);
        try
        {
            var key = Marshal.PtrToStructure<Native.KeyData>(lp);
            bool down = (int)wp is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            if ((key.Flags & 0x10) != 0)
            {
                if (down && key.Key == 9 && state.IsSwitchChord((key.Flags & 0x20) != 0))
                    diagnostics.Record(SwitchBypassReason.None, Environment.TickCount64 - Interlocked.Read(ref heartbeat), SwitchDiagnosticEvent.InjectedBypass);
                return Native.CallNextHookEx(hook, code, wp, lp);
            }
            // Idle typing needs only modifier bookkeeping and balanced owned-key release.
            // Do not query foreground, catalog, or settings for ordinary keyboard events.
            if (!state.Active && !(down && key.Key == 9 && state.IsSwitchChord((key.Flags & 0x20) != 0)))
            {
                if (down && key.Key == 9 && (key.Flags & 0x20) != 0)
                    Trace(SwitchDiagnosticEvent.ModifierBypass);
                var idle = state.Handle((int)key.Key, down, (key.Flags & 0x20) != 0, false, true);
                return idle.Consume ? 1 : Native.CallNextHookEx(hook, code, wp, lp);
            }
            long callbackStarted = Stopwatch.GetTimestamp();
            long heartbeatAge = Environment.TickCount64 - Interlocked.Read(ref heartbeat);
            bool healthy = enabled && SwitchAvailability.IsResponsive(heartbeatAge);
            var snapshot = catalog.Snapshot;
            var config = Volatile.Read(ref settings);
            bool canBegin = snapshot.Warning == null;
            nint foreground = !state.Active ? Native.GetForegroundWindow() : 0;
            bool foregroundMatches = true, blocked = false, fullscreenBlocked = false;
            if (!state.Active && down && key.Key == 9)
            {
                var active = snapshot.Windows.FirstOrDefault(w => w.Handle == foreground);
                // Excluded foreground apps may be absent from the candidate catalog. Store this
                // decision in the UI heartbeat instead of doing process I/O inside the callback.
                var policy = Volatile.Read(ref foregroundPolicy);
                foregroundMatches = foreground == policy.Foreground;
                blocked = foregroundMatches && policy.Blocked;
                fullscreenBlocked = config.ExcludeFullscreen && active?.Fullscreen == true;
                // A recently focused, already enumerated window need not wait for the next UI tick.
                // Validate its PID to avoid trusting a recycled window handle.
                if (!foregroundMatches && active != null)
                {
                    Native.GetWindowThreadProcessId(foreground, out uint pid);
                    foregroundMatches = SwitchAvailability.CandidateAllows(active, pid, config);
                }
                canBegin &= foregroundMatches && !blocked && !fullscreenBlocked;
            }
            bool switchChord = down && key.Key == 9 && state.IsSwitchChord((key.Flags & 0x20) != 0);
            if (!switchChord && down && key.Key == 9 && (key.Flags & 0x20) != 0)
                diagnostics.Record(SwitchBypassReason.None, heartbeatAge, SwitchDiagnosticEvent.ModifierBypass);
            bool existingSystemSession = state.IsSystemSession;
            var decision = state.Handle((int)key.Key, down, (key.Flags & 0x20) != 0, canBegin, healthy);
            if (decision.Gesture == Gesture.Cancel)
                diagnostics.Record(healthy ? SwitchBypassReason.None : SwitchBypassReason.UiUnresponsive, heartbeatAge,
                    key.Key == 27 ? SwitchDiagnosticEvent.CancelledByEscape : SwitchDiagnosticEvent.CancelledByOtherInput);
            if (switchChord && !decision.Consume)
                diagnostics.Record(SwitchAvailability.Reasons(enabled, heartbeatAge, snapshot.Warning == null,
                    foregroundMatches, blocked, fullscreenBlocked) | (existingSystemSession ? SwitchBypassReason.ExistingSystemSession : SwitchBypassReason.None), heartbeatAge);
            if (decision.Gesture != Gesture.None)
            {
                nint monitor = 0;
                if (decision.Gesture == Gesture.Begin)
                {
                    if (config.UsePointerMonitor && Native.GetCursorPos(out var point)) monitor = Native.MonitorFromPoint(point, 2);
                    else monitor = Native.MonitorFromWindow(foreground, 2);
                }
                // Posting to this hook's own message loop guarantees UI notification begins only
                // after the callback returns. Do not invoke the UI dispatcher inside this callback.
                pendingGestures.Enqueue(new(decision.Gesture, foreground, monitor, state.Reverse, snapshot));
                if (!Native.PostThreadMessage(threadId, 0x8003, 0, 0))
                {
                    while (pendingGestures.TryDequeue(out _)) { }
                    state.Cancel(); session = null;
                    diagnostics.Record(SwitchBypassReason.None, heartbeatAge, SwitchDiagnosticEvent.CallbackError);
                }
            }
            if (decision.Consume)
                diagnostics.Record(SwitchBypassReason.None, heartbeatAge,
                    decision.Gesture == Gesture.Begin ? SwitchDiagnosticEvent.Intercepted : SwitchDiagnosticEvent.Suppressed,
                    (long)(Stopwatch.GetElapsedTime(callbackStarted).TotalMilliseconds * 1000));
            return decision.Consume ? 1 : Native.CallNextHookEx(hook, code, wp, lp);
        }
        catch { diagnostics.Record(SwitchBypassReason.None, Environment.TickCount64 - Interlocked.Read(ref heartbeat), SwitchDiagnosticEvent.CallbackError); state.Cancel(); session = null; return Native.CallNextHookEx(hook, code, wp, lp); }
    }
    private void DispatchPendingGesture()
    {
        if (!pendingGestures.TryDequeue(out var value)) return;
        switch (value.Gesture)
        {
            case Gesture.Begin:
                var windows = value.Snapshot.ForMonitor(value.Monitor);
                int index = Rules.InitialIndex(windows.Select(w => w.Handle).ToArray(), value.Foreground, value.Reverse);
                session = new(++nextId, value.Monitor, value.Foreground, windows, index);
                notify(new(Gesture.Begin, session)); break;
            case Gesture.Next:
                if (session != null) { session = session with { Index = Rules.Wrap(session.Index + (value.Reverse ? -1 : 1), session.Windows.Length) }; notify(new(Gesture.Next, session)); } break;
            case Gesture.Commit: case Gesture.Cancel:
                notify(new(value.Gesture, session)); session = null; break;
        }
    }
    private sealed record ForegroundPolicy(nint Foreground, bool Blocked);
    private ForegroundPolicy foregroundPolicy = new(0, false);
    internal void BlockForeground(nint foreground, bool blocked)
    {
        var previous = Volatile.Read(ref foregroundPolicy);
        if (previous.Foreground != foreground || previous.Blocked != blocked)
            Volatile.Write(ref foregroundPolicy, new ForegroundPolicy(foreground, blocked));
    }
    internal void Trace(SwitchDiagnosticEvent kind) => diagnostics.Record(SwitchBypassReason.None,
        Environment.TickCount64 - Interlocked.Read(ref heartbeat), kind);
    internal void CancelActive() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x8001, 0, 0); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; enabled = false;
        if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, 0, 0);
        worker.Join(1500); diagnostics.Dispose(); ready.Dispose();
    }
}
