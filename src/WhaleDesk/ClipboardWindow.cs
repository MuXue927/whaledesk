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
    private readonly StackPanel _snippetRows = new();
    private readonly SnippetStore? _snippets;
    private readonly Action? _onSnippetsChanged;
    private readonly string? _snippetImageDir;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x1C, 0x2E));
    private static readonly Brush RowBg = new SolidColorBrush(Color.FromArgb(0x2E, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));

    public ClipboardWindow(ClipboardHistoryStore store, Action onChanged, Action<string> notify,
        SnippetStore? snippets = null, Action? onSnippetsChanged = null, string? snippetImageDir = null)
    {
        _store = store;
        _onChanged = onChanged;
        _notify = notify;
        _snippets = snippets;
        _onSnippetsChanged = onSnippetsChanged;
        _snippetImageDir = snippetImageDir;

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
            Text = "剪贴板（片段 + 历史，只存在本机）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            Margin = new Thickness(2, 0, 0, 8),
        });

        // Snippets first: they are the deliberate, reusable half of the palette.
        stack.Children.Add(SectionLabel("常用片段（左键放回剪贴板，右键删除）"));
        var snippetScroll = new ScrollViewer
        {
            Content = _snippetRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 170,
        };
        ThinScrollBar.Apply(snippetScroll);
        stack.Children.Add(snippetScroll);
        RebuildSnippetRows();

        stack.Children.Add(SectionLabel("最近复制"));

        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 400,
        };
        ThinScrollBar.Apply(scroll);
        stack.Children.Add(scroll);
        RebuildRows();

        var footer = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(Link("存剪贴板为片段", SaveSnippetFromClipboard));
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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
            // Every history row can be pinned as a snippet right here - no need to put it back on
            // the clipboard first (that was the old, awkward two-step flow). The button only shows
            // while the pointer is over that row: a column of permanent buttons is visual noise.
            // Visibility.Hidden (not Collapsed) keeps the column reserved, so revealing it does not
            // reflow the preview text.
            if (_snippets is not null)
            {
                var pin = new TextBlock
                {
                    Text = "存为片段",
                    FontSize = 9.5,
                    Foreground = Dim,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    Visibility = Visibility.Hidden,
                    ToolTip = "把这一条收藏成常用片段（不会改动剪贴板）",
                };
                Grid.SetColumn(pin, 2);
                grid.Children.Add(pin);
                var capturedForPin = entry;
                pin.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;   // must not trigger the row's "put it back" click
                    PinAsSnippet(capturedForPin);
                };
                row.MouseEnter += (_, _) => pin.Visibility = Visibility.Visible;
                row.MouseLeave += (_, _) => pin.Visibility = Visibility.Hidden;
            }

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

    /// <summary>Load a snippet/history PNG for putting back on the clipboard (full size, frozen).</summary>
    public static BitmapSource? LoadImageForClipboard(string? path) => LoadThumbnail(path, freeze: true);

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

    /// <summary>Rows currently rendered in the snippet section (probes verify the wiring).</summary>
    public int SnippetRowCount => _snippetRows.Children.Count;

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 9.5,
        Foreground = Dim,
        Margin = new Thickness(2, 0, 0, 4),
    };

    private void RebuildSnippetRows()
    {
        _snippetRows.Children.Clear();
        if (_snippets is null)
        {
            _snippetRows.Children.Add(new TextBlock
            {
                Text = "片段功能不可用",
                FontSize = 10.5,
                Foreground = Dim,
                Margin = new Thickness(2, 2, 0, 4),
            });
            return;
        }
        if (_snippets.Items.Count == 0)
        {
            _snippetRows.Children.Add(new TextBlock
            {
                Text = "还没有片段：在下面任意一条历史右侧点「存为片段」即可收藏",
                FontSize = 10.5,
                Foreground = Dim,
                Margin = new Thickness(2, 2, 0, 4),
            });
            return;
        }

        foreach (var snippet in _snippets.Items)
        {
            var row = new Border
            {
                Background = RowBg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                ToolTip = snippet.IsImage ? "图片片段（左键放回剪贴板，右键删除）" : snippet.Text,
            };
            var lines = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (snippet.IsImage)
            {
                var thumb = LoadThumbnail(snippet.ImagePath);
                if (thumb is not null)
                {
                    lines.Children.Add(new System.Windows.Controls.Image
                    {
                        Width = 32,
                        Height = 32,
                        Stretch = System.Windows.Media.Stretch.UniformToFill,
                        Source = thumb,
                        Margin = new Thickness(0, 0, 8, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
            }
            var textPart = new StackPanel();
            textPart.Children.Add(new TextBlock
            {
                Text = snippet.Name,
                FontSize = 11,
                Foreground = Fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var preview = snippet.IsImage
                ? "[图片片段]"
                : snippet.Text.Replace('\r', ' ').Replace('\n', ' ');
            textPart.Children.Add(new TextBlock
            {
                Text = preview.Length > 80 ? preview[..80] + "…" : preview,
                FontSize = 9.5,
                Foreground = Dim,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            lines.Children.Add(textPart);
            row.Child = lines;

            var captured = snippet;
            row.MouseLeftButtonUp += (_, _) => CopySnippet(captured);
            row.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                _snippets.Remove(captured);
                _onSnippetsChanged?.Invoke();
                _notify($"片段「{captured.Name}」已删除");
                RebuildSnippetRows();
            };
            _snippetRows.Children.Add(row);
        }
    }

    /// <summary>Pin one history entry as a snippet without touching the clipboard.</summary>
    private void PinAsSnippet(ClipEntry entry)
    {
        if (_snippets is null) return;
        Snippet? snippet;
        if (entry.IsImage)
        {
            if (string.IsNullOrEmpty(_snippetImageDir))
            {
                _notify("图片片段目录不可用");
                return;
            }
            snippet = _snippets.AddImage(entry.Preview, entry.ImagePath ?? "", _snippetImageDir, DateTime.Now);
            if (snippet is null)
            {
                _notify("这张图片已经不在啦（可能被清理了），无法收藏");
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                _notify("这一条没有可收藏的文字");
                return;
            }
            snippet = _snippets.Add(SnippetStore.FirstLine(entry.Text), entry.Text, DateTime.Now);
        }
        _onSnippetsChanged?.Invoke();
        _notify(snippet.IsImage ? $"已收藏图片片段「{snippet.Name}」" : $"已收藏片段「{snippet.Name}」");
        RebuildSnippetRows();
    }

    private void CopySnippet(Snippet snippet)
    {
        if (snippet.IsImage)
        {
            var bitmap = LoadThumbnail(snippet.ImagePath, freeze: true);
            if (bitmap is null)
            {
                _notify("片段图片不在啦");
                return;
            }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetImage(bitmap);
                    _notify($"片段「{snippet.Name}」已放回剪贴板");
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

        var text = SnippetStore.Expand(snippet.Text, DateTime.Now);
        if (ClipboardWriter.TryCopy(Array.Empty<string>(), text, out var error))
        {
            _notify($"片段「{snippet.Name}」已放到剪贴板");
            Close();
        }
        else
        {
            _notify("放回剪贴板失败：" + error);
        }
    }

    private void SaveSnippetFromClipboard()
    {
        if (_snippets is null) return;

        // A copied image has no text, so try the image path first.
        try
        {
            if (!string.IsNullOrEmpty(_snippetImageDir) && System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image is not null)
                {
                    Directory.CreateDirectory(_snippetImageDir);
                    var temp = Path.Combine(_snippetImageDir, "clip-" + Guid.NewGuid().ToString("N")[..8] + ".png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var stream = File.Create(temp)) encoder.Save(stream);
                    var imageSnippet = _snippets.AddImage("图片片段", temp, _snippetImageDir, DateTime.Now);
                    try { File.Delete(temp); } catch { }
                    if (imageSnippet is null)
                    {
                        _notify("这张图片收藏失败");
                        return;
                    }
                    _onSnippetsChanged?.Invoke();
                    _notify($"已收藏图片片段「{imageSnippet.Name}」");
                    RebuildSnippetRows();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _notify("收藏剪贴板图片失败：" + ex.Message);
            return;
        }

        string text;
        try { text = System.Windows.Clipboard.GetText() ?? ""; }
        catch { text = ""; }
        if (string.IsNullOrWhiteSpace(text))
        {
            _notify("剪贴板里没有文字或图片，先复制一段再存");
            return;
        }
        var snippet = _snippets.Add(SnippetStore.FirstLine(text), text, DateTime.Now);
        _onSnippetsChanged?.Invoke();
        _notify($"已存为片段「{snippet.Name}」");
        RebuildSnippetRows();
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
