using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RetroGcCreator.Views;

namespace RetroGcCreator;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // retrogc-creator <card folder or .rgc>
            if (desktop.Args?.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) is { } path)
                window.OpenFromArgument(path);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
