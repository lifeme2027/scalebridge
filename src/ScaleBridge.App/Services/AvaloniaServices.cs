using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ScaleBridge.App.Services;

public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

public sealed class AvaloniaDialogService : IDialogService
{
    private readonly Func<Window> _owner;
    public AvaloniaDialogService(Func<Window> owner) => _owner = owner;

    public async Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => dlg.Close(true);
        cancel.Click += (_, _) => dlg.Close(false);
        dlg.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel },
                },
            },
        };
        var result = await dlg.ShowDialog<bool?>(_owner());
        return result == true;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await _owner().StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = new[] { "*." + extension } } },
        });
        return file?.TryGetLocalPath();
    }
}
