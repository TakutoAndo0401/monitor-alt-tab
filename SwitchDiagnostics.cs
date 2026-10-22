using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace MonitorAltTab;

[Flags]
internal enum SwitchBypassReason
{
    None = 0, Disabled = 1, UiUnresponsive = 2, CatalogUnavailable = 4,
    ForegroundChanged = 8, ForegroundBlocked = 16, FullscreenExcluded = 32, ExistingSystemSession = 64
}
internal enum SwitchDiagnosticEvent
{
    SystemFallback, Intercepted, InjectedBypass, CallbackError, HookInstalled, ModifierBypass, Suppressed,
    WindowsSwitchStart, WindowsSwitchEnd, WindowsObserverReady, WindowsObserverUnavailable,
    UiDisplayStart, UiDisplayReady, UiActivationRejected, UiDisplayHidden,
    Paused, Resumed, DesktopUnavailable, DesktopAvailable,
    UiLayoutReady, UiThumbnailsReady, UiRenderPass, UiActivationStart, UiActivationComplete, UiSessionCancelled,
    UiPassiveDisplay, ForegroundMovedAway, CancelledByEscape, CancelledByOtherInput
}

internal static class SwitchAvailability
{
    internal const int UiTimeoutMilliseconds = 1500;
    internal static bool IsResponsive(long age) => age >= 0 && age < UiTimeoutMilliseconds;
    internal static bool CandidateAllows(WindowEntry? candidate, uint foregroundPid, Settings config) =>
        candidate != null && candidate.ProcessId == foregroundPid &&
        !Rules.Excluded(candidate.AppName, config.ExcludedApps) && !(config.ExcludeFullscreen && candidate.Fullscreen);
    internal static SwitchBypassReason Reasons(bool enabled, long age, bool catalogReady,
        bool foregroundMatches, bool blocked, bool fullscreenBlocked)
    {
        var reason = SwitchBypassReason.None;
        if (!enabled) reason |= SwitchBypassReason.Disabled;
        if (!IsResponsive(age)) reason |= SwitchBypassReason.UiUnresponsive;
        if (!catalogReady) reason |= SwitchBypassReason.CatalogUnavailable;
        if (!foregroundMatches) reason |= SwitchBypassReason.ForegroundChanged;
        if (blocked) reason |= SwitchBypassReason.ForegroundBlocked;
        if (fullscreenBlocked) reason |= SwitchBypassReason.FullscreenExcluded;
        return reason;
    }
}

// Only diagnostic categories and heartbeat age are retained, never input contents or window identity.
// The keyboard callback only enqueues bounded data; all file access runs on a timer thread.
internal sealed class SwitchDiagnostics : IDisposable
{
    private readonly record struct Entry(DateTimeOffset Time, SwitchBypassReason Reason, long Age, SwitchDiagnosticEvent Event, long CallbackMicroseconds);
    private readonly ConcurrentQueue<Entry> pending = new();
    private readonly object gate = new();
    private readonly Timer timer;
    private readonly string path;
    private int pendingCount;
    private bool disposed;
    internal const int MaxPending = 128;
    internal const long MaxFileBytes = 256 * 1024;
    internal SwitchDiagnostics() : this(Path.Combine(AppContext.BaseDirectory, "switch-diagnostics.log")) { }
    internal SwitchDiagnostics(string path)
    {
        this.path = path;
        timer = new Timer(_ => Flush(), null, 1000, 1000);
    }
    internal void Record(SwitchBypassReason reason, long heartbeatAge, SwitchDiagnosticEvent kind = SwitchDiagnosticEvent.SystemFallback, long callbackMicroseconds = 0)
    {
        if (Interlocked.Increment(ref pendingCount) > MaxPending)
        { Interlocked.Decrement(ref pendingCount); return; }
        pending.Enqueue(new(DateTimeOffset.UtcNow, reason, heartbeatAge, kind, callbackMicroseconds));
    }
    internal void Flush()
    {
        lock (gate)
        {
            if (disposed || pending.IsEmpty) return;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes)
                    File.Move(path, path + ".previous", true);
                using var writer = File.AppendText(path);
                // A fixed batch keeps a burst of repeated key events from monopolizing this thread.
                for (int i = 0; i < MaxPending && pending.TryDequeue(out var entry); i++)
                {
                    Interlocked.Decrement(ref pendingCount);
                    writer.WriteLine($"{entry.Time:O} event={entry.Event} reason={entry.Reason} heartbeatAgeMs={entry.Age} limitMs={SwitchAvailability.UiTimeoutMilliseconds} callbackUs={entry.CallbackMicroseconds}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never interrupt switching, even in a read-only folder.
                for (int i = 0; i < MaxPending && pending.TryDequeue(out _); i++)
                    Interlocked.Decrement(ref pendingCount);
            }
        }
    }
    public void Dispose()
    {
        timer.Dispose();
        Flush();
        lock (gate) disposed = true;
    }
}
