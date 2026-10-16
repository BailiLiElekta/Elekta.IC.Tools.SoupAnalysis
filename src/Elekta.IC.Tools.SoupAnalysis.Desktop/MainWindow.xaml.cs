using System.ComponentModel;
using System.Windows;
using Elekta.IC.Tools.SoupAnalysis.Desktop.ViewModels;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop;

public partial class MainWindow : Window
{
    private bool allowClose;
    private bool closing;
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }
    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (allowClose) return;
        if (DataContext is MainViewModel { SmokeMode: true }) return;
        args.Cancel = true;
        if (closing || DataContext is not MainViewModel model) return;
        closing = true;
        try
        {
            if (model.IsBusy)
            {
                if (MessageBox.Show(this, "Cancel the active operation and wait for its actual outcome before closing?", "Operation active",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                model.CancelActiveOperation();
                await model.WaitForIdleAsync();
            }
            if (model.RemoteOutcomeNeedsReview && MessageBox.Show(this,
                "A remote update ended without a complete outcome result. Inspect the executor journal before retrying. Close with this unresolved outcome acknowledged?",
                "Remote outcome requires review", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            model.SetWindowLayout(RestoreBounds.Width, RestoreBounds.Height, WindowState == WindowState.Maximized);
            await model.PersistOnCloseAsync();
            if (model.UserError is not null && MessageBox.Show(this, $"{model.UserError}\n\nClose without saving these settings?", "Settings not saved",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            allowClose = true;
            model.Dispose();
            Close();
            await ((App)System.Windows.Application.Current).ShutdownHostAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not finish closing: {exception.GetType().Name}. The window remains open.", "Close failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { closing = false; }
    }
}