using Wpf.Ui.Controls;

namespace AirFerry.Windows.Services;

/// <summary>
/// Themed message dialogs (WPF-UI <see cref="MessageBox"/>) replacing the stock
/// Win32 <c>System.Windows.MessageBox</c>, which ignores the Fluent theme.
/// All call sites must <c>await</c> — ShowDialogAsync pumps its own modal loop
/// and blocking on it from the dispatcher would deadlock.
/// </summary>
public static class UiMessages
{
    public static async Task InfoAsync(string content, string title = "AirFerry")
    {
        var box = new MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "确定",
            IsCloseButtonEnabled = false,
        };
        await box.ShowDialogAsync();
    }

    public static async Task ErrorAsync(string content, string title = "AirFerry")
    {
        var box = new MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = "确定",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            IsCloseButtonEnabled = false,
        };
        await box.ShowDialogAsync();
    }

    /// <summary>Confirmation dialog; true when the user picks the primary action.</summary>
    public static async Task<bool> ConfirmAsync(string content, string title = "AirFerry",
        string primaryText = "确定", bool danger = false)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            CloseButtonText = "取消",
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
