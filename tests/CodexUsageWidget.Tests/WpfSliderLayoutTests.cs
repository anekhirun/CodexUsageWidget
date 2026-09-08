using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Xunit;

namespace CodexUsageWidget.Tests;

public class WpfSliderLayoutTests
{
    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void ActualTemplateFillsToEndAndKeepsThumbInsideTrack(double scale)
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
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var sliders = new List<Slider>();
                string[] labels = ["DEFAULT", "300K", "600K", "1M"];
                for (int value = 0; value <= 3; value++)
                {
                    var xml = new XElement(document.Descendants(wpf + "Slider").Single());
                    xml.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);
                    xml.SetAttributeValue(XNamespace.Xmlns + "local", local.NamespaceName);
                    xml.AddFirst(new XElement(wpf + "Slider.Resources",
                        new XElement(local + "SliderProgressConverter", new XAttribute(x + "Key", "SliderProgressWidth"))));
                    var slider = (Slider)XamlReader.Parse(xml.ToString());
                    slider.Width = 79;
                    slider.Value = value;
                    var column = new StackPanel { Width = 96 };
                    column.Children.Add(new TextBlock
                    {
                        Text = labels[value], Foreground = Brushes.White, FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    column.Children.Add(slider);
                    row.Children.Add(column);
                    sliders.Add(slider);
                }
                var root = new Border
                {
                    Width = 400, Height = 60, Padding = new Thickness(8),
                    Background = new SolidColorBrush(Color.FromRgb(27, 27, 27)), Child = row
                };
                root.Measure(new Size(400, 60));
                root.Arrange(new Rect(0, 0, 400, 60));
                root.UpdateLayout();
                for (int value = 0; value <= 3; value++)
                {
                    var slider = sliders[value];
                    var fill = (Border)slider.Template.FindName("SelectionFill", slider);
                    var track = (Track)slider.Template.FindName("PART_Track", slider);
                    Assert.Equal(79d * value / 3, fill.ActualWidth, 6);
                    Assert.Equal((double)value, track.Value);
                    double thumbLeft = track.Thumb.TranslatePoint(new Point(), slider).X;
                    Assert.InRange(thumbLeft, 0, 79 - track.Thumb.ActualWidth);
                    if (value == 3) Assert.Equal(79, thumbLeft + track.Thumb.ActualWidth, 6);
                }

                string? output = Environment.GetEnvironmentVariable("CODEX_WIDGET_RENDER_OUTPUT");
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap((int)(400 * scale), (int)(60 * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"slider-{(int)(scale * 100)}.png"));
                    encoder.Save(file);
                }
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF layout did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
