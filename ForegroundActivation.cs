using System;
using System.Runtime.InteropServices;

namespace MonitorAltTab;

internal static class ForegroundActivation
{
    // A background keyboard hook does not itself receive foreground activation rights.
    // Use a no-action mouse INPUT, also used in Microsoft's PowerToys WindowHelpers,
    // only for an explicit switch request. All fields are zero: no click or movement.
    // Do not attach input queues (a hung application could hang this application's UI).
    internal static bool Request(nint target)
    {
        return RequestCore(
            () => Native.GetForegroundWindow() == target || Native.SetForegroundWindow(target),
            () => Native.SendInput(1, new[] { new Native.Input() }, Marshal.SizeOf<Native.Input>()) == 1);
    }

    internal static bool RequestCore(Func<bool> activate, Func<bool> notifyInput)
    {
        if (activate()) return true;
        return notifyInput() && activate();
    }

    internal static bool Matches(nint target, nint foreground) =>
        foreground != 0 && (target == foreground || Native.GetAncestor(target, 3) == Native.GetAncestor(foreground, 3));
}
