using System;
using System.Threading.Tasks;
using Avalonia;
using RetroGcCreator.Services;

namespace RetroGcCreator;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless modes (no window): image build from the command line, elevated USB writer.
        if (args.Length > 0 && args[0] == "--build")
            return RunBuild(args).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == Flasher.HelperArgument)
            return Flasher.RunElevatedHelper(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static async Task<int> RunBuild(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: retrogc-creator --build <card-folder> <output.img.xz>");
            return 2;
        }

        try
        {
            var last = -1;
            await ImageBuilder.BuildAsync(args[1], args[2], new Progress<BuildProgress>(p =>
            {
                var percent = (int)(p.Fraction * 100);
                if (percent / 10 == last / 10 && percent != 100) return;
                last = percent;
                Console.WriteLine($"{p.Stage}: {percent}%");
            }));
            return 0;
        }
        catch (Exception exception)
        {
            Log.Error("Build failed", exception);
            return 1;
        }
    }
}
