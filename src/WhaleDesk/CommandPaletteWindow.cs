using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhaleDesk.Interaction;

namespace WhaleDesk;

/// <summary>
/// The pet's search palette: one input box, one ranked list, keyboard first (Up/Down/Enter/Esc).
///
/// The interesting part is <see cref="SearchEngine"/> + <see cref="UsageStore"/>: every keystroke
/// ranks candidates from all providers with the fuzzy scorer, a bounded usage boost reorders
/// comparable hits, and an empty query lists what the user uses most. This window is just a thin
/// renderer over that pipeline.
///
/// Stale results are dropped with a generation counter: while a rebuild is pending, a result that
/// belongs to an older query can no longer overwrite the newer list.
/// </summary>
internal sealed class CommandPaletteWindow : Window
{
    private const int MaxRows = 9;

    private readonly Func<IReadOnlyList<PaletteItem>> _provider;
    private readonly UsageStore? _usage;
    private readonly Action? _onUsed;
    private readonly Action<string>? _onExecuted;
    private readonly Action<string> _notify;

    private readonly TextBox _input = new();
    private readonly TextBlock _placeholder = new();
    private readonly StackPanel _rows = new();
    private readonly DispatcherTimer _debounce;
    private readonly List<PaletteItem> _visible = new();
    private int _selected;
    private int _generation;
    private readonly DispatcherTimer _focusRetry;
    private int _focusRetryTicks;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF4, 0x12, 0x1A, 0x2C));
    private static readonly Brush RowBack = new SolidColorBrush(Color.FromArgb(0x44, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));

    public CommandPaletteWindow(
        Func<IReadOnlyList<PaletteItem>> provider,
        UsageStore? usage,
        Action? onUsed,
        Action<string>? onExecuted,
        Action<string> notify,
        MonitorEntry monitor,
        double marginDip = 20)
    {
        _provider = provider;
        _usage = usage;
        _onUsed = onUsed;
        _onExecuted = onExecuted;
        _notify = notify;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => Rebuild(force: true);

        Title = "小鲸娘 · 搜索";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 620;
        ShowActivated = true;

        Content = Build();

        // Width is DIP; the monitor work area is physical, so convert. The actual position is set
        // in physical pixels by PopupFrame (WPF Left/Top assume the primary monitor's origin, which
        // puts a secondary-monitor palette half off the screen).
        var monitorScale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;
        var workWidthDip = monitor.Width / monitorScale;
        Width = Math.Min(Width, Math.Max(320, workWidthDip - marginDip * 2));

        Loaded += (_, _) => FocusInput();
        Activated += (_, _) => FocusInput();
        // The hotkey is pressed while another app is in front; Windows may activate this window a
        // beat late, and the first Focus() then lands on the window instead of the text box. Keep
        // retrying briefly instead of leaving the user with a palette that ignores the keyboard.
        _focusRetry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _focusRetry.Tick += (_, _) =>
        {
            if (_input.IsKeyboardFocusWithin || _focusRetryTicks++ > 6) _focusRetry.Stop();
            else FocusInput();
        };
        _focusRetry.Start();
        // Tunneling handler: a TextBox consumes Up/Down (caret movement) and Enter in its own
        // KeyDown, so a bubbling window handler never sees the keys the palette needs.
        PreviewKeyDown += OnPreviewKeyDown;
        // A pending debounce must not fire into a closed window (it would rebuild a list nobody
        // can see and keep the dispatcher busy); the generation bump invalidates it defensively.
        Closed += (_, _) =>
        {
            _generation++;
            _debounce.Stop();
        };
        PopupDismiss.Install(this);

        Rebuild(force: true);
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Margin = new Thickness(12) };

        var inputHost = new Grid();
        _input.FontSize = 16;
        _input.Padding = new Thickness(8, 6, 8, 6);
        _input.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x8A, 0xB4, 0xFF));
        _input.Foreground = Fg;
        _input.CaretBrush = Fg;
        _input.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF));
        _input.BorderThickness = new Thickness(1);
        _input.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _debounce.Stop();
            _debounce.Start();
        };
        inputHost.Children.Add(_input);

        _placeholder.Text = "搜索命令、片段、快捷启动、剪贴板历史…";
        _placeholder.FontSize = 16;
        _placeholder.Foreground = Dim;
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(12, 0, 0, 0);
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        inputHost.Children.Add(_placeholder);
        stack.Children.Add(inputHost);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 320,
            Margin = new Thickness(0, 8, 0, 0),
        };
        ThinScrollBar.Apply(scroll);
        stack.Children.Add(scroll);

        var hint = new TextBlock
        {
            Text = "↑↓ 选择 · Enter 执行 · Esc 关闭 · 结果按匹配度与使用习惯排序",
            FontSize = 9.5,
            Foreground = Dim,
            Margin = new Thickness(2, 8, 0, 0),
        };
        stack.Children.Add(hint);

        return new Border
        {
            Background = Bg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = stack,
        };
    }

    /// <summary>Recompute the visible list for the current query, then redraw.</summary>
    private void Rebuild(bool force = false)
    {
        _debounce.Stop();
        var generation = ++_generation;
        var query = _input.Text;

        List<PaletteItem> items;
        try
        {
            items = _provider().ToList();
        }
        catch
        {
            items = new List<PaletteItem>();
        }

        // The calculator is generated from the query itself and always competes at the top.
        var calculator = PaletteItems.Calculator(query, text =>
        {
            if (ClipboardWriter.TryCopy(Array.Empty<string>(), text, out var error)) _notify("结果已复制：" + text);
            else _notify("复制失败：" + error);
        });
        if (calculator is not null) items.Insert(0, calculator);

        var byId = items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var candidates = byId.Values
            .Select(i => new SearchCandidate(i.Id, i.Title, i.Subtitle, i.SearchText, i.Weight))
            .ToList();
        var recents = _usage is null ? (_ => 0.0) : (Func<string, double>)(id => _usage.Boost(id, DateTime.Now));
        var hits = SearchEngine.Rank(query, candidates, recents, MaxRows);

        if (generation != _generation) return;   // a newer query already took over

        _visible.Clear();
        foreach (var hit in hits)
        {
            if (byId.TryGetValue(hit.Candidate.Id, out var item)) _visible.Add(item);
        }
        _selected = _visible.Count == 0 ? -1 : 0;
        Redraw(force);
    }

    private void Redraw(bool scrollIntoView = false)
    {
        _rows.Children.Clear();
        if (_visible.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "没有匹配的结果。试试命令名（如「清理」「番茄钟」）或片段名。",
                FontSize = 11,
                Foreground = Dim,
                Margin = new Thickness(2, 6, 0, 6),
            });
            return;
        }

        for (var index = 0; index < _visible.Count; index++)
        {
            var item = _visible[index];
            var row = new Border
            {
                Background = index == _selected ? RowBack : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
            };
            var lines = new StackPanel();
            lines.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 12.5,
                Foreground = Fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrWhiteSpace(item.Subtitle))
            {
                lines.Children.Add(new TextBlock
                {
                    Text = item.Subtitle,
                    FontSize = 9.5,
                    Foreground = Dim,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            row.Child = lines;

            var captured = item;
            row.MouseLeftButtonUp += (_, _) => Run(captured);
            _rows.Children.Add(row);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While an IME is COMPOSING (Chinese/Japanese input) the IME owns the keyboard: arrows pick
        // candidates and Enter commits, so leave those keys alone. When the IME is merely enabled
        // (not composing) WPF still reports the key as ImeProcessed - the real key is in
        // ImeProcessedKey, and the palette must keep navigating with it.
        var composing = e.Key == Key.ImeProcessed &&
                        InputMethod.Current is { ImeState: InputMethodState.On };
        if (composing) return;
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Down:
                e.Handled = true;
                if (_visible.Count > 0) { _selected = (_selected + 1) % _visible.Count; Redraw(); }
                break;
            case Key.Up:
                e.Handled = true;
                if (_visible.Count > 0) { _selected = (_selected - 1 + _visible.Count) % _visible.Count; Redraw(); }
                break;
            // Key.Enter and Key.Return are the same enum value in WPF (6), so one label covers the
            // main Enter key and the numpad one.
            case Key.Enter:
                e.Handled = true;
                if (_selected >= 0 && _selected < _visible.Count) Run(_visible[_selected]);
                break;
        }
    }

    /// <summary>Visible titles in rank order (probes read this).</summary>
    public IReadOnlyList<string> VisibleTitles => _visible.Select(v => v.Title).ToList();

    /// <summary>Probe-only: type a query so the ranking + calculator paths run for real.</summary>
    public void DebugSetQuery(string query)
    {
        _input.Text = query;
        Rebuild(force: true);
    }

    private void Run(PaletteItem item)
    {
        try
        {
            _usage?.Record(item.Id, DateTime.Now);
            _onUsed?.Invoke();
            _onExecuted?.Invoke(item.Id);
            item.Run();
        }
        catch (Exception ex)
        {
            _notify("执行失败：" + ex.Message);
        }
        Close();
    }

    /// <summary>
    /// Put the caret in the search box. The window may be activated a beat after Show(), so this
    /// runs on Loaded, on Activated and from a short retry timer; a failed attempt costs nothing.
    /// </summary>
    private void FocusInput()
    {
        try
        {
            _input.Focus();
            Keyboard.Focus(_input);
        }
        catch
        {
            // focusing is best effort: the retry timer tries again
        }
    }
}
