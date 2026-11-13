using Avalonia;
using Avalonia.Controls;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("macos")]

namespace AccountDeck.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--diagnose")) return Diagnosis.Run(open: !args.Contains("--no-open"));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
            .LogToTrace();
}
