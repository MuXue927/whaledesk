using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shapes;
using WhaleDesk.Cleanup;
using WhaleDesk.Monitoring;

namespace WhaleDesk;

/// <summary>
/// The monitoring card, drawn inside the pet window (same trick as the menu: no
/// extra HWND, no activation, and it renders offscreen for verification).
/// </summary>
internal sealed partial class MonitorPanel : Border
{
    private const double TrackWidth = 92;
    private const double BarHeight = 7;
    /// <summary>Card width in DIP. The window is sized from this so text never clips.</summary>
    public const double CardWidth = 272;

    private readonly TextBlock _cpuText = new();
    private readonly TextBlock _memText = new();
    private readonly TextBlock _gpuText = new();
    private readonly TextBlock _diskText = new();
    private readonly TextBlock _netText = new();
    private readonly TextBlock _topText = new();
    private readonly TextBlock _note = new();
    private readonly StackPanel _hogs = new();
    private readonly TextBlock _usage = new();
    private readonly StackPanel _bigDirs = new();
    private readonly ScrollViewer _scroll;
    private const double DefaultMaxHeight = 340;
    /// <summary>Measured height stays bounded: the pet must never be pushed off the screen.</summary>
    public double ContentHeight => _scroll.ActualHeight;

    /// <summary>
    /// The height the card WANTS, independent of the window it currently sits in. ActualHeight
    /// is clamped by the (too short) window, which made the re-measure useless and left the
    /// pet clipped; ExtentHeight is the content's own size.
    /// </summary>
    public double DesiredHeight => _scroll.ExtentHeight > 1 ? _scroll.ExtentHeight + 6 : ActualHeight;
    /// <summary>Raised when the user clicks a memory hog; PetWindow confirms before killing.</summary>
    public event Action<HogRow>? KillRequested;
    /// <summary>Clicking a big directory opens it in Explorer.</summary>
    public event Action<string>? OpenPathRequested;
    /// <summary>Left-click on a big directory asks for a "what is inside" report.</summary>
    public event Action<string>? AnalyzeRequested;
    private readonly Rectangle _cpuFill;
    private readonly Rectangle _memFill;
    private readonly Rectangle _gpuFill;
    private readonly Rectangle _diskFill;

