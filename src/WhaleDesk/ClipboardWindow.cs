using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using WhaleDesk.Interaction;

namespace WhaleDesk;

/// <summary>
/// The clipboard history palette: newest first, click a row to put it back on the clipboard,
/// right-click to forget that entry, 清空 to wipe everything.
/// </summary>
internal sealed class ClipboardWindow : Window
{
    private readonly ClipboardHistoryStore _store;
    private readonly Action _onChanged;
    private readonly Action<string> _notify;
    private readonly StackPanel _rows = new();

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x1C, 0x2E));
    private static readonly Brush RowBg = new SolidColorBrush(Color.FromArgb(0x2E, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));

    public ClipboardWindow(ClipboardHistoryStore store, Action onChanged, Action<string> notify)
    {
        _store = store;
        _onChanged = onChanged;
        _notify = notify;

        Title = "小鲸娘 · 剪贴板历史";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        Width = 400;
        ShowActivated = true;

        Content = Build();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        PopupDismiss.Install(this);
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock
        {
            Text = "剪贴板历史（只存在本机）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            Margin = new Thickness(2, 0, 0, 8),
        });

        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 460,
        };
        ThinScrollBar.Apply(scroll);
        stack.Children.Add(scroll);
        RebuildRows();

        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(Link("清空历史", ClearAll));
        footer.Children.Add(Link("关闭 (Esc)", Close));
        stack.Children.Add(footer);

        return new Border
        {
            Background = Bg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
    }

    private void RebuildRows()
    {
        _rows.Children.Clear();
        if (_store.Entries.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "还没有记录：复制一段文字就会出现在这里",
                FontSize = 10.5,
                Foreground = Dim,
                Margin = new Thickness(2, 4, 0, 4),
            });
            return;
        }

        foreach (var entry in _store.Entries)
        {
            var row = new Border
            {
                Background = RowBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                ToolTip = entry.Text.Length > 400 ? entry.Text[..400] + "…" : entry.Text,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var time = new TextBlock
            {
                Text = entry.TimeLabel,
                FontSize = 9.5,
                Foreground = Dim,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var preview = new TextBlock
            {
                Text = entry.Preview,
                FontSize = 11,
                Foreground = entry.IsImage ? Dim : Fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(entry.IsImage ? 8 : 0, 0, 0, 0),
            };
            Grid.SetColumn(time, 0);
            if (entry.IsImage)
            {
                var thumbs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                thumbs.Children.Add(new System.Windows.Controls.Image
                {
                    Width = 40,
                    Height = 40,
                    Stretch = System.Windows.Media.Stretch.UniformToFill,
                    Source = LoadThumbnail(entry.ImagePath),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                thumbs.Children.Add(preview);
                Grid.SetColumn(thumbs, 1);
                grid.Children.Add(time);
                grid.Children.Add(thumbs);
            }
            else
            {
                Grid.SetColumn(preview, 1);
                grid.Children.Add(time);
                grid.Children.Add(preview);
            }
            row.Child = grid;

            var captured = entry;
            row.MouseLeftButtonUp += (_, _) => CopyBack(captured);
            row.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                _store.Remove(captured);
                _onChanged();
                _notify("已从历史里删除这一条");
                RebuildRows();
            };
            _rows.Children.Add(row);
        }
    }

    private void CopyBack(ClipEntry entry)
    {
        if (entry.IsImage)
        {
            var bitmap = LoadThumbnail(entry.ImagePath, freeze: true);
            if (bitmap is null) { _notify("图片文件不在啦（可能被清理了）"); return; }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetImage(bitmap);
                    _notify("图片已放回剪贴板，去 Ctrl+V 吧");
                    Close();
                    return;
                }
                catch
                {
                    System.Threading.Thread.Sleep(60);
                }
            }
            _notify("放回图片失败：剪贴板被其他程序占用");
            return;
        }

        // Reuse the verified writer so "copied" really means copied.
        if (ClipboardWriter.TryCopy(Array.Empty<string>(), entry.Text, out var error))
        {
            _notify("已把这条件放回剪贴板，去 Ctrl+V 吧");
            Close();
        }
        else
        {
            _notify("放回剪贴板失败：" + error);
        }
    }

    private static BitmapSource? LoadThumbnail(string? path, bool freeze = false)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = freeze ? 0 : 120;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            if (freeze) image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void ClearAll()
    {
        _store.Clear();
        _onChanged();
        RebuildRows();
        _notify("剪贴板历史已清空");
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
}
