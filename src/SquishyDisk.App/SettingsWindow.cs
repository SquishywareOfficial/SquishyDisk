using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SquishyDisk.Core;
using DiskCacheMode = SquishyDisk.Core.CacheMode;

namespace SquishyDisk.App;

public sealed class SettingsWindow : Window
{
    public BenchmarkSettings Settings { get; private set; }
    public List<Workload> Workloads { get; private set; }
    private readonly List<(ComboBox Pattern, ComboBox Block, TextBox Queue, TextBox Threads)> editors = [];
    private readonly TextBox duration = new(), warmup = new(), interval = new();
    private readonly ComboBox data = new(), cache = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 10, 0, 0) };

    public SettingsWindow(BenchmarkSettings settings, List<Workload> workloads)
    {
        Settings = settings; Workloads = workloads.ToList();
        Style = (Style)FindResource(typeof(Window));
        Title = "Benchmark settings"; Width = 790; Height = 620; MinWidth = 730; MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new(24) };
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(new TextBlock { Text = "Custom Test", FontSize = 24, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "Queue depth is per thread. Q32 × T16 allows 512 outstanding requests.", FontSize = 12, Margin = new(0, 6, 0, 20), Foreground = (Brush)FindResource("MutedBrush") });
        var table = new Grid();
        foreach (var width in new[] { 55d, 160, 150, 125, 125 }) table.ColumnDefinitions.Add(new() { Width = new(width) });
        table.RowDefinitions.Add(new() { Height = GridLength.Auto });
        string[] headings = ["ROW", "ACCESS", "BLOCK SIZE", "QUEUE (1–64)", "THREADS (1–16)"];
        for (int c = 0; c < headings.Length; c++) Add(table, new TextBlock { Text = headings[c], FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new(0, 0, 8, 8) }, 0, c);
        for (int i = 0; i < 4; i++)
        {
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var w = workloads[i];
            var pattern = new ComboBox { ItemsSource = Enum.GetValues<AccessPattern>(), SelectedItem = w.Pattern };
            var block = new ComboBox { ItemsSource = Sizes.BlockSizes.Select(b => new Choice<int>(Sizes.Format(b), b)).ToList() };
            block.SelectedItem = block.Items.Cast<Choice<int>>().First(b => b.Value == w.BlockSize);
            var queue = new TextBox { Text = w.QueueDepth.ToString() };
            var threads = new TextBox { Text = w.Threads.ToString() };
            FrameworkElement[] cells = [new TextBlock { Text = (i + 1).ToString(), VerticalAlignment = VerticalAlignment.Center }, pattern, block, queue, threads];
            for (int c = 0; c < cells.Length; c++) { cells[c].Margin = new(0, 0, 10, 10); Add(table, cells[c], i + 1, c); }
            editors.Add((pattern, block, queue, threads));
        }
        content.Children.Add(table);
        var timings = new Grid { Margin = new(0, 10, 0, 18) };
        for (int i = 0; i < 3; i++) timings.ColumnDefinitions.Add(new());
        duration.Text = settings.DurationSeconds.ToString(); warmup.Text = settings.WarmupSeconds.ToString(); interval.Text = settings.IntervalSeconds.ToString();
        Add(timings, Field("Test duration (1–60 s)", duration), 0, 0);
        Add(timings, Field("Warm-up seconds (0–60)", warmup), 0, 1);
        Add(timings, Field("Interval seconds (0–60)", interval), 0, 2);
        content.Children.Add(timings);
        data.ItemsSource = new[] { new Choice<DataPattern>("Random data", DataPattern.Random), new Choice<DataPattern>("Zero-filled data", DataPattern.Zero) };
        data.SelectedItem = data.Items.Cast<Choice<DataPattern>>().First(d => d.Value == settings.Data);
        cache.ItemsSource = new[] { new Choice<DiskCacheMode>("Windows cache off · device cache normal", DiskCacheMode.Unbuffered), new Choice<DiskCacheMode>("Windows cache off · write-through", DiskCacheMode.WriteThrough) };
        cache.SelectedItem = cache.Items.Cast<Choice<DiskCacheMode>>().First(c => c.Value == settings.Cache);
        var options = new Grid(); options.ColumnDefinitions.Add(new() { Width = new(220) }); options.ColumnDefinitions.Add(new());
        Add(options, Field("Test data", data), 0, 0); Add(options, Field("Caching", cache), 0, 1); content.Children.Add(options);
        content.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply settings", IsDefault = true, Style = (Style)FindResource("Primary") };
        apply.Click += Apply;
        buttons.Children.Add(cancel); buttons.Children.Add(apply); content.Children.Add(buttons);
    }
    private void Apply(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!int.TryParse(duration.Text, out int d) || !int.TryParse(warmup.Text, out int w) || !int.TryParse(interval.Text, out int gap))
                throw new ArgumentException("Enter whole seconds for measurement, warm-up, and interval.");
            var updated = Settings with { DurationSeconds = d, WarmupSeconds = w, IntervalSeconds = gap,
                Data = ((Choice<DataPattern>)data.SelectedItem).Value, Cache = ((Choice<DiskCacheMode>)cache.SelectedItem).Value };
            var workloads = editors.Select((e, i) => {
                if (!int.TryParse(e.Queue.Text, out int q) || !int.TryParse(e.Threads.Text, out int t))
                    throw new ArgumentException($"Row {i + 1}: enter whole numbers for queue depth and threads.");
                return new Workload($"Workload {i + 1}", (AccessPattern)e.Pattern.SelectedItem, ((Choice<int>)e.Block.SelectedItem).Value, q, t);
            }).ToList();
            // Preserve names when only timing or data settings change.
            for (int i = 0; i < workloads.Count; i++) workloads[i] = workloads[i] with { Name = Workloads[i].Name };
            PlanValidation.Validate(new(Path.GetTempPath(), "Custom", updated, workloads.Select((x, i) => new TestCase(i, x, TestDirection.Read)).ToList()));
            Settings = updated; Workloads = workloads; DialogResult = true;
        }
        catch (ArgumentException ex) { error.Text = ex.Message; }
    }
    private static StackPanel Field(string label, Control control)
    {
        var field = new StackPanel { Margin = new(0, 0, 12, 0) };
        field.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new(0, 0, 0, 7) });
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        field.Children.Add(control); return field;
    }
    private static void Add(Grid grid, UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
}
