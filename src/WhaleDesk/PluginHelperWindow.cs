using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhaleDesk.Monitoring;

namespace WhaleDesk;

/// <summary>
/// Plugin health check: lists the DSH plugins found on disk, marks the ones the pet depends on,
/// and explains what degrades if they are missing. Read-only by design - installing plugins needs
/// a logged-in DSH session (the plugin-manager API answers 401), so the window points the user to
/// the right place instead of pretending it can install things.
/// </summary>
internal sealed class PluginHelperWindow : Window
{
    private readonly Func<IReadOnlyList<InstalledPlugin>> _scan;
    private readonly Action<string> _openPath;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary = new();

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF6, 0x12, 0x1A, 0x2C));
    private static readonly Brush RowBg = new SolidColorBrush(Color.FromArgb(0x2E, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0xD8));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xE8, 0xD0, 0x86));

    public PluginHelperWindow(Func<IReadOnlyList<InstalledPlugin>> scan, Action<string> openPath)
    {
        _scan = scan;
        _openPath = openPath;

        Title = "小鲸娘 · 插件体检";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 480;
        ShowActivated = true;

        Content = Build();
        Refresh();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        PopupDismiss.Install(this);
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = "插件体检（只读）",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "我依赖两个插件。DSH 升级或重装插件后，用这里确认它们还在。安装/卸载请到 DSH 的插件面板里做（那个接口需要登录）。",
            FontSize = 10.5,
            Foreground = Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 8),
        });

        _summary.FontSize = 11;
        _summary.Foreground = Teal;
        _summary.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(_summary);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 380,
            Margin = new Thickness(0, 8, 0, 0),
        };
        ThinScrollBar.Apply(scroll);
        stack.Children.Add(scroll);

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Button("重新扫描", Refresh));
        buttons.Children.Add(Button("打开插件目录", () => _openPath(PluginPath())));
        buttons.Children.Add(Button("关闭", Close));
        stack.Children.Add(buttons);

        return new Border
        {
            Background = Bg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = stack,
        };
    }

    private static string PluginPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles");

    private void Refresh()
    {
        _rows.Children.Clear();
        IReadOnlyList<InstalledPlugin> plugins;
        try
        {
            plugins = _scan();
        }
        catch (Exception ex)
        {
            _summary.Text = "扫描失败：" + ex.Message;
            return;
        }

        var missing = PluginHelper.Missing(plugins);
        var dependencies = plugins.Where(p => p.IsDependency).ToList();
        _summary.Text = missing.Count == 0
            ? $"依赖齐全：{string.Join("、", dependencies.Select(p => p.Name + " " + p.Version))}"
            : "缺少依赖：" + string.Join("、", missing.Select(m => m.Name));

        if (missing.Count > 0)
        {
            _summary.Foreground = Yellow;
            foreach (var (name, why) in missing)
            {
                _rows.Children.Add(Row(name, "缺失", why, null));
            }
        }

        if (plugins.Count == 0)
        {
            _rows.Children.Add(Row("（没扫描到插件）", "—",
                "可能是 DSH 换了安装位置，或插件目录结构变了；先启动一次 DSH 让它生成 profiles 目录。", null));
            return;
        }

        foreach (var plugin in plugins)
        {
            _rows.Children.Add(Row(plugin.Name, plugin.Version,
                plugin.IsDependency ? "依赖（小鲸娘用到）" : "已安装", plugin.Path));
        }
    }

    private UIElement Row(string name, string version, string note, string? path)
    {
        var row = new Border
        {
            Background = RowBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 1, 0, 1),
            ToolTip = path ?? note,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Fg, TextTrimming = TextTrimming.CharacterEllipsis });
        left.Children.Add(new TextBlock { Text = note, FontSize = 9.5, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 });
        var right = new TextBlock
        {
            Text = version,
            FontSize = 10,
            Foreground = Teal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        row.Child = grid;
        return row;
    }

    private UIElement Button(string label, Action onClick)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 6),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = label, FontSize = 11, Foreground = Fg },
        };
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    /// <summary>Bottom-right of the work area, in DIP.</summary>
    public void Place(double workRightDip, double workBottomDip, double marginDip = 24)
    {
        Left = Math.Max(0, workRightDip - Width - marginDip);
        UpdateLayout();
        var height = ActualHeight > 1 ? ActualHeight : 460;
        Top = Math.Max(0, workBottomDip - height - marginDip);
    }
}