    private static readonly Brush Track = new SolidColorBrush(Color.FromArgb(0x55, 0x9A, 0xB4, 0xE0));
    private static readonly Brush Label = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Value = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF));
    private static readonly FontFamily Font = new("Microsoft YaHei UI, Segoe UI");

    public MonitorPanel()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x18, 0x2B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x8A, 0x8A, 0xB4, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 8, 10, 9);
        Margin = new Thickness(0, 0, 0, 7);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Visibility = Visibility.Collapsed;
        Width = CardWidth;

        var stack = new StackPanel();
        var title = new TextBlock
        {
            Text = "小鲸娘 · 系统监控",
            FontFamily = Font,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Value,
            Margin = new Thickness(0, 0, 0, 6),
        };
        stack.Children.Add(title);

        _cpuFill = Bar(stack, "CPU", _cpuText);
        _memFill = Bar(stack, "内存", _memText);
        _gpuFill = Bar(stack, "GPU", _gpuText);
        _diskFill = Bar(stack, "磁盘", _diskText);

        _netText.FontFamily = Font; _netText.FontSize = 11; _netText.Foreground = Label; _netText.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(_netText);

        _topText.FontFamily = Font; _topText.FontSize = 11; _topText.Foreground = Label;
        _topText.TextWrapping = TextWrapping.Wrap; _topText.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(_topText);

        var hogsTitle = new TextBlock
        {
            Text = "内存大户（点一下 → 再点确认结束）",
            FontFamily = Font, FontSize = 10.5, Foreground = Label,
            Margin = new Thickness(0, 6, 0, 2),
        };
        stack.Children.Add(hogsTitle);
        stack.Children.Add(_hogs);

        _usage.FontFamily = Font; _usage.FontSize = 10.5; _usage.Foreground = Value;
        _usage.TextWrapping = TextWrapping.Wrap; _usage.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(_usage);

        var dirsTitle = new TextBlock
        {
            Text = "最占空间（点一下打开）",
            FontFamily = Font, FontSize = 10.5, Foreground = Label,
            Margin = new Thickness(0, 6, 0, 2),
        };
        stack.Children.Add(dirsTitle);
        stack.Children.Add(_bigDirs);

        _note.FontFamily = Font; _note.FontSize = 10.5; _note.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x6A));
        _note.TextWrapping = TextWrapping.Wrap; _note.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(_note);

        _scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            MaxHeight = DefaultMaxHeight,
            Padding = new Thickness(0, 0, 2, 0),
        };
        ApplyThinScrollBar(_scroll);
        Child = _scroll;
    }

    private Rectangle Bar(Panel host, string label, TextBlock valueText)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TrackWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock { Text = label, FontFamily = Font, FontSize = 11, Foreground = Label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var track = new Grid { Width = TrackWidth, Height = BarHeight, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Rectangle { Fill = Track, RadiusX = 3.5, RadiusY = 3.5 });
        var fill = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromRgb(0x6A, 0xA8, 0xFF)),
            RadiusX = 3.5,
            RadiusY = 3.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        track.Children.Add(fill);
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        valueText.FontFamily = Font; valueText.FontSize = 11; valueText.Foreground = Value;
        valueText.VerticalAlignment = VerticalAlignment.Center;
        valueText.Margin = new Thickness(6, 0, 0, 0);
        // Long values (显存 / 剩余空间) used to be clipped by the card edge; ellipsis
        // makes the degradation visible instead of silent.
        valueText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(valueText, 2);
        row.Children.Add(valueText);

        host.Children.Add(row);
        return fill;
    }

    private static void SetBar(Rectangle fill, double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        fill.Width = TrackWidth * clamped / 100.0;
        fill.Fill = clamped switch
        {
            >= 90 => new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
            >= 70 => new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x66)),
            _ => new SolidColorBrush(Color.FromRgb(0x6A, 0xA8, 0xFF)),
        };
    }

    public void Update(Snapshot s)
    {
        SetBar(_cpuFill, s.CpuPercent);
        _cpuText.Text = Fmt.Percent(s.CpuPercent);

        var memUsed = s.MemTotal > s.MemAvailable ? s.MemTotal - s.MemAvailable : 0;
        SetBar(_memFill, s.MemLoadPercent);
        _memText.Text = $"{Fmt.Bytes(memUsed)}/{Fmt.Bytes(s.MemTotal)}";

        if (s.GpuAvailable)
        {
            SetBar(_gpuFill, s.GpuPercent);
            _gpuText.Text = $"{Fmt.Percent(s.GpuPercent)} · 显存 {Fmt.Bytes(s.GpuDedicatedBytes)}";
        }
        else
        {
            SetBar(_gpuFill, 0);
            _gpuText.Text = "不可用";
        }

        SetBar(_diskFill, s.DiskBusyPercent);
        _diskText.Text = $"{s.DiskName.TrimEnd('\\')} 剩 {Fmt.Bytes(s.DiskFree)} ({Fmt.Percent(s.DiskFreePercent)})";
        _netText.Text = $"网络  ↓ {Fmt.Rate(s.NetDownBps)}   ↑ {Fmt.Rate(s.NetUpBps)}";

        RebuildHogs(s.Families);

        var top = s.TopProcesses.Take(3).Select(p => $"{p.Name} {p.CpuPercent:0.0}%");
        _topText.Text = "占用最高: " + (s.TopProcesses.Count == 0 ? "—" : string.Join("  ·  ", top));

        _note.Text = s.Error is null ? "" : "⚠ " + s.Error;
        _note.Visibility = s.Error is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
