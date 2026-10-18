using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LightBulb.PlatformInterop;

namespace LightBulb.Services;

public static class InvocationPreventionChecker
{
    public const string DiagnosticArgument = "--check-invocation-prevention";

    private const uint SpiGetScreenSaverRunning = 0x0072;
    private const int SystemExecutionStateInformationLevel = 16;
    private const uint EsDisplayRequired = 0x00000002;
    private const int FullScreenEdgeTolerance = 2;

    public static bool ShouldBlockAutomaticInvocation()
    {
        // Rule A: A native Windows screensaver is running.
        try
        {
            if (
                SystemParametersInfoW(SpiGetScreenSaverRunning, 0, out var screenSaverRunning, 0)
                && screenSaverRunning
            )
            {
                return true;
            }
        }
        catch
        {
            // A failed query is not proof of a protected state.
        }

        // Rule B: Windows reports a protected notification state.
        try
        {
            var result = SHQueryUserNotificationState(out var state);

            if (
                result >= 0
                && state
                    is QueryUserNotificationState.NotPresent
                        or QueryUserNotificationState.Busy
                        or QueryUserNotificationState.RunningD3DFullScreen
                        or QueryUserNotificationState.PresentationMode
            )
            {
                return true;
            }
        }
        catch
        {
            // Continue to the independent checks.
        }

        // Rule C: Windows says the display is required.
        try
        {
            var status = CallNtPowerInformation(
                SystemExecutionStateInformationLevel,
                0,
                0,
                out var executionState,
                sizeof(uint)
            );

            if (status == 0 && (executionState & EsDisplayRequired) != 0)
                return true;
        }
        catch
        {
            // This is only a supplemental signal.
        }

        // Rule D: Fallback for borderless fullscreen windows.
        try
        {
            return IsForegroundBorderlessFullScreen(out _, out _);
        }
        catch
        {
            // A failed fallback query is not proof of fullscreen.
            return false;
        }
    }

