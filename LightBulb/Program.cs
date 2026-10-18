using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Avalonia;
using LightBulb.PlatformInterop;
using LightBulb.Services;

namespace LightBulb;

public static class Program
{
    private static Assembly Assembly { get; } = Assembly.GetExecutingAssembly();

    public static string Name { get; } = Assembly.GetName().Name ?? "LightBulb";

    public static Version Version { get; } = Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string VersionString { get; } = Version.ToString(3);

    public static bool IsDevelopmentBuild { get; } = Version.Major is <= 0 or >= 999;

    public static string ExecutableDirPath { get; } = AppContext.BaseDirectory;

    public static string ExecutableFilePath { get; } =
        Path.ChangeExtension(Assembly.Location, "exe");

    public static string ProjectUrl { get; } = "https://github.com/Tyrrrz/LightBulb";

    public static string ProjectReleasesUrl { get; } = $"{ProjectUrl}/releases";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .With(
                new Win32PlatformOptions
                {
                    // Use redirection surface composition to avoid Avalonia's WinUI Composition
                    // renderer (WinUiCompositorConnection) from ticking endlessly via dcomp.dll
                    // when the application is idle, which would otherwise wake up dwm.exe
                    // continuously even when monitors are powered off.
                    CompositionMode = [Win32CompositionMode.RedirectionSurface],
                    RenderingMode = [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
                }
            )
            .LogToTrace();

    [STAThread]
    public static int Main(string[] args)
    {
        // Diagnostic mode runs independently of the normal single-instance check.
        // It reports Windows state without changing LightBulb's enabled state.
        foreach (var argument in args)
        {
            if (
                !string.Equals(
                    argument,
                    InvocationPreventionChecker.DiagnosticArgument,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            Thread.Sleep(TimeSpan.FromSeconds(5));

            var report = InvocationPreventionChecker.CreateDiagnosticReport();

            MessageBox.ShowInfo("LightBulb - Invocation Prevention", report);
            return 0;
        }

        // Ensure only one instance of the app is running at a time
        using var identityMutex = new Mutex(
            true,
            $"{Name}_Identity",
            out var isOnlyRunningInstance
        );

        if (!isOnlyRunningInstance)
            return 1;

        // Build and run the app
        var builder = BuildAvaloniaApp();

        try
        {
            return builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            MessageBox.ShowError("Fatal Error", ex.ToString());
            throw;
        }
        finally
        {
            // Clean up after application shutdown
            if (builder.Instance is IDisposable disposableApp)
                disposableApp.Dispose();
        }
    }
}
