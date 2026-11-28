using Avalonia;
using Avalonia.Media;
using XStream.Player;

namespace XStream.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool child = args.Length >= 2 && args[0] == StreamLauncherArgs.ChildCommand;
        CrashLog.Install(child ? "stream" : "launcher");

        // Single-file Windows build: SDL2/FFmpeg live in a folder unpacked from the exe (see NativeBundle).
        Services.NativeBundle.Register();

        // Child mode: the launcher started us to run one stream (see StreamLauncher). The launcher has
        // already unpacked the libraries; this is a safety net that reports instead of crashing.
        if (child)
        {
            try { Services.NativeBundle.EnsureExtracted(); }
            catch (Exception ex)
            {
                Console.Out.WriteLine(new PlayerEvent
                {
                    Type = "stage", Stage = PlayerStage.Failed,
                    Message = $"Couldn't prepare the video and controller libraries in {Services.NativeBundle.Location}: {ex.Message}",
                }.ToLine());
                return 1;
            }
            // Play together: watching a friend's stream (no sign-in needed).
            if (args[1] == PlayerHost.JoinArg && args.Length > 2)
                return PlayerHost.RunGuestChild(args[2], args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null);
            return PlayerHost.RunChild(args[1], args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null);
        }

        // An update downloaded last time: install it and open the new version instead (before any window).
        if (Services.Updater.TryInstallPending()) return 0;

        // Already running: hand this launch (e.g. a shortcut's --stream) to that copy instead of opening a second one.
        if (Services.SingleInstance.TryForward(args)) return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
        .LogToTrace();
}

/// <summary>Writes any unhandled crash, with its stack trace, to ~/.xstream/logs/crash-*.txt.</summary>
internal static class CrashLog
{
    public static void Install(string who) => AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        try
        {
            var path = Path.Combine(XStream.Core.Config.AppPaths.Logs, $"crash-{who}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, $"Vexel {who} crash at {DateTime.Now:O}\n{Environment.OSVersion}, .NET {Environment.Version}\n\n{e.ExceptionObject}");
            Console.Error.WriteLine($"Crash details saved to {path}");
        }
        catch { /* nothing more we can do */ }
    };
}

internal static class StreamLauncherArgs
{
    public const string ChildCommand = "play-child";
}
