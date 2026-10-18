using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LightBulb.Services;

public sealed class WindowsLifecycleMonitor : IDisposable
{
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsPopup = 0x80000000;

    private const uint WmDisplayChange = 0x007E;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;

    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private const int WtsConsoleConnect = 0x0001;
    private const int WtsConsoleDisconnect = 0x0002;
    private const int WtsRemoteConnect = 0x0003;
    private const int WtsRemoteDisconnect = 0x0004;
    private const int WtsSessionLogon = 0x0005;
    private const int WtsSessionLogoff = 0x0006;
    private const int WtsSessionLock = 0x0007;
    private const int WtsSessionUnlock = 0x0008;
    private const int WtsSessionDesktopReady = 0x000F;

    private const uint NotifyForThisSession = 0;

    private readonly string _className;
    private readonly NativeWindowProc _windowProcedure;
    private readonly nint _moduleHandle;

    private ushort _classAtom;
    private nint _windowHandle;
    private bool _sessionNotificationsRegistered;
    private bool _isDisposed;

    public WindowsLifecycleMonitor()
    {
        _className = $"LightBulb.Lifecycle.{Guid.NewGuid():N}";
        _windowProcedure = WindowProcedure;
        _moduleHandle = GetModuleHandleW(null);

        if (_moduleHandle == 0)
        {
            Debug.WriteLine("Lifecycle monitor: failed to obtain the module handle.");
            return;
        }

        var windowClass = new NativeWindowClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeWindowClassEx>(),
            WindowProcedure = _windowProcedure,
            Instance = _moduleHandle,
            ClassName = _className,
        };

        _classAtom = RegisterClassExW(ref windowClass);

        if (_classAtom == 0)
        {
            Debug.WriteLine(
                "Lifecycle monitor: failed to register its window class. "
                    + $"Win32 error {Marshal.GetLastWin32Error()}."
            );
            return;
        }

        // This is a hidden top-level window, not an HWND_MESSAGE window.
        // It is never shown to the user.
        _windowHandle = CreateWindowExW(
            WsExToolWindow,
            _className,
            _className,
            WsPopup,
            0,
            0,
            0,
            0,
            0,
            0,
            _moduleHandle,
            0
        );

        if (_windowHandle == 0)
        {
            Debug.WriteLine(
                "Lifecycle monitor: failed to create its window. "
                    + $"Win32 error {Marshal.GetLastWin32Error()}."
            );

            UnregisterClassW(_className, _moduleHandle);
            _classAtom = 0;
            return;
        }

        _sessionNotificationsRegistered = WTSRegisterSessionNotification(
            _windowHandle,
            NotifyForThisSession
        );

        if (!_sessionNotificationsRegistered)
        {
            Debug.WriteLine(
                "Lifecycle monitor: failed to register session notifications. "
                    + $"Win32 error {Marshal.GetLastWin32Error()}."
            );
        }
    }

    public bool IsAvailable =>
        !_isDisposed && _windowHandle != 0 && _sessionNotificationsRegistered;

    public event Action? DisplayChanged;
    public event Action? SystemSuspending;
    public event Action? SystemResumed;
    public event Action? SessionUnavailable;
    public event Action? SessionAvailable;

    private nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        if (message == WmDisplayChange)
        {
            DisplayChanged?.Invoke();
        }
        else if (message == WmPowerBroadcast)
        {
            switch (wParam.ToInt32())
            {
                case PbtApmSuspend:
                    SystemSuspending?.Invoke();
                    return (nint)1;

                case PbtApmResumeAutomatic:
                case PbtApmResumeSuspend:
                    SystemResumed?.Invoke();
                    return (nint)1;
            }
        }
        else if (message == WmWtsSessionChange)
        {
            switch (wParam.ToInt32())
            {
                case WtsConsoleDisconnect:
                case WtsRemoteDisconnect:
                case WtsSessionLogoff:
                case WtsSessionLock:
                    SessionUnavailable?.Invoke();
                    break;

                case WtsConsoleConnect:
                case WtsRemoteConnect:
                case WtsSessionLogon:
                case WtsSessionUnlock:
                case WtsSessionDesktopReady:
                    SessionAvailable?.Invoke();
                    break;
            }
        }

        return DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_sessionNotificationsRegistered && _windowHandle != 0)
        {
            if (!WTSUnRegisterSessionNotification(_windowHandle))
            {
                Debug.WriteLine(
                    "Lifecycle monitor: failed to unregister session notifications. "
                        + $"Win32 error {Marshal.GetLastWin32Error()}."
                );
            }

            _sessionNotificationsRegistered = false;
        }

        if (_windowHandle != 0)
        {
            if (!DestroyWindow(_windowHandle))
            {
                Debug.WriteLine(
                    "Lifecycle monitor: failed to destroy its window. "
                        + $"Win32 error {Marshal.GetLastWin32Error()}."
                );
            }

            _windowHandle = 0;
        }

        if (_classAtom != 0)
        {
            if (!UnregisterClassW(_className, _moduleHandle))
            {
                Debug.WriteLine(
                    "Lifecycle monitor: failed to unregister its window class. "
                        + $"Win32 error {Marshal.GetLastWin32Error()}."
                );
            }

            _classAtom = 0;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint NativeWindowProc(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWindowClassEx
    {
        public uint Size;
        public uint Style;
        public NativeWindowProc? WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ClassName;

        public nint IconSmall;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true
    )]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport(
        "user32.dll",
        EntryPoint = "RegisterClassExW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true
    )]
    private static extern ushort RegisterClassExW(ref NativeWindowClassEx windowClass);

    [DllImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true
    )]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter
    );

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", ExactSpelling = true)]
    private static extern nint DefWindowProcW(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport(
        "user32.dll",
        EntryPoint = "DestroyWindow",
        SetLastError = true,
        ExactSpelling = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport(
        "user32.dll",
        EntryPoint = "UnregisterClassW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport(
        "wtsapi32.dll",
        EntryPoint = "WTSRegisterSessionNotification",
        SetLastError = true,
        ExactSpelling = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint hWnd, uint flags);

    [DllImport(
        "wtsapi32.dll",
        EntryPoint = "WTSUnRegisterSessionNotification",
        SetLastError = true,
        ExactSpelling = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint hWnd);
}
