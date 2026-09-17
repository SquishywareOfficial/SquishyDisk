using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SquishyDisk.Core;
using SquishyDisk.Windows;

namespace SquishyDisk.App;

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "About SquishyDisk";
        Width = 700; Height = 730; MinWidth = 580; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new(24) };
        Content = layout;
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = ProductInfo.DisplayName, FontSize = 26, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Squishyware", Foreground = (Brush)FindResource("MutedBrush"), Margin = new(0, 4, 0, 16) });
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var close = new Button { Content = "Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom); layout.Children.Add(close);

        var overview = new StackPanel { Margin = new(0, 16, 12, 0) };
        overview.Children.Add(Paragraph("Portable storage benchmark and drive information for Windows."));
        overview.Children.Add(Link("Project and releases", "https://github.com/SquishywareOfficial/SquishyDisk"));
        overview.Children.Add(Heading("SquishyDisk license"));
        overview.Children.Add(Paragraph("WTFPL v2 with a separate NO WARRANTY notice. Full license texts are available in the Licenses tab."));
        overview.Children.Add(Heading("Microsoft DiskSpd 2.3"));
        overview.Children.Add(Paragraph("Benchmark engine built from MIT-licensed source with notification and XML-path fixes. SquishyDisk is not affiliated with Microsoft."));
        overview.Children.Add(Link("DiskSpd source", "https://github.com/microsoft/diskspd/tree/5e7025bfc9d1364f185d4c30963d7ac79195435e"));
        overview.Children.Add(Heading("smartctl 7.5"));
        overview.Children.Add(Paragraph("Drive information and self-tests use the unmodified smartctl executable from smartmontools, licensed under GPL v2 or later. The matching smartctl-7.5-source.zip is supplied with each release."));
        overview.Children.Add(Link("smartmontools", "https://www.smartmontools.org"));
        overview.Children.Add(Heading("Microsoft .NET and WPF 10.0.12"));
        overview.Children.Add(Paragraph("MIT-licensed runtimes. Their licenses and additional third-party notices are included in the Licenses tab."));

        var notices = new[] {
            new Notice("SquishyDisk — license and warranty", Read("AppLicense")),
            new Notice("WTFPL v2", Read("AppWtfpl")),
            new Notice("Microsoft DiskSpd — MIT", EngineStore.License),
            new Notice("smartctl — GPL and authors", SmartctlPayload.Notice),
            new Notice("Microsoft .NET — MIT", Read("ThirdParty.LICENSE.txt")),
            new Notice("Microsoft WPF — MIT", Read("ThirdParty.WPF-LICENSE.txt")),
            new Notice(".NET third-party notices", Read("ThirdParty.THIRD-PARTY-NOTICES.txt"))
        };
        var licenseText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        var selector = new ComboBox { ItemsSource = notices, Margin = new(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetName(selector, "License or notice");
        System.Windows.Automation.AutomationProperties.SetName(licenseText, "Full license text");
        selector.SelectionChanged += (_, _) => { licenseText.Text = ((Notice)selector.SelectedItem).Text; licenseText.ScrollToHome(); };
        selector.SelectedIndex = 0;
        var licenses = new DockPanel { Margin = new(0, 16, 0, 0) };
        DockPanel.SetDock(selector, Dock.Top); licenses.Children.Add(selector); licenses.Children.Add(licenseText);
        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new(0) };
        tabs.Items.Add(new TabItem { Header = "About", Content = new ScrollViewer { Content = overview, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Licenses", Content = licenses });
        layout.Children.Add(tabs);
    }

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 16, 0, 6) };
    private TextBlock Link(string label, string url)
    {
        var link = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url), Foreground = (Brush)FindResource("AccentBrush") };
        link.RequestNavigate += (_, e) => {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Win32Exception) { MessageBox.Show(this, "Could not open the browser.\n" + e.Uri.AbsoluteUri, "Open link", MessageBoxButton.OK, MessageBoxImage.Information); }
            e.Handled = true;
        };
        var text = new TextBlock(); text.Inlines.Add(link); return text;
    }
    private static string Read(string resource)
    {
        using var reader = new StreamReader(typeof(AboutWindow).Assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }
    private sealed record Notice(string Name, string Text)
    {
        public override string ToString() => Name;
    }
}
