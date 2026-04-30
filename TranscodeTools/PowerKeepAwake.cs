// ============================================================
// PowerKeepAwake.cs
// ------------------------------------------------------------
// Prevents Windows from sleeping the system or turning off the
// display while a transcode/remux run is active.
//
// Why this exists:
//   - Windows sleep is driven by idle timers, NOT by CPU/GPU
//     activity. A busy ffmpeg process does not, on its own,
//     prevent the system from going to sleep after the
//     configured idle timeout.
//   - Separately, when the display sleeps the NVIDIA driver
//     can drop the GPU into a low-power state. NVENC encode
//     sessions can stall in that state until something forces
//     the GPU back up (e.g. mouse movement re-waking display).
//
// Both problems are fixed by calling SetThreadExecutionState
// with ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
// on run start, and clearing it (ES_CONTINUOUS alone) on run
// end. This is exactly what Handbrake, OBS, and similar tools
// do for the same reason.
//
// Scope: process-wide kernel state. Acquire/Release are
// idempotent — calling Acquire twice is harmless, and Release
// without a prior Acquire is harmless too.
// ============================================================

using System.Runtime.InteropServices;

namespace TranscodeTools;

internal static class PowerKeepAwake
{
    // Flags for SetThreadExecutionState. Documented at:
    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate
    [System.Flags]
    private enum ExecutionState : uint
    {
        Continuous       = 0x80000000,
        SystemRequired   = 0x00000001,
        DisplayRequired  = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    /// <summary>
    /// Tells Windows to keep the system awake and the display on until Release()
    /// is called. Safe to call multiple times — the kernel just refreshes the
    /// state. No-op on non-Windows platforms (the app is Windows-only, but the
    /// guard keeps build/test on other platforms clean).
    /// </summary>
    public static void Acquire()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        SetThreadExecutionState(
            ExecutionState.Continuous |
            ExecutionState.SystemRequired |
            ExecutionState.DisplayRequired);
    }

    /// <summary>
    /// Restores normal idle behaviour. Windows resumes counting idle time from
    /// this moment — it does NOT immediately sleep. Safe to call without a
    /// matching Acquire().
    /// </summary>
    public static void Release()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        SetThreadExecutionState(ExecutionState.Continuous);
    }
}
