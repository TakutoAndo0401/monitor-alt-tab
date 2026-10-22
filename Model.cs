using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MonitorAltTab;

internal sealed class Settings
{
    public bool UsePointerMonitor { get; set; } = true;
    public bool ShowPreviews { get; set; } = true;
    public bool ExcludeFullscreen { get; set; } = true;
    public string Theme { get; set; } = "system";
    public string[] ExcludedApps { get; set; } = Array.Empty<string>();
    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    internal static Settings Load()
    {
        try { var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); s.ExcludedApps ??= Array.Empty<string>(); return s; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save()
    {
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}

internal sealed record WindowEntry(nint Handle, uint ProcessId, nint Monitor, string Title, string AppName, string? Executable, bool Minimized, bool Fullscreen, long LastUsed);
internal sealed record CatalogSnapshot(WindowEntry[] Windows, string? Warning)
{
    internal static CatalogSnapshot Empty = new(Array.Empty<WindowEntry>(), null);
    internal WindowEntry[] ForMonitor(nint monitor) => Windows.Where(w => w.Monitor == monitor).OrderByDescending(w => w.LastUsed).ToArray();
}

internal static class Rules
{
    internal static int Wrap(int value, int count) => count == 0 ? -1 : ((value % count) + count) % count;
    internal static int InitialIndex(nint[] handles, nint foreground, bool reverse)
    {
        if (handles.Length == 0) return -1;
        int current = Array.IndexOf(handles, foreground);
        return current < 0 ? (reverse ? handles.Length - 1 : 0) : Wrap(current + (reverse ? -1 : 1), handles.Length);
    }
    internal static bool CandidateStyle(long style, bool owner, bool representative)
    {
        const long Tool = 0x80, App = 0x40000, NoActivate = 0x08000000;
        if ((style & NoActivate) != 0) return false;
        if ((style & App) != 0) return true;
        return (style & Tool) == 0 && (!owner || representative);
    }
    internal static bool Excluded(string name, IEnumerable<string> exclusions) => exclusions.Any(x => string.Equals(Path.GetFileNameWithoutExtension(x.Trim()), name, StringComparison.OrdinalIgnoreCase));
    internal static bool Fullscreen(Native.Rect rect, Native.Rect monitor, bool minimized, long style) =>
        !minimized && (style & 0x00C00000) == 0 && rect.Width > 0 && rect.Height > 0 &&
        rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
}

internal enum Gesture { None, Begin, Next, Commit, Cancel, Pause }
internal readonly record struct KeyDecision(bool Consume, Gesture Gesture);

// This state machine only retains modifier states; it never retains typed text.
internal sealed class KeyboardState
{
    private bool leftAlt, rightAlt, leftCtrl, rightCtrl, leftShift, rightShift, leftWin, rightWin, tabOwned, escapeOwned;
    internal bool Active { get; private set; }
    internal bool Reverse => leftShift || rightShift;
    internal void RefreshModifiers(bool la, bool ra, bool lc, bool rc, bool ls, bool rs, bool lw, bool rw)
    { leftAlt = la; rightAlt = ra; leftCtrl = lc; rightCtrl = rc; leftShift = ls; rightShift = rs; leftWin = lw; rightWin = rw; }
    internal void Cancel() => Active = false;
    internal KeyDecision Handle(int key, bool down, bool altFlag, bool canBegin, bool healthy)
    {
        switch (key)
        {
            case 0xA4: case 0x12: leftAlt = down; break;
            case 0xA5: rightAlt = down; break;
            case 0xA2: case 0x11: leftCtrl = down; break;
            case 0xA3: rightCtrl = down; break;
            case 0xA0: case 0x10: leftShift = down; break;
            case 0xA1: rightShift = down; break;
            case 0x5B: leftWin = down; break;
            case 0x5C: rightWin = down; break;
        }
        if (!healthy && Active)
        {
            Active = false;
            bool ownedRelease = !down && ((key == 9 && tabOwned) || (key == 27 && escapeOwned));
            if (key == 9 && !down) tabOwned = false;
            if (key == 27 && !down) escapeOwned = false;
            return new(ownedRelease, Gesture.Cancel);
        }
        if (key == 9 && !down && tabOwned) { tabOwned = false; return new(true, Gesture.None); }
        if (key == 27 && !down && escapeOwned) { escapeOwned = false; return new(true, Gesture.None); }
        if (Active && key is 0x12 or 0xA4 or 0xA5 && !down && !leftAlt && !rightAlt)
        { Active = false; return new(false, Gesture.Commit); }
        if (Active && down && key == 27) { Active = false; escapeOwned = true; return new(true, Gesture.Cancel); }
        if (Active && down && key is not (9 or 0x10 or 0xA0 or 0xA1 or 0x12 or 0xA4 or 0xA5))
        { Active = false; return new(false, Gesture.Cancel); }
        if (down && key == 9 && (leftAlt || rightAlt || altFlag) && !leftCtrl && !rightCtrl && !leftWin && !rightWin)
        {
            if (!Active && (!healthy || !canBegin)) return default;
            tabOwned = true;
            if (Active) return new(true, Gesture.Next);
            Active = true; return new(true, Gesture.Begin);
        }
        return default;
    }
}
