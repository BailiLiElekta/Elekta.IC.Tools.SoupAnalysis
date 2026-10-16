using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Elekta.IC.Tools.SoupAnalysis.Tests;

[TestClass]
public sealed class ActionStyleTests
{
    [STATestMethod]
    [DataRow("Upgrade")]
    [DataRow("AwaitingSelection")]
    public void ActionBackgroundIsDistinctAndRetainedWhenSelected(string action)
    {
        var resources = (ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/Elekta.IC.Tools.SoupAnalysis.Desktop;component/Themes/Controls.xaml", UriKind.Relative));
        var cell = new DataGridCell
        {
            Style = (Style)resources["ActionCell"],
            DataContext = new { Action = action }
        };
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        cell.ApplyTemplate();
        var background = ((SolidColorBrush)cell.Background).Color;
        var text = ((SolidColorBrush)cell.Foreground).Color;
        var surface = (Border)cell.Template.FindName("CellSurface", cell);
        Assert.IsTrue(Contrast(background, Colors.White) >= 1.5, "Action fill must be visibly distinct from white.");
        Assert.IsTrue(Contrast(background, text) >= 4.5, "Action text must remain readable.");
        Assert.AreEqual(FontWeights.SemiBold, cell.FontWeight);
        cell.IsSelected = true;
        Assert.AreEqual(background, ((SolidColorBrush)surface.Background).Color);
        cell.DataContext = new { Action = "NotApplicable" };
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        Assert.AreEqual(Colors.White, ((SolidColorBrush)cell.Background).Color, "Recycled cells must clear the action fill.");
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
