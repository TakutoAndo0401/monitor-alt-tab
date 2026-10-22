using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace MonitorAltTab;

internal sealed class Catalog : IDisposable
{
    private readonly Thread worker;
    private readonly ConcurrentDictionary<nint, (uint Pid, long Stamp)> history = new();
    private readonly ConcurrentDictionary<uint, (string Name, string? Path, long Time)> names = new();
    private CatalogSnapshot snapshot = CatalogSnapshot.Empty;
    private Settings settings;
    private volatile bool stop;
    private int disposed;
    private readonly ManualResetEventSlim stopping = new(false);
    private IVirtualDesktopManager? desktops;
    internal CatalogSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal bool IsStopped => !worker.IsAlive;
    internal void UpdateSettings(Settings value) => Volatile.Write(ref settings, value);
    internal Catalog(Settings value)
    {
        settings = value;
        worker = new Thread(Run) { IsBackground = true, Name = "Window catalog" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
    }
    internal void Record(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != Environment.ProcessId) history[hwnd] = (pid, Stopwatch.GetTimestamp());
    }
    private void Run()
    {
        try { desktops = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!; } catch (COMException) { }
        try
        {
            while (!stop)
            {
                try { Volatile.Write(ref snapshot, Read()); }
                catch { Volatile.Write(ref snapshot, CatalogSnapshot.Empty with { Warning = "候補を更新できません。一時停止して再開してください。" }); }
                stopping.Wait(500);
            }
        }
        finally { if (desktops != null) Marshal.ReleaseComObject(desktops); }
    }
    private CatalogSnapshot Read()
    {
        var list = new List<WindowEntry>();
        var seen = new HashSet<nint>();
        var screens = new Dictionary<nint, Native.MonitorInfo>();
        var config = Volatile.Read(ref settings);
        string? warning = desktops == null ? "仮想デスクトップ判定が利用できないため、切り替えを停止しています。" : null;
        // Fail closed when the public virtual desktop API is unavailable.
        if (desktops == null) return new(Array.Empty<WindowEntry>(), warning);
        int rank = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            rank++;
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            string cls = Native.Class(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (desktops.IsWindowOnCurrentVirtualDesktop(hwnd, out bool current) < 0 || !current) return true;
            long style = (long)Native.GetWindowLongPtr(hwnd, -20);
            bool owner = Native.GetWindow(hwnd, 4) != 0;
            nint root = Native.GetAncestor(hwnd, 3), representative = root;
            for (int i = 0; i < 16; i++)
            {
                nint popup = Native.GetLastActivePopup(representative);
                if (popup == representative || popup == 0) break;
                representative = popup;
                if (Native.IsWindowVisible(popup)) break;
            }
            if (!Rules.CandidateStyle(style, owner, representative == hwnd)) return true;
            if ((style & 0x40000) == 0 && representative != hwnd && Native.IsWindowVisible(representative)) return true;
            string title = Native.Title(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;
            if (!names.TryGetValue(pid, out var process) || Environment.TickCount64 - process.Time > 5000)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    string? path = null; try { path = p.MainModule?.FileName; } catch { }
                    process = (p.ProcessName, path, Environment.TickCount64);
                }
                catch { process = ("アプリ", null, Environment.TickCount64); }
                names[pid] = process;
            }
            if (Rules.Excluded(process.Name, config.ExcludedApps)) return true;
            nint monitor = Native.MonitorFromWindow(hwnd, 2);
            bool minimized = Native.IsIconic(hwnd);
            if (!screens.TryGetValue(monitor, out var screen)) screens[monitor] = screen = Native.Monitor(monitor);
            Native.GetWindowRect(hwnd, out var rect);
            bool full = Rules.Fullscreen(rect, screen.Monitor, minimized, (long)Native.GetWindowLongPtr(hwnd, -16));
            long stamp = history.TryGetValue(hwnd, out var h) && h.Pid == pid ? h.Stamp : -rank;
            if (seen.Add(hwnd)) list.Add(new(hwnd, pid, monitor, title, process.Name, process.Path, minimized, full, stamp));
            return true;
        }, 0);
        foreach (nint handle in history.Keys) if (!Native.IsWindow(handle)) history.TryRemove(handle, out _);
        if (names.Count > 512) names.Clear();
        return new(list.ToArray(), warning);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop = true; stopping.Set(); if (worker.Join(1500)) stopping.Dispose();
    }
}
