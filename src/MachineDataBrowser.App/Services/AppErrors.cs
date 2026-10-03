using System.Globalization;
using Avalonia.Threading;
using Opc.Ua;

namespace MachineDataBrowser.App.Services;

/// <summary>
/// Last line of defence: every exception that is not handled where it happens ends up here, is written to
/// <c>logs/machinedatabrowser.log</c> under the data folder and shown in the error bar instead of crashing the app.
/// </summary>
public static class AppErrors
{
    private static readonly Lock LogLock = new();
    private static int _installed;

    /// <summary>Raised on the UI thread with a message for the user.</summary>
    public static event EventHandler<string>? Reported;

    public static string LogPath { get; set; } = Path.Combine(Core.ClientPaths.DataRoot, "logs", "machinedatabrowser.log");

    /// <summary>Errors worth showing and carrying on after; only process-fatal ones are excluded.</summary>
    public static bool IsRecoverable(Exception ex) => Core.Errors.IsRecoverable(ex);

    /// <summary>
    /// Dock.Avalonia 12.1 throws this from its pointer-move handler while a pane is dragged and a drop target has left
    /// the visual tree (upstream wieslawsoltes/Dock#1136 fixed only part of it).
    /// </summary>
    public static bool IsDockDragGlitch(Exception ex) =>
        ex is ArgumentException { ParamName: "visual" }
        && ex.StackTrace?.Contains("Dock.Avalonia.Internal.DockControlState", StringComparison.Ordinal) == true;

    /// <summary>A short, user-facing description (OPC UA status codes by name, aggregates unwrapped).</summary>
    public static string Describe(Exception ex) => ex switch
    {
        AggregateException { InnerExceptions.Count: 1 } a => Describe(a.InnerExceptions[0]),
        AggregateException a => string.Join("; ", a.InnerExceptions.Select(Describe).Distinct()),
        System.Reflection.TargetInvocationException { InnerException: { } inner } => Describe(inner),
        ServiceResultException sre => $"{Core.StatusText.Of(sre.Result.StatusCode)}: {sre.Message}",
        IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException
            or System.Net.Sockets.SocketException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    /// <summary>Logs the exception and shows it to the user. Safe to call from any thread.</summary>
    public static void Report(Exception ex, string? context = null)
    {
        Log(ex, context);
        var message = context is null ? Describe(ex) : $"{context}: {Describe(ex)}";
        if (Dispatcher.UIThread.CheckAccess())
        {
            Reported?.Invoke(null, message);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Reported?.Invoke(null, message));
        }
    }

    public static void Log(Exception ex, string? context = null)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                }

                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {context ?? "error"}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"Could not write error log: {logFailure.Message}{Environment.NewLine}{ex}");
        }
    }

    /// <summary>Hooks the process-wide handlers. Call once, after Avalonia is set up.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        // Exceptions escaping event handlers, async void methods and commands on the UI thread.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (IsDockDragGlitch(e.Exception))
            {
                // Dock loses track of a drop target mid-drag; the next pointer move recovers, so no error bar.
                e.Handled = true;
                Log(e.Exception, "ignored (pane drag)");
            }
            else if (IsRecoverable(e.Exception))
            {
                e.Handled = true;
                Report(e.Exception);
            }
            else
            {
                Log(e.Exception, "fatal (UI thread)");
            }
        };

        // Faulted fire-and-forget tasks (SDK callbacks, polling loops) that nobody awaited.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Report(e.Exception, "Background operation failed");
        };

        // Exceptions on other threads cannot be recovered by .NET; at least leave a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log(ex, "fatal (background thread)");
            }
        };
    }
}
