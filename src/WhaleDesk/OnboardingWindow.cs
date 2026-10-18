using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WhaleDesk;

/// <summary>What the user picked in the onboarding window.</summary>
internal sealed record OnboardingResult(
    bool LongTaskAlerts,
    bool OsTaskAlerts,
    bool SedentaryAlerts,
    bool AutoStart,
    bool CursorFollow,
    int FollowSpeed);

/// <summary>
/// First-run wizard: tells the user what she does and lets them choose the initial switches.
/// Shown only on the very first start (or from the menu), so it never gets in the way later.
/// </summary>
internal sealed class OnboardingWindow : Window
{
    private readonly Action<OnboardingResult> _done;
    private readonly Func<string> _dshStatus;
    private readonly CheckBox _longTask;
    private readonly CheckBox _osTask;
    private readonly CheckBox _sedentary;
    private readonly CheckBox _autoStart;
    private readonly CheckBox _cursorFollow;
    private readonly TextBlock _speedLabel;
    private readonly TextBlock _status;
    private int _followSpeed;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF6, 0x12, 0x1A, 0x2C));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0xD8));

    public OnboardingWindow(Action<OnboardingResult> done, Func<string> dshStatus, bool cursorFollow, int followSpeed)
    {
        _done = done;
        _dshStatus = dshStatus;
        _followSpeed = followSpeed <= 0 ? 160 : followSpeed;

        Title = "小鲸娘 · 初次见面";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        ShowActivated = true;

        _longTask = Option("长任务完成提醒（DSH 会话跑了很久后告诉你结果）", true);
        _osTask = Option("系统任务提醒（其他程序跑完长任务也告诉你）", true);
        _sedentary = Option("久坐提醒（连续工作 50 分钟提醒起来活动）", true);
        _autoStart = Option("开机自动启动", false);
        _cursorFollow = Option("跟随光标彩蛋（你离开时她会慢慢朝鼠标走）", cursorFollow);
        _speedLabel = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Teal,
            Margin = new Thickness(24, 2, 0, 2),
            Cursor = Cursors.Hand,
            ToolTip = "点一下切换：慢（80）→ 中（160）→ 快（320）像素/秒",
        };
        _speedLabel.MouseLeftButtonUp += (_, _) =>
        {
            _followSpeed = _followSpeed switch { <= 90 => 160, <= 200 => 320, _ => 80 };
            UpdateSpeedLabel();
        };
        UpdateSpeedLabel();

        _status = new TextBlock
        {
            Text = _dshStatus(),
            FontSize = 10.5,
            Foreground = Teal,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0),
        };

        Content = Build();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void UpdateSpeedLabel() =>
        _speedLabel.Text = $"跟随速度：{_followSpeed switch { <= 90 => "慢", <= 200 => "中", _ => "快" }}（{_followSpeed} 像素/秒，点这里切换）";

    private static CheckBox Option(string label, bool initial)
    {
        return new CheckBox
        {
            Content = label,
            IsChecked = initial,
            FontSize = 11.5,
            Foreground = Fg,
            Margin = new Thickness(2, 3, 0, 3),
        };
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock
        {
            Text = "你好，我是小鲸娘",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "我住在桌面右下角，右键可以打开这些功能：\n" +
                   "· 陪你看 DSH 的会话状态，长任务跑完提醒你\n" +
                   "· 监控内存/磁盘/GPU，内存告急时帮你找出大户\n" +
                   "· C 盘清理、最占空间目录分析（只读，删前会确认）\n" +
                   "· 拖放文件、快捷启动、剪贴板历史、久坐提醒",
            FontSize = 11,
            Foreground = Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 12),
        });

        stack.Children.Add(new TextBlock
        {
            Text = "先选几个开关（以后都能在右键菜单里改）：",
            FontSize = 11,
            Foreground = Fg,
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(_longTask);
        stack.Children.Add(_osTask);
        stack.Children.Add(_sedentary);
        stack.Children.Add(_autoStart);
        stack.Children.Add(_cursorFollow);
        stack.Children.Add(_speedLabel);
        stack.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(Button("开始使用", () => Finish(true), primary: true));
        buttons.Children.Add(Button("稍后再说", () => Finish(false)));
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
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = label, FontSize = 11.5, Foreground = Fg },
        };
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    private void Finish(bool accepted)
    {
        if (accepted)
        {
            _done(new OnboardingResult(
                _longTask.IsChecked == true,
                _osTask.IsChecked == true,
                _sedentary.IsChecked == true,
                _autoStart.IsChecked == true,
                _cursorFollow.IsChecked == true,
                _followSpeed));
        }
        Close();
    }

    /// <summary>Bottom-right of the work area, in DIP.</summary>
    public void Place(double workRightDip, double workBottomDip, double marginDip = 24)
    {
        Left = Math.Max(0, workRightDip - Width - marginDip);
        UpdateLayout();
        var height = ActualHeight > 1 ? ActualHeight : 420;
        Top = Math.Max(0, workBottomDip - height - marginDip);
    }
}
