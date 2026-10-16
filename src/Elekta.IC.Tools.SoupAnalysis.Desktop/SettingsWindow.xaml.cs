using System.Windows;

namespace Elekta.IC.Tools.SoupAnalysis.Desktop;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}