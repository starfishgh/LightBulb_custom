using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;

namespace LightBulb.Services;

public sealed class IdleInvocationController : IDisposable
{
    private const int MaximumIdleInvokeTimeMinutes = 1440;
    private const ulong TickCountWrap = 1UL << 32;

    private readonly SettingsService _settingsService;
    private readonly ExternalApplicationService _externalApplicationService;
    private readonly WindowsLifecycleMonitor _lifecycleMonitor;
    private readonly Func<bool> _isEnabled;
    private readonly Action<bool> _setEnabled;
    private readonly Func<bool> _isSettingsDialogOpen;
    private readonly Action<bool> _setIdleActivationActive;
    private readonly System.Threading.Timer _timer;

    private bool _timerStarted;
    private bool _isDisposed;
    private bool _wasActivatedAutomatically;
    private bool _hasPreviousPreventionSample;
    private bool _wasBlockedByPrevention;
    private bool _isSessionUsable = true;
    private bool _isSystemSuspended;

    private ulong _automaticActivationLastInputUptime;
    private DateTimeOffset _activationAllowedAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCheckTimestamp = DateTimeOffset.MinValue;

    public IdleInvocationController(
        SettingsService settingsService,
        ExternalApplicationService externalApplicationService,
        WindowsLifecycleMonitor lifecycleMonitor,
        Func<bool> isEnabled,
        Action<bool> setEnabled,
        Func<bool> isSettingsDialogOpen,
        Action<bool> setIdleActivationActive
    )
    {
        _settingsService = settingsService;
        _externalApplicationService = externalApplicationService;
        _lifecycleMonitor = lifecycleMonitor;
        _isEnabled = isEnabled;
        _setEnabled = setEnabled;
        _isSettingsDialogOpen = isSettingsDialogOpen;
        _setIdleActivationActive = setIdleActivationActive;

        _lifecycleMonitor.DisplayChanged += OnDisplayChanged;
        _lifecycleMonitor.SystemSuspending += OnSystemSuspending;
        _lifecycleMonitor.SystemResumed += OnSystemResumed;
        _lifecycleMonitor.SessionUnavailable += OnSessionUnavailable;
        _lifecycleMonitor.SessionAvailable += OnSessionAvailable;

        _timer = new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(Check),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan
        );
    }

    public bool IsApplyingAutomaticChange { get; private set; }

    public bool Start()
    {
        if (_isDisposed)
            return false;

        if (!_lifecycleMonitor.IsAvailable)
        {
            Debug.WriteLine(
                "Automatic invocation is disabled because the Windows lifecycle monitor "
                    + "could not register its session notifications."
            );

            return false;
        }

        if (_timerStarted)
            return true;

        _lastCheckTimestamp = DateTimeOffset.Now;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        _timerStarted = true;

        return true;
    }

    // Call when a manual control changes the enabled state.
    public void NotifyManualControl()
    {
        if (!IsApplyingAutomaticChange)
            SetAutomaticOwnership(false);
    }

    private void Check()
    {
        if (_isDisposed)
            return;

        var now = DateTimeOffset.Now;

        // A long gap may indicate suspend/resume or an unavailable UI thread.
        if (
            _lastCheckTimestamp != DateTimeOffset.MinValue
            && now - _lastCheckTimestamp > TimeSpan.FromSeconds(5)
        )
        {
            ExtendActivationGrace(now, TimeSpan.FromSeconds(10));
            DisableAutomaticallyActivatedEffect();
        }

        _lastCheckTimestamp = now;

        if (!_isSessionUsable || _isSystemSuspended)
        {
            DisableAutomaticallyActivatedEffect();
            return;
        }

        var timeoutMinutes = Math.Clamp(
            _settingsService.IdleInvokeTimeMinutes,
            0,
            MaximumIdleInvokeTimeMinutes
        );

        var preventionBlocked = InvocationPreventionChecker.ShouldBlockAutomaticInvocation();

        // Allow a five-second grace period after a protected condition ends.
        if (_hasPreviousPreventionSample && _wasBlockedByPrevention && !preventionBlocked)
        {
            ExtendActivationGrace(now, TimeSpan.FromSeconds(5));
        }

        _hasPreviousPreventionSample = true;
        _wasBlockedByPrevention = preventionBlocked;

        if (_wasActivatedAutomatically)
        {
            // A manual command may already have turned the effect off.
            if (!_isEnabled())
            {
                SetAutomaticOwnership(false);
                return;
            }

            // New keyboard, mouse, or touchpad input wakes the automatic effect.
            if (!TryGetIdleDuration(out _, out var observedLastInputUptime))
            {
                DisableAutomaticallyActivatedEffect();
                return;
            }

            if (observedLastInputUptime != _automaticActivationLastInputUptime)
            {
                DisableAutomaticallyActivatedEffect();
                return;
            }

            if (
                timeoutMinutes == 0
                || now < _activationAllowedAfter
                || _isSettingsDialogOpen()
                || IsForegroundWhitelisted()
                || preventionBlocked
            )
            {
                DisableAutomaticallyActivatedEffect();
            }

            return;
        }

        // Never override an effect that is already enabled manually.
        if (timeoutMinutes == 0 || _isEnabled())
            return;

        if (!TryGetIdleDuration(out var idleDuration, out var lastInputUptime))
            return;

        if (idleDuration < TimeSpan.FromMinutes(timeoutMinutes))
            return;

        if (now < _activationAllowedAfter || _isSettingsDialogOpen())
            return;

        if (IsForegroundWhitelisted() || preventionBlocked)
            return;

        _automaticActivationLastInputUptime = lastInputUptime;
        SetEnabledAutomatically(true);
        SetAutomaticOwnership(true);
    }

    private bool IsForegroundWhitelisted()
    {
        if (
            !_settingsService.IsApplicationWhitelistEnabled
            || _settingsService.WhitelistedApplications is not { } applications
        )
        {
            return false;
        }

        var foregroundApplication = _externalApplicationService.TryGetForegroundApplication();

        return foregroundApplication is not null && applications.Contains(foregroundApplication);
    }

    private void OnDisplayChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
                return;

            ExtendActivationGrace(DateTimeOffset.Now, TimeSpan.FromSeconds(5));
            DisableAutomaticallyActivatedEffect();
        });
    }

    private void OnSystemSuspending()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
                return;

            _isSystemSuspended = true;
            DisableAutomaticallyActivatedEffect();
        });
    }

    private void OnSystemResumed()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
                return;

            _isSystemSuspended = false;
            ExtendActivationGrace(DateTimeOffset.Now, TimeSpan.FromSeconds(10));
            DisableAutomaticallyActivatedEffect();
        });
    }

    private void OnSessionUnavailable()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
                return;

            _isSessionUsable = false;
            DisableAutomaticallyActivatedEffect();
        });
    }

    private void OnSessionAvailable()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposed)
                return;

            _isSessionUsable = true;
            ExtendActivationGrace(DateTimeOffset.Now, TimeSpan.FromSeconds(5));
            DisableAutomaticallyActivatedEffect();
        });
    }

    private void ExtendActivationGrace(DateTimeOffset now, TimeSpan duration)
    {
        var deadline = now.Add(duration);

        if (deadline > _activationAllowedAfter)
            _activationAllowedAfter = deadline;
    }

    private void DisableAutomaticallyActivatedEffect()
    {
        if (_isDisposed || !_wasActivatedAutomatically)
            return;

        if (_isEnabled())
            SetEnabledAutomatically(false);

        SetAutomaticOwnership(false);
    }

    private void SetAutomaticOwnership(bool active)
    {
        _wasActivatedAutomatically = active;
        _setIdleActivationActive(active);
    }

    private void SetEnabledAutomatically(bool enabled)
    {
        IsApplyingAutomaticChange = true;

        try
        {
            _setEnabled(enabled);
        }
        finally
        {
            IsApplyingAutomaticChange = false;
        }
    }

    private static bool TryGetIdleDuration(out TimeSpan idleDuration, out ulong lastInputUptime)
    {
        idleDuration = TimeSpan.Zero;
        lastInputUptime = 0;

        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };

        // A failed query must not be treated as proof that the user is idle.
        if (!GetLastInputInfo(ref info))
            return false;

        var currentUptime = GetTickCount64();

        // Reconstruct a 64-bit timestamp from the 32-bit LASTINPUTINFO tick.
        lastInputUptime = (currentUptime & ~(TickCountWrap - 1)) | info.Time;

        if (lastInputUptime > currentUptime)
        {
            if (lastInputUptime < TickCountWrap)
                return false;

            lastInputUptime -= TickCountWrap;
        }

        idleDuration = TimeSpan.FromMilliseconds(currentUptime - lastInputUptime);

        return true;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _lifecycleMonitor.DisplayChanged -= OnDisplayChanged;
        _lifecycleMonitor.SystemSuspending -= OnSystemSuspending;
        _lifecycleMonitor.SystemResumed -= OnSystemResumed;
        _lifecycleMonitor.SessionUnavailable -= OnSessionUnavailable;
        _lifecycleMonitor.SessionAvailable -= OnSessionAvailable;

        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll", EntryPoint = "GetTickCount64", ExactSpelling = true)]
    private static extern ulong GetTickCount64();
}
