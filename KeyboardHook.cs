using System;
using System.ComponentModel;
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
    private readonly Native.HookProc callback;
    private readonly Native.WinEventProc foregroundCallback;
    private nint hook, foregroundHook;
    private uint threadId;
    private Exception? failure;
    private Settings settings;
    private SwitchSession? session;
    private long nextId, heartbeat;
    private volatile bool enabled = true, disposed;
    internal bool EmergencyHotkeyAvailable { get; private set; }
    internal bool IsStopped => !worker.IsAlive;
    internal KeyboardHook(Catalog catalog, Settings settings, Action<SwitchSignal> notify, Action togglePause)
    {
        this.catalog = catalog; this.settings = settings; this.notify = notify; this.togglePause = togglePause;
        callback = Callback; foregroundCallback = (_, _, hwnd, _, _, _, _) => catalog.Record(hwnd);
        worker = new Thread(Run) { IsBackground = true, Name = "Alt Tab keyboard hook" };
        worker.SetApartmentState(ApartmentState.STA); Pulse(); worker.Start();
        if (!ready.Wait(5000)) throw new TimeoutException("キーフックを開始できませんでした。");
        if (failure != null) throw failure;
    }
    internal void Pulse()
    {
        Interlocked.Exchange(ref heartbeat, Environment.TickCount64);
        if (threadId != 0) Native.PostThreadMessage(threadId, 0x8002, 0, 0);
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
            catalog.Record(Native.GetForegroundWindow());
            EmergencyHotkeyAvailable = Native.RegisterHotKey(0, 1, 0x4003, 0x7B); // Ctrl+Alt+F12
            ready.Set();
            while (Native.GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Id == Native.WM_HOTKEY && msg.WParam == 1) togglePause();
                if (msg.Id == 0x8001) { state.Cancel(); session = null; }
                if (msg.Id == 0x8002 && !state.Active) RefreshModifiers();
                Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg);
            }
        }
        catch (Exception e) { failure = e; ready.Set(); }
        finally
        {
            if (hook != 0) Native.UnhookWindowsHookEx(hook);
            if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
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
            if ((key.Flags & 0x10) != 0) return Native.CallNextHookEx(hook, code, wp, lp);
            bool down = (int)wp is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool healthy = enabled && Environment.TickCount64 - Interlocked.Read(ref heartbeat) < 750;
            var snapshot = catalog.Snapshot;
            var config = Volatile.Read(ref settings);
            bool canBegin = snapshot.Warning == null;
            nint foreground = Native.GetForegroundWindow();
            if (!state.Active && down && key.Key == 9)
            {
                var active = snapshot.Windows.FirstOrDefault(w => w.Handle == foreground);
                // Excluded foreground apps may be absent from the candidate catalog. Store this
                // decision in the UI heartbeat instead of doing process I/O inside the callback.
                canBegin &= foreground == Volatile.Read(ref policyForeground) && !Volatile.Read(ref blockedForeground);
                if (config.ExcludeFullscreen && active?.Fullscreen == true) canBegin = false;
            }
            var decision = state.Handle((int)key.Key, down, (key.Flags & 0x20) != 0, canBegin, healthy);
            switch (decision.Gesture)
            {
                case Gesture.Begin:
                    nint monitor;
                    if (config.UsePointerMonitor && Native.GetCursorPos(out var point)) monitor = Native.MonitorFromPoint(point, 2);
                    else monitor = Native.MonitorFromWindow(foreground, 2);
                    var windows = snapshot.ForMonitor(monitor);
                    int index = Rules.InitialIndex(windows.Select(w => w.Handle).ToArray(), foreground, state.Reverse);
                    session = new(++nextId, monitor, foreground, windows, index);
                    notify(new(Gesture.Begin, session)); break;
                case Gesture.Next:
                    if (session != null) { session = session with { Index = Rules.Wrap(session.Index + (state.Reverse ? -1 : 1), session.Windows.Length) }; notify(new(Gesture.Next, session)); } break;
                case Gesture.Commit: case Gesture.Cancel:
                    notify(new(decision.Gesture, session)); session = null; break;
            }
            return decision.Consume ? 1 : Native.CallNextHookEx(hook, code, wp, lp);
        }
        catch { state.Cancel(); session = null; return Native.CallNextHookEx(hook, code, wp, lp); }
    }
    private bool blockedForeground;
    private nint policyForeground;
    internal void BlockForeground(nint foreground, bool blocked)
    {
        Volatile.Write(ref blockedForeground, blocked);
        Volatile.Write(ref policyForeground, foreground);
    }
    internal void CancelActive() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x8001, 0, 0); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; enabled = false;
        if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, 0, 0);
        worker.Join(1500); ready.Dispose();
    }
}
