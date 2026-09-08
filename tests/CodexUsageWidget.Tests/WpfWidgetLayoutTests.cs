using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Xunit;

namespace CodexUsageWidget.Tests;

public class WpfWidgetLayoutTests
{
    [Theory]
    [InlineData(false, 385)]
    [InlineData(true, 279)]
    public void BothProductionViewsRenderWithoutOpeningAWindow(bool compact, int width)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                XNamespace local = "clr-namespace:CodexUsageWidget;assembly=CodexTaskbarWidget";
                var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "WidgetLayout.xaml"));
                var xml = new XElement(document.Root!.Element(wpf + "Border")!);
                xml.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
                xml.SetAttributeValue(XNamespace.Xmlns + "local", local.NamespaceName);
                xml.AddFirst(new XElement(wpf + "Border.Resources",
                    new XElement(local + "SliderProgressConverter", new XAttribute(x + "Key", "SliderProgressWidth"))));
                // Resolve the same source icon without creating an Application or HWND.
                foreach (var image in xml.Descendants(wpf + "Image"))
                {
                    if ((string?)image.Attribute("Source") == "CodexWidget.ico")
                        image.SetAttributeValue("Source", new Uri(Path.Combine(AppContext.BaseDirectory, "CodexWidget.ico")).AbsoluteUri);
                }
                var root = (Border)XamlReader.Parse(xml.ToString());
                root.Width = width;
                root.Height = 48;
                var compactPanel = (Grid)root.FindName("CompactLayoutPanel");
                var overviewPanel = (Grid)root.FindName("OverviewLayoutPanel");
                compactPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                overviewPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                if (!compact)
                {
                    root.Background = Brushes.Transparent;
                    root.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
                }
                foreach (string prefix in new[] { "", "Overview" })
                {
                    ((TextBlock)root.FindName(prefix + "TimeText")).Text = "Weekly · Resets Sep 12";
                    ((TextBlock)root.FindName(prefix + "PercentText")).Text = "75%";
                    ((Border)root.FindName(prefix + "RemainingBar")).Width = prefix.Length == 0 ? 81 : 84;
                }
                ((TextBlock)root.FindName("ShortTimeText")).Text = "5 HOURS · Resets in 2h";
                ((TextBlock)root.FindName("ShortPercentText")).Text = "100%";
                ((Border)root.FindName("ShortRemainingBar")).Width = 126;
                ((TextBlock)root.FindName("TaskbarModeText")).Text = "1M";
                ((Slider)root.FindName("TaskbarModeSlider")).Value = 3;
                var backdrop = new Border { Width = width, Height = 48,
                    Background = new SolidColorBrush(Color.FromRgb(27, 27, 27)), Child = root };
                backdrop.Measure(new Size(width, 48));
                backdrop.Arrange(new Rect(0, 0, width, 48));
                backdrop.UpdateLayout();
                Assert.Equal(width, root.ActualWidth);
                Assert.Equal(48, root.ActualHeight);
                Assert.Equal(Visibility.Visible, compact ? compactPanel.Visibility : overviewPanel.Visibility);
                string? output = Environment.GetEnvironmentVariable("CODEX_WIDGET_RENDER_OUTPUT");
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap(width * 2, 96, 192, 192, PixelFormats.Pbgra32);
                    bitmap.Render(backdrop);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, compact ? "widget-compact.png" : "widget-overview.png"));
                    encoder.Save(file);
                }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF rendering did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
