using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WhaleDesk.Interaction;

/// <summary>
/// The quick-launch palette: a small always-on-top list near the pet.
///
/// Deliberately a separate window instead of a card: the card layout is height-sensitive
/// (it has already clipped the pet twice), and a palette that opens/closes on its own has no
/// bearing on the pet window's size at all.
///
/// Entries can be added at runtime: "+ 文件夹" picks any directory (opened in Explorer) and
/// "+ 程序" picks an executable. Everything is persisted to quick-launch.json, so hand editing
/// stays possible.
/// </summary>
internal sealed class LauncherWindow : Window
{
    private readonly LauncherStore _store;
    private readonly string _configPath;
    private readonly Action<string> _report;
    private readonly StackPanel _rows = new();
    /// <summary>True while a modal dialog is up. Closing the owner window under a native modal
    /// dialog crashed the process (0xC0000005), so the dismiss watchdog must not fire then.</summary>
    private bool _dialogOpen;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x1C, 0x2E));
    private static readonly Brush RowBg = new SolidColorBrush(Color.FromArgb(0x3A, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));

    public LauncherWindow(LauncherStore store, string configPath, string title, Action<string> report)
    {
        _store = store;
        _configPath = configPath;
        _report = report;

        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        Width = 320;
        ShowActivated = true;

        Content = BuildChrome();
        Rebuild();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        // Click-outside closes, exactly like the menu and the clipboard window. The old
        // "Deactivated -> Close()" policy is what made this panel flash away whenever the
        // foreground bounced while it was opening (a NOACTIVATE pet window can hand activation
        // back at any moment), and it also fought the palette's own file dialogs.
        PopupDismiss.Install(this, allowDismiss: () => !_dialogOpen);
    }

    private UIElement BuildChrome()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock
        {
            Text = "快捷启动",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            Margin = new Thickness(2, 0, 0, 8),
        });

        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 460,
        };
        stack.Children.Add(scroll);
        stack.Children.Add(BuildFooter());

        return new Border
        {
            Background = Bg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
    }

    private UIElement BuildFooter()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };

        wrap.Children.Add(Button("+ 文件夹", AddFolder));
        wrap.Children.Add(Button("+ 程序", AddProgram));
        wrap.Children.Add(Button("编辑配置", EditConfig));
        wrap.Children.Add(Button("关闭", Close));
        return wrap;
    }

    private UIElement Button(string label, Action onClick)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = Dim,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.MouseLeftButtonUp += (_, _) => onClick();
        return text;
    }

    /// <summary>Re-render the entry rows (after adding or deleting one).</summary>
    private void Rebuild()
    {
        _rows.Children.Clear();
        foreach (var item in _store.Visible())
        {
            var row = new Border
            {
                Background = RowBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                ToolTip = Describe(item),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = item.Name,
                FontSize = 12,
                Foreground = Fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var remove = new TextBlock
            {
                Text = "×",
                FontSize = 12,
                Foreground = Dim,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "从列表移除（也可直接编辑配置文件）",
            };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(remove, 1);
            grid.Children.Add(name);
            grid.Children.Add(remove);
            row.Child = grid;

            var captured = item;
            row.MouseLeftButtonUp += (_, _) => Launch(captured);
            remove.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _store.Remove(captured);
                SaveAndRefresh($"已移除「{captured.Name}」");
            };
            _rows.Children.Add(row);
        }

        if (_rows.Children.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "列表是空的，用「+ 文件夹」或「+ 程序」加一个吧",
                FontSize = 10.5,
                Foreground = Dim,
                Margin = new Thickness(2, 4, 0, 4),
            });
        }
    }

    private static string Describe(Launcher item)
    {
        var parts = new List<string> { item.Target };
        if (!string.IsNullOrWhiteSpace(item.Arguments)) parts.Add(item.Arguments!);
        if (!string.IsNullOrWhiteSpace(item.WorkingDirectory)) parts.Add("(起始目录: " + item.WorkingDirectory + ")");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Show at the bottom-right of the given work area, in DIP. The window is positioned while it
    /// is still hidden, so it never paints a frame at the default window position first.
    /// </summary>
    public void ShowBottomRight(double workRightDip, double workBottomDip, double marginDip = 20)
        => PopupFrame.ShowBottomRight(this, workRightDip, workBottomDip, marginDip, fallbackHeight: 320);

    // ---------- add / remove ----------
    private void AddFolder()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要加入快捷启动的文件夹",
                Multiselect = false,
            };
            if (!RunDialog(dialog.ShowDialog)) return;
            var path = dialog.FolderName.TrimEnd('\\', '/');
            var label = Path.GetFileName(path);
            if (string.IsNullOrEmpty(label)) label = path;
            _store.Add(new Launcher(label, path));
            SaveAndRefresh($"已加入文件夹「{label}」");
        }
        catch (Exception ex)
        {
            _report("选择文件夹失败：" + ex.Message);
        }
    }

    private void AddProgram()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要加入快捷启动的程序",
                Filter = "程序 (*.exe;*.cmd;*.bat;*.lnk)|*.exe;*.cmd;*.bat;*.lnk|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (!RunDialog(dialog.ShowDialog)) return;
            var path = dialog.FileName;
            var label = Path.GetFileNameWithoutExtension(path);
            _store.Add(new Launcher(label, path, null, Path.GetDirectoryName(path)));
            SaveAndRefresh($"已加入程序「{label}」");
        }
        catch (Exception ex)
        {
            _report("选择程序失败：" + ex.Message);
        }
    }

    /// <summary>
    /// Run a modal dialog without letting our Deactivated handler close us, and without an
    /// owner window (a layered/no-activate owner confused the native dialog host).
    /// </summary>
    private bool RunDialog(Func<bool?> show)
    {
        _dialogOpen = true;
        try
        {
            return show() == true;
        }
        catch (Exception ex)
        {
            _report("打开对话框失败：" + ex.Message);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void SaveAndRefresh(string message)
    {
        try
        {
            _store.Save(_configPath);
        }
        catch (Exception ex)
        {
            _report("保存配置失败：" + ex.Message);
            return;
        }
        Rebuild();
        UpdateLayout();
        _report(message);
    }

    private void Launch(Launcher item)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = item.Target,
                UseShellExecute = true,
            };
            if (!string.IsNullOrWhiteSpace(item.Arguments)) info.Arguments = item.Arguments;
            // The pet lives in its own folder; a shell started without a working directory opens
            // THERE, which is not what the user expects. Use the configured one, else the profile.
            info.WorkingDirectory = !string.IsNullOrWhiteSpace(item.WorkingDirectory)
                ? item.WorkingDirectory!
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Process.Start(info);
            _report($"启动了「{item.Name}」");
            Close();
        }
        catch (Exception ex)
        {
            _report($"启动「{item.Name}」失败：{ex.Message}");
        }
    }

    private void EditConfig()
    {
        try
        {
            _store.Save(_configPath);
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = "\"" + _configPath + "\"", UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            _report("打开配置文件失败：" + ex.Message);
        }
    }
}
