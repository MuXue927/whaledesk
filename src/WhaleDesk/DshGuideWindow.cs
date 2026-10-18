using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WhaleDesk;

/// <summary>
/// "I cannot find DSH" guide: shows what was detected, lets the user pick the executable, retries,
/// and launches it. This is the answer to the first question a new user has when the pet sits
/// there with no session information.
/// </summary>
internal sealed class DshGuideWindow : Window
{
    private readonly Func<string> _detect;
    private readonly Func<string, string> _setPath;
    private readonly Action _launch;
    private readonly TextBlock _detail = new();
    private readonly TextBlock _pathText = new();

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF6, 0x12, 0x1A, 0x2C));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0xD8));

    public DshGuideWindow(Func<string> detect, Func<string, string> setPath, Action launch)
    {
        _detect = detect;
        _setPath = setPath;
        _launch = launch;

        Title = "小鲸娘 · 没找到 DSH";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        ShowActivated = true;

        Content = Build();
        Refresh();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock
        {
            Text = "我还没连上 DSH",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "桌面上的我靠 DSH 提供的接口工作。按顺序试一下：\n" +
                   "1. 先启动 DSH（第一次启动可能要十几秒）\n" +
                   "2. 如果 DSH 装在非默认位置，用下面的「选择 DSH 程序」告诉我\n" +
                   "3. 还是不行的话，把 DSH 打开后点「重新检测」",
            FontSize = 11,
            Foreground = Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 10),
        });

        _detail.FontSize = 11;
        _detail.Foreground = Teal;
        _detail.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(_detail);

        _pathText.FontSize = 10;
        _pathText.Foreground = Dim;
        _pathText.TextWrapping = TextWrapping.Wrap;
        _pathText.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(_pathText);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Button("重新检测", () => Refresh()));
        buttons.Children.Add(Button("选择 DSH 程序", PickExecutable));
        buttons.Children.Add(Button("启动 / 唤起 DSH", () => { _launch(); Refresh(); }, primary: true));
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

    private UIElement Button(string label, Action onClick, bool primary = false)
    {
        var border = new Border
        {
            Background = primary ? new SolidColorBrush(Color.FromArgb(0x55, 0x3C, 0x7A, 0xC8)) : Brushes.Transparent,
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

    private void Refresh()
    {
        try
        {
            _detail.Text = _detect();
        }
        catch (Exception ex)
        {
            _detail.Text = "检测失败：" + ex.Message;
        }
    }

    private void PickExecutable()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 DSH 的主程序",
                Filter = "DSH / 程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            bool? picked;
            try { picked = dialog.ShowDialog(); }
            catch (Exception dialogError)
            {
                _detail.Text = "打开选择框失败：" + dialogError.Message;
                return;
            }
            if (picked != true) return;
            _pathText.Text = _setPath(dialog.FileName);
            Refresh();
        }
        catch (Exception ex)
        {
            _detail.Text = "设置失败：" + ex.Message;
        }
    }

    /// <summary>Bottom-right of the work area, in DIP.</summary>
    public void Place(double workRightDip, double workBottomDip, double marginDip = 24)
    {
        Left = Math.Max(0, workRightDip - Width - marginDip);
        UpdateLayout();
        var height = ActualHeight > 1 ? ActualHeight : 380;
        Top = Math.Max(0, workBottomDip - height - marginDip);
    }
}
