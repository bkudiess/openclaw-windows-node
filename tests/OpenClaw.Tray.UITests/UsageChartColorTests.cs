using Microsoft.UI;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Pages.Usage;
using OpenClawTray.Presentation.Usage;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class UsageChartColorTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataMarksRemainContrastingWhenTheButtonAccentMatchesThePlot(bool interactive)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        UIElement? root = null;
        Border? buttonFill = null;
        ChatThemeProofScope? resources = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope("HighContrast");
                // Supply the native HC collision as theme input, without changing the desktop's theme.
                foreach (var dictionary in Application.Current.Resources.ThemeDictionaries.Values
                    .OfType<ResourceDictionary>().Prepend(Application.Current.Resources))
                {
                    dictionary["AccentFillColorDefaultBrush"] = new SolidColorBrush(Colors.Black);
                    dictionary["UsagePalette1Brush"] = new SolidColorBrush(Colors.White);
                }
                buttonFill = (Border)XamlReader.Load(
                    """<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource AccentFillColorDefaultBrush}" Width="20" Height="20"/>""");
                ui.Container.Children.Add(buttonFill);
                host = new ReactorHostControl { Width = 320, RequestedTheme = ElementTheme.Light };
                var chart = UsageCharts.Columns(
                    [new("day", 50, [new("total", 50)], "Day", "Sample day", false, false, false)],
                    [new("total", UsageSeriesKind.Total, null, 0)],
                    100, value => value.ToString(),
                    interactive ? static (_, _) => { } : null,
                    "UsageColorProof");
                root = host.Reconciler.Mount(chart, static () => { });
                host.Content = root;
                ui.Container.Children.Add(host);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(buttonFill!.Background).Color);
                var bar = Assert.Single(FindLogical<FrameworkElement>(root!),
                    element => AutomationProperties.GetName(element) == "Sample day");
                var mark = Assert.Single(FindLogical<Border>(bar),
                    border => border.Child is null);
                await WaitForRenderedConditionAsync(
                    () => mark.ActualWidth > 0 && mark.ActualHeight > 0,
                    "Usage chart data mark layout",
                    () => $"Width={mark.ActualWidth}, Height={mark.ActualHeight}");
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(mark.Background).Color);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                }
                if (buttonFill is not null) ui.Container.Children.Remove(buttonFill);
                resources?.Dispose();
            });
        }
    }
}