    public static string CreateDiagnosticReport()
    {
        var lines = new List<string>
        {
            "LightBulb - Automatic Invocation Diagnostics",
            $"Checked: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "This is a snapshot of the current Windows state.",
            "No LightBulb settings or display effects are changed.",
            "",
        };

        var blockingReasons = new List<string>();
        var queryFailed = false;

        // Rule A: Is a native Windows screensaver running?
        try
        {
            if (SystemParametersInfoW(SpiGetScreenSaverRunning, 0, out var screenSaverRunning, 0))
            {
                lines.Add(
                    $"Native Windows screensaver: {(screenSaverRunning ? "RUNNING - BLOCK" : "not running")}"
                );

                if (screenSaverRunning)
                    blockingReasons.Add("A native Windows screensaver is running.");
            }
            else
            {
                lines.Add(
                    $"Native Windows screensaver: query failed (Win32 error {Marshal.GetLastWin32Error()})."
                );
                queryFailed = true;
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Native Windows screensaver: query failed ({ex.Message}).");
            queryFailed = true;
        }

        // Rule B: Does Windows report a protected notification/user state?
        try
        {
            var result = SHQueryUserNotificationState(out var state);

            if (result >= 0)
            {
                var stateName = GetNotificationStateName(state);
                var blocksInvocation =
                    state
                    is QueryUserNotificationState.NotPresent
                        or QueryUserNotificationState.Busy
                        or QueryUserNotificationState.RunningD3DFullScreen
                        or QueryUserNotificationState.PresentationMode;

                lines.Add(
                    $"Windows notification state: {stateName}{(blocksInvocation ? " - BLOCK" : "")}"
                );

                if (blocksInvocation)
                {
                    blockingReasons.Add($"Windows reports the protected state {stateName}.");
                }
            }
            else
            {
                lines.Add($"Windows notification state: query failed (HRESULT 0x{result:X8}).");
                queryFailed = true;
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Windows notification state: query failed ({ex.Message}).");
            queryFailed = true;
        }

        // Rule C: Is the display marked as required?
        // This is a supplemental signal, not a definitive video detector.
        try
        {
            var status = CallNtPowerInformation(
                SystemExecutionStateInformationLevel,
                0,
                0,
                out var executionState,
                sizeof(uint)
            );

            if (status == 0)
            {
                var displayRequired = (executionState & EsDisplayRequired) != 0;

                lines.Add(
                    $"System execution state: 0x{executionState:X8}; "
                        + $"display required: {(displayRequired ? "YES - BLOCK" : "no")}"
                );

                if (displayRequired)
                {
                    blockingReasons.Add("Windows reports that the display is required.");
                }
            }
            else
            {
                lines.Add($"System execution state: query failed (NTSTATUS 0x{status:X8}).");
                queryFailed = true;
            }
        }
        catch (Exception ex)
        {
            lines.Add($"System execution state: query failed ({ex.Message}).");
            queryFailed = true;
        }

        // Rule D: Fallback for borderless fullscreen windows.
        try
        {
            var isFullScreen = IsForegroundBorderlessFullScreen(
                out var detail,
                out var checkFailed
            );

            lines.Add($"Fullscreen geometry fallback: {detail}");

            if (isFullScreen)
                blockingReasons.Add("The foreground window covers its monitor.");

            if (checkFailed)
                queryFailed = true;
        }
        catch (Exception ex)
        {
            lines.Add($"Fullscreen geometry fallback: query failed ({ex.Message}).");
            queryFailed = true;
        }

        lines.Add("");

        if (blockingReasons.Count > 0)
        {
            lines.Add("RESULT: AUTOMATIC INVOCATION WOULD BE BLOCKED.");

            foreach (var reason in blockingReasons)
                lines.Add($"  - {reason}");
        }
        else if (queryFailed)
        {
            lines.Add(
                "RESULT: INCONCLUSIVE. No blocking condition was detected by the "
                    + "successful checks, but at least one check failed."
            );
        }
        else
        {
            lines.Add("RESULT: No invocation-prevention condition was detected.");
        }

        lines.Add("");
        lines.Add(
            "Limitations: this test does not inspect individual media players "
                + "or guarantee detection of every video playback scenario."
        );
        lines.Add("The automatic idle timer and mouse wake-up are not implemented by this test.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetNotificationStateName(QueryUserNotificationState state) =>
        state switch
        {
            QueryUserNotificationState.NotPresent => "QUNS_NOT_PRESENT",
            QueryUserNotificationState.Busy => "QUNS_BUSY",
            QueryUserNotificationState.RunningD3DFullScreen => "QUNS_RUNNING_D3D_FULL_SCREEN",
            QueryUserNotificationState.PresentationMode => "QUNS_PRESENTATION_MODE",
            QueryUserNotificationState.AcceptsNotifications => "QUNS_ACCEPTS_NOTIFICATIONS",
            QueryUserNotificationState.QuietTime => "QUNS_QUIET_TIME",
            QueryUserNotificationState.App => "QUNS_APP",
            _ => $"Unknown state ({(int)state})",
        };

    private static bool IsForegroundBorderlessFullScreen(out string detail, out bool checkFailed)
    {
        checkFailed = false;

        using var window = Window.TryGetForeground();

        if (window is null)
        {
            detail = "Could not obtain the foreground window.";
            checkFailed = true;
            return false;
        }

        if (!window.IsVisible())
        {
            detail = "Foreground window is hidden; ignored.";
            return false;
        }

        if (window.IsSystemWindow())
        {
            detail = "Foreground window is a Windows shell window; ignored.";
            return false;
        }

        using var process = window.TryGetProcess();
        var executablePath = process?.TryGetExecutableFilePath();

        // Don't mistake LightBulb's own window for a fullscreen application.
        if (
            string.Equals(
                executablePath,
                global::LightBulb.Program.ExecutableFilePath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            detail = "LightBulb itself is foreground; ignored.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
            checkFailed = true;

        if (window.TryGetRect() is not { } windowRect)
        {
            detail = "Could not obtain the foreground window rectangle.";
            checkFailed = true;
            return false;
        }

        using var monitor = window.TryGetMonitor();

        if (monitor?.TryGetBounds() is not { } monitorRect)
        {
            detail = "Could not obtain the monitor rectangle.";
            checkFailed = true;
            return false;
        }

        var matchesMonitor =
            Math.Abs((long)windowRect.Left - monitorRect.Left) <= FullScreenEdgeTolerance
            && Math.Abs((long)windowRect.Top - monitorRect.Top) <= FullScreenEdgeTolerance
            && Math.Abs((long)windowRect.Right - monitorRect.Right) <= FullScreenEdgeTolerance
            && Math.Abs((long)windowRect.Bottom - monitorRect.Bottom) <= FullScreenEdgeTolerance;

        detail = matchesMonitor
            ? "Foreground window matches the monitor edges within two pixels - BLOCK."
            : "Foreground window does not match the complete monitor rectangle.";

        return matchesMonitor;
    }

    private enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(
        uint uiAction,
        uint uiParam,
        [MarshalAs(UnmanagedType.Bool)] out bool pvParam,
        uint fWinIni
    );

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);

    [DllImport("PowrProf.dll", ExactSpelling = true)]
    private static extern int CallNtPowerInformation(
        int informationLevel,
        nint inputBuffer,
        uint inputBufferLength,
        out uint outputBuffer,
        uint outputBufferLength
    );
}
