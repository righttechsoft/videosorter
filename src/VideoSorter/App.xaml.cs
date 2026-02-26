using System.Windows;
using LibVLCSharp.Shared;

namespace VideoSorter;

public partial class App : Application
{
    public static LibVLC LibVLC { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoSorter", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UI EXCEPTION:\n{args.Exception}\n\n");
            args.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoSorter", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION:\n{args.ExceptionObject}\n\n");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoSorter", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TASK EXCEPTION:\n{args.Exception}\n\n");
        };

        Core.Initialize();
        LibVLC = new LibVLC("--no-video-title-show");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LibVLC?.Dispose();
        base.OnExit(e);
    }
}
