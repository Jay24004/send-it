using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SendIt.App.Services;
using SendIt.Core.Diagnostics;

namespace SendIt.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Intercept WPF Dispatcher unhandled exceptions
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 2. Intercept AppDomain unhandled exceptions (worker threads, CLR crashes)
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // 3. Intercept unobserved task exceptions (async background Tasks)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppLogger.Info("App", $"SendIt application started. Log file: {AppLogger.LogFilePath}");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            AppLogger.Error("CRASH", $"Unhandled UI Dispatcher Exception: {e.Exception.Message}", e.Exception);
            AppLogger.WriteCrashDump(e.Exception, "WPF UI Dispatcher");

            DebugConsoleService.SpawnCrashConsole(e.Exception);

            MessageBox.Show(
                $"A critical UI error occurred:\n\n{e.Exception.Message}\n\nA complete crash dump and log has been saved to:\n{AppLogger.CrashDumpPath}\n\nThe debug console has been opened to inspect the error trace.",
                "SendIt • Critical Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            // Mark as handled to prevent abrupt application crash if recoverable
            e.Handled = true;
        }
        catch
        {
            // Fallback if message box or dispatcher cannot proceed
        }
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception ?? new Exception($"Non-exception object thrown: {e.ExceptionObject}");
            AppLogger.Error("FATAL", $"Fatal AppDomain Unhandled Exception (IsTerminating: {e.IsTerminating}): {ex.Message}", ex);
            AppLogger.WriteCrashDump(ex, $"AppDomain Unhandled (IsTerminating: {e.IsTerminating})");

            DebugConsoleService.SpawnCrashConsole(ex);
        }
        catch
        {
            // Ensure no secondary crash in handler
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            AppLogger.Error("ASYNC_TASK", $"Unobserved Task Exception: {e.Exception.Message}", e.Exception);
            AppLogger.WriteCrashDump(e.Exception, "TaskScheduler Unobserved Task");

            // CRITICAL: Prevent the runtime from terminating the application due to an unobserved network task
            e.SetObserved();
        }
        catch
        {
            // Ensure handler does not throw
        }
    }
}
