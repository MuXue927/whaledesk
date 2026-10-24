using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhaleDesk.Cleanup;
using WhaleDesk.Interaction;
using WhaleDesk.Monitoring;

namespace WhaleDesk;

/// <summary>
/// "What is inside this big folder, and what may I delete?" - a read-only report card.
///
/// A separate window (like the quick launcher) keeps it away from the pet's height-sensitive
/// card layout. Deleting is offered only for SafeCache rows and always goes through a
/// confirmation + the guarded CacheCleaner (recycle bin).
///
/// Row layout: star | name + reason | verdict badge | size
/// The star is the FIRST column on purpose: the user could not find it on the right edge.
/// Left-click = drill into the folder. Right-click = ignore/mute it.
/// </summary>
internal sealed class AnalysisWindow : Window
{
    private readonly AnalysisReport _report;
    private readonly Action<string> _notify;
    private readonly Action<string>? _drill;
    private readonly KeepList? _keep;
    private readonly Action? _onKeepChanged;
    private readonly IgnoreList? _ignore;
    private readonly Action? _onIgnoreChanged;
    private readonly Action<string>? _deep;
    private readonly Action<AnalysisReport>? _clean;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x1C, 0x2E));
    private static readonly Brush RowBg = new SolidColorBrush(Color.FromArgb(0x2E, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x86, 0xE0, 0xA8));
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x8A, 0xC4, 0xFF));
    private static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0xD8));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xE8, 0xD0, 0x86));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x9A));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0xB0, 0xBC, 0xD8));

    public AnalysisWindow(AnalysisReport report, Action<string> notify,
        Action<string>? drill = null, KeepList? keep = null, Action? onKeepChanged = null,
        IgnoreList? ignore = null, Action? onIgnoreChanged = null,
        Action<string>? deep = null, Action<AnalysisReport>? clean = null)
    {
        _report = report;
        _notify = notify;
        _drill = drill;
        _keep = keep;
        _onKeepChanged = onKeepChanged;
        _ignore = ignore;
        _onIgnoreChanged = onIgnoreChanged;
        _deep = deep;
        _clean = clean;

        Title = "小鲸娘 · 空间分析";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 700;
        Width = 470;
        ShowActivated = true;

        Content = Build();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Margin = new Thickness(14) };

        stack.Children.Add(new TextBlock
        {
            Text = "空间分析（只读；清理会先确认，且进回收站）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
        });
        stack.Children.Add(new TextBlock
        {
            Text = _report.Root,
            FontSize = 10,
            Foreground = Dim,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 2),
            ToolTip = _report.Root,
        });

        var reclaim = DirectoryAnalyzer.Reclaimable(_report);
        stack.Children.Add(new TextBlock
        {
            Text = $"共 {Fmt.Bytes(_report.Total)} · {_report.Items.Count} 项" +
                   (reclaim > 0 ? $" · 可安全清理约 {Fmt.Bytes(reclaim)}" : "") +
                   (_report.Truncated ? " · 只列最大的若干项" : ""),
            FontSize = 10.5,
            Foreground = Fg,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var rows = new StackPanel();
        foreach (var item in _report.Items) rows.Children.Add(BuildRow(item));

        var scroll = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 460,
            Padding = new Thickness(0, 0, 2, 0),
        };
        ThinScrollBar.Apply(scroll);
        stack.Children.Add(scroll);

        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(Link("打开这个目录", () => OpenPath(_report.Root)));
        var parent = Directory.GetParent(_report.Root.TrimEnd('\\', '/'))?.FullName;
        if (parent is not null && _drill is not null) footer.Children.Add(Link("上一级", () => _drill(parent)));
        if (_deep is not null) footer.Children.Add(Link("自动深挖（找所有缓存）", () => _deep(_report.Root)));
        if (_clean is not null && reclaim > 0)
            footer.Children.Add(Link($"清理可删项（{Fmt.Bytes(reclaim)}）", () => _clean(_report)));
        footer.Children.Add(Link("关闭 (Esc)", Close));
        stack.Children.Add(footer);

        stack.Children.Add(new TextBlock
        {
            Text = "★=已保留（再点取消） · 左键点目录行=下钻 · 右键=忽略这个目录",
            FontSize = 9.5,
            Foreground = Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "绿=可安全删 蓝=在用应用（留程序清缓存） 青=你标记保留 黄=应用数据 红=系统/你的文件 灰=不确定",
            FontSize = 9.5,
            Foreground = Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new Border
        {
            Background = Bg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
    }

    private UIElement BuildRow(AnalysisItem item)
    {
        var color = item.Verdict switch
        {
            CleanVerdict.SafeCache => Green,
            CleanVerdict.InUseApp => Blue,
            CleanVerdict.Kept => Teal,
            CleanVerdict.CleanInApp => Yellow,
            CleanVerdict.SystemFiles or CleanVerdict.PersonalFiles => Red,
            _ => Gray,
        };

        var kept = _keep?.Contains(item.Path) == true;

        var row = new Border
        {
            Background = RowBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            ToolTip = item.Path + "\n" + item.Reason + "\n★ 保留 / 右键 忽略",
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (item.Drillable && _drill is not null && Directory.Exists(item.Path)) _drill(item.Path);
            else OpenPath(item.Path);
        };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_ignore is null) return;
            if (_ignore.Contains(item.Path))
            {
                _ignore.Remove(item.Path);
                _notify($"不再忽略「{item.Name}」");
            }
            else
            {
                _ignore.Add(item.Path);
                _notify($"已忽略「{item.Name}」，以后不再出现（再右键可取消）");
            }
            _onIgnoreChanged?.Invoke();
            Content = Build();
            UpdateLayout();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // star
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // badge
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // size

        var star = new TextBlock
        {
            Text = kept ? "★" : "☆",
            FontSize = 14,
            Foreground = kept ? Teal : Dim,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = kept ? "已加入保留清单：点一下取消" : "加入保留清单（以后不再建议动它）",
        };
        star.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_keep is null) return;
            if (kept) _keep.Remove(item.Path); else _keep.Add(item.Path);
            _onKeepChanged?.Invoke();
            _notify(kept ? $"已取消保留「{item.Name}」" : $"已保留「{item.Name}」");
            Content = Build();
            UpdateLayout();
        };

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 11.5,
            Foreground = Fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        left.Children.Add(new TextBlock
        {
            Text = item.Reason,
            FontSize = 9.5,
            Foreground = Dim,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 240,
        });

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, ((SolidColorBrush)color).Color.R,
                ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = DirectoryAnalyzer.VerdictLabel(item.Verdict),
                FontSize = 9.5,
                Foreground = color,
            },
        };

        var size = new TextBlock
        {
            Text = (item.Partial ? "≥" : "") + Fmt.Bytes(item.Bytes),
            FontSize = 10.5,
            Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(star, 0);
        Grid.SetColumn(left, 1);
        Grid.SetColumn(badge, 2);
        Grid.SetColumn(size, 3);
        grid.Children.Add(star);
        grid.Children.Add(left);
        grid.Children.Add(badge);
        grid.Children.Add(size);
        row.Child = grid;
        return row;
    }

    private UIElement Link(string label, Action onClick)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = Dim,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 12, 4),
        };
        text.MouseLeftButtonUp += (_, _) => onClick();
        return text;
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                _notify("这个路径已经不在了");
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _notify("打开失败：" + ex.Message);
        }
    }
}
