using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RetroGcCreator.Views;

/// <summary>Small modal message / question box.</summary>
public sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message, string yes, string? no)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yesButton = new Button { Content = yes, IsDefault = true, MinWidth = 90 };
        yesButton.Classes.Add("primary");
        yesButton.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        if (no is not null)
        {
            var noButton = new Button { Content = no, IsCancel = true, MinWidth = 90 };
            noButton.Classes.Add("ghost");
            noButton.Click += (_, _) => Close(false);
            buttons.Children.Add(noButton);
        }

        buttons.Children.Add(yesButton);

        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        heading.Classes.Add("h2");
        var body = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

        Content = new StackPanel
        {
            Margin = new Thickness(26, 22),
            Children = { heading, body, buttons }
        };
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message, string yes, string? no) =>
        await new MessageWindow(title, message, yes, no).ShowDialog<bool>(owner);
}
