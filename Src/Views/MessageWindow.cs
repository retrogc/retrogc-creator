using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RetroGcCreator.Views;

/// <summary>Small modal message / question box with any number of buttons.</summary>
public sealed class MessageWindow : Window
{
    private int _result = -1;

    /// <param name="options">
    /// Button labels, left to right. The first one is the default (primary); a "Cancel" option is the
    /// cancel button.
    /// </param>
    private MessageWindow(string title, string message, string[] options)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        // Left to right: "Cancel", the other choices, then the primary (first) choice.
        var order = new System.Collections.Generic.List<int>();
        for (var i = 1; i < options.Length; i++)
            if (options[i] == "Cancel") order.Add(i);
        for (var i = 1; i < options.Length; i++)
            if (options[i] != "Cancel") order.Add(i);
        order.Add(0);

        foreach (var index in order)
        {
            var isCancel = options[index] == "Cancel" || (options.Length == 2 && index == 1);
            var button = new Button { Content = options[index], MinWidth = 90, IsDefault = index == 0, IsCancel = isCancel };
            if (index == 0)
                button.Classes.Add("primary");
            else if (isCancel)
                button.Classes.Add("ghost");

            var result = options[index] == "Cancel" ? -1 : index;
            button.Click += (_, _) =>
            {
                _result = result;
                Close();
            };
            buttons.Children.Add(button);
        }

        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        heading.Classes.Add("h2");
        var body = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap };

        Content = new StackPanel
        {
            Margin = new Thickness(26, 22),
            Children = { heading, body, buttons }
        };
    }

    /// <summary>Message (one button) or question (two buttons): true when the first button is chosen.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message, string yes, string? no) =>
        await ChooseAsync(owner, title, message, no is null ? [yes] : [yes, no]) == 0;

    /// <summary>Returns the index of the chosen option, or -1 (closed or "Cancel").</summary>
    public static async Task<int> ChooseAsync(Window owner, string title, string message, string[] options)
    {
        var window = new MessageWindow(title, message, options);
        await window.ShowDialog(owner);
        return window._result;
    }
}
