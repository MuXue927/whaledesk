using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WhaleDesk;

/// <summary>One menu row (moved here when the obsolete in-window PetMenu control was deleted).</summary>
internal sealed record PetMenuEntry(string Label, Action? Activate, bool Enabled = true, bool Checked = false, bool Separator = false);

/// <summary>One group of menu entries. Non-inline groups open a flyout to the right.</summary>
internal sealed record MenuSection(string Title, IReadOnlyList<PetMenuEntry> Items, bool Inline = false);

/// <summary>
/// The right-click menu: a separate window anchored NEXT TO THE PET, with real two-level
/// flyout submenus that expand to the right.
///
/// HISTORY (each step was a reported bug):
///  1. drawn inside the pet window  -> a long menu pushed the pet off the screen;
///  2. separate window pinned to the work-area corner -> far from the pet, and
///     Deactivated->Close() made it vanish while the mouse was still moving towards it;
///  3. beside the pet, flyouts to the right, closes on Esc / pick / click outside;
///  4. v0.15.1: positioning is done in DEVICE PIXELS. With a 125% primary + 100% secondary
///     monitor, Window.Left/Top (DIP) mean different pixel positions depending on which monitor
///     the window lands on, which made the menu invisible when the pet was moved to monitor 2.
/// </summary>
internal sealed class MenuWindow : Window
{
    private readonly IReadOnlyList<MenuSection> _sections;
    private readonly Rect _petRect;        // PHYSICAL pixels
    private readonly Rect _workArea;       // PHYSICAL pixels
    private readonly double _scale;        // DPI scale of the pet's monitor
    private readonly StackPanel _rows = new();
    private SubMenuWindow? _sub;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF4, 0x12, 0x1A, 0x2C));
    private static readonly Brush Hover = new SolidColorBrush(Color.FromArgb(0x44, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0xD8));

    /// <summary>Last placement description, logged by the caller so layout problems stay visible.</summary>
    public string PlaceDiagnostics { get; private set; } = "";

    public MenuWindow(IReadOnlyList<MenuSection> sections, Rect petRectPhysical, Rect workAreaPhysical, double scale)
    {
        _sections = sections;
        _petRect = petRectPhysical;
        _workArea = workAreaPhysical;
        _scale = scale <= 0 ? 1.0 : scale;

        Title = "小鲸娘";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(240, workAreaPhysical.Height / _scale - 40);
        Width = 232;
        ShowActivated = true;

        Content = Build();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        PopupDismiss.Install(this, () => _sub is not null ? new Window[] { _sub } : Array.Empty<Window>(), onDismiss: CloseSub);
        Closed += (_, _) => CloseSub();
    }

    private UIElement Build()
    {
        RebuildRows();
        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(200, _workArea.Height / _scale - 60),
            Padding = new Thickness(0, 0, 2, 0),
        };
        ThinScrollBar.Apply(scroll);

        return new Border
        {
            Background = Bg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new StackPanel { Margin = new Thickness(8), Children = { scroll } },
        };
    }

    private void RebuildRows()
    {
        _rows.Children.Clear();
        foreach (var section in _sections)
        {
            if (section.Inline)
            {
                foreach (var entry in section.Items) AddEntryRow(entry);
                _rows.Children.Add(Divider());
                continue;
            }
            _rows.Children.Add(SectionRow(section));
        }
    }

    private UIElement SectionRow(MenuSection section)
    {
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock { Text = "▸ " + section.Title, FontSize = 11.5, Foreground = Fg };
        var count = new TextBlock
        {
            Text = section.Items.Count(i => !i.Separator) + " 项",
            FontSize = 9.5,
            Foreground = Dim,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(count, 1);
        grid.Children.Add(title);
        grid.Children.Add(count);
        row.Child = grid;

        void Open() => OpenSection(section, row);
        row.MouseEnter += (_, _) => { row.Background = Hover; Open(); };
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => Open();
        return row;
    }

    private void AddEntryRow(PetMenuEntry entry)
    {
        if (entry.Separator)
        {
            _rows.Children.Add(Divider());
            return;
        }
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = entry.Enabled ? Cursors.Hand : Cursors.Arrow,
            Opacity = entry.Enabled ? 1.0 : 0.45,
        };
        row.Child = new TextBlock
        {
            Text = (entry.Checked ? "✓ " : "   ") + entry.Label,
            FontSize = 11.5,
            Foreground = entry.Checked ? Teal : Fg,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (entry.Enabled)
        {
            row.MouseLeftButtonUp += (_, _) => Activate(entry);
            row.MouseEnter += (_, _) => row.Background = Hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        _rows.Children.Add(row);
    }

    private static UIElement Divider() => new Border
    {
        Height = 1,
        Margin = new Thickness(6, 4, 6, 4),
        Background = new SolidColorBrush(Color.FromArgb(0x33, 0x8A, 0xB4, 0xFF)),
    };

    private void Activate(PetMenuEntry entry)
    {
        try
        {
            CloseSub();
            Close();
            entry.Activate?.Invoke();
        }
        catch (Exception ex)
        {
            Close();
            MessageBox.Show("这一项执行失败了：" + ex.Message, "小鲸娘", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenSection(MenuSection section, FrameworkElement anchor)
    {
        try
        {
            CloseSub();
            var offset = anchor.TransformToAncestor(this).Transform(new Point(0, 0));
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !Win32.GetWindowRect(handle, out var parentRect)) return;
            var rowTopPhysical = parentRect.Top + offset.Y * _scale;
            _sub = new SubMenuWindow(section.Title, section.Items, Activate,
                rowTopPhysical, parentRect.Left, parentRect.Right - parentRect.Left, _workArea, _scale);
            _sub.Show();
            _sub.Place();
        }
        catch
        {
            // a flyout failing must not take the menu down
        }
    }

    private void CloseSub()
    {
        _sub?.Close();
        _sub = null;
    }

    /// <summary>
    /// Place the menu beside the pet and keep it inside the pet's work area.
    /// All maths here is in device pixels: see the class comment (mixed-DPI bug).
    /// </summary>
    public void Place()
    {
        UpdateLayout();
        var hPhys = (int)Math.Round((ActualHeight > 1 ? ActualHeight : 320) * _scale);
        var wPhys = (int)Math.Round((ActualWidth > 1 ? ActualWidth : Width) * _scale);
        var gap = (int)Math.Round(8 * _scale);

        var xRight = (int)Math.Round(_petRect.Right) + gap;
        var xLeft = (int)Math.Round(_petRect.Left) - wPhys - gap;
        var fitsRight = xRight + wPhys <= (int)_workArea.Right - 4;
        var x = fitsRight ? xRight : Math.Max((int)_workArea.Left + 4, xLeft);

        var y = (int)Math.Round(_petRect.Bottom) - hPhys;
        y = Math.Max((int)_workArea.Top + 4, Math.Min(y, (int)_workArea.Bottom - hPhys - 4));

        MovePhysical(x, y, wPhys, hPhys);
        PlaceDiagnostics = $"menu phys {x},{y} {wPhys}x{hPhys}; pet {_petRect.Left:0},{_petRect.Top:0} " +
                           $"{(int)_petRect.Width}x{(int)_petRect.Height}; work {_workArea.Left:0},{_workArea.Top:0} " +
                           $"{(int)_workArea.Width}x{(int)_workArea.Height}; scale {_scale:0.##}; {(fitsRight ? "right" : "left")}";

        // Verify: if Windows still put us outside the work area, drag it back.
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !Win32.GetWindowRect(handle, out var rect)) return;
        var outside = rect.Left < (int)_workArea.Left - 2 || rect.Top < (int)_workArea.Top - 2 ||
                      rect.Right > (int)_workArea.Right + 2 || rect.Bottom > (int)_workArea.Bottom + 2;
        if (!outside) return;

        var cx = Math.Max((int)_workArea.Left + 4, Math.Min(rect.Left, (int)_workArea.Right - (rect.Right - rect.Left) - 4));
        var cy = Math.Max((int)_workArea.Top + 4, Math.Min(rect.Top, (int)_workArea.Bottom - (rect.Bottom - rect.Top) - 4));
        MovePhysical(cx, cy, rect.Right - rect.Left, rect.Bottom - rect.Top);
        PlaceDiagnostics += $" | CORRECTED {rect.Left},{rect.Top} -> {cx},{cy}";
    }

    private void MovePhysical(int x, int y, int w, int h)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        Win32.SetWindowPos(handle, IntPtr.Zero, x, y, w, h,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }
}

/// <summary>A second-level flyout: opens to the right of the menu row (left if no room).</summary>
internal sealed class SubMenuWindow : Window
{
    private readonly Rect _workArea;      // PHYSICAL
    private readonly double _anchorTopPhysical;
    private readonly double _parentLeftPhysical;
    private readonly double _parentWidthPhysical;
    private readonly double _scale;

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(0xF6, 0x14, 0x1E, 0x32));
    private static readonly Brush Hover = new SolidColorBrush(Color.FromArgb(0x55, 0x8A, 0xB4, 0xFF));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Teal = new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0xD8));

    public SubMenuWindow(string title, IReadOnlyList<PetMenuEntry> items, Action<PetMenuEntry> onPick,
        double anchorTopPhysical, double parentLeftPhysical, double parentWidthPhysical, Rect workAreaPhysical, double scale)
    {
        _anchorTopPhysical = anchorTopPhysical;
        _parentLeftPhysical = parentLeftPhysical;
        _parentWidthPhysical = parentWidthPhysical;
        _workArea = workAreaPhysical;
        _scale = scale <= 0 ? 1.0 : scale;

        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(200, workAreaPhysical.Height / _scale - 40);
        Width = 244;
        ShowActivated = false;

        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10,
            Foreground = Dim,
            Margin = new Thickness(6, 2, 0, 4),
        });
        foreach (var entry in items)
        {
            if (entry.Separator)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(4, 3, 4, 3),
                    Background = new SolidColorBrush(Color.FromArgb(0x33, 0x8A, 0xB4, 0xFF)),
                });
                continue;
            }
            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = entry.Enabled ? Cursors.Hand : Cursors.Arrow,
                Opacity = entry.Enabled ? 1.0 : 0.45,
            };
            row.Child = new TextBlock
            {
                Text = (entry.Checked ? "✓ " : "   ") + entry.Label,
                FontSize = 11,
                Foreground = entry.Checked ? Teal : Fg,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (entry.Enabled)
            {
                row.MouseLeftButtonUp += (_, _) => onPick(entry);
                row.MouseEnter += (_, _) => row.Background = Hover;
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            }
            stack.Children.Add(row);
        }

        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(180, workAreaPhysical.Height / _scale - 100),
        };
        ThinScrollBar.Apply(scroll);

        Content = new Border
        {
            Background = Bg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = scroll,
        };
    }

    public void Place()
    {
        UpdateLayout();
        var hPhys = (int)Math.Round((ActualHeight > 1 ? ActualHeight : 240) * _scale);
        var wPhys = (int)Math.Round((ActualWidth > 1 ? ActualWidth : Width) * _scale);
        var gap = (int)Math.Round(4 * _scale);

        var xRight = (int)_parentLeftPhysical + (int)_parentWidthPhysical + gap;
        var xLeft = (int)_parentLeftPhysical - wPhys - gap;
        var fitsRight = xRight + wPhys <= (int)_workArea.Right - 4;
        var x = fitsRight ? xRight : Math.Max((int)_workArea.Left + 4, xLeft);
        var y = Math.Max((int)_workArea.Top + 4, Math.Min((int)Math.Round(_anchorTopPhysical), (int)_workArea.Bottom - hPhys - 4));

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        Win32.SetWindowPos(handle, IntPtr.Zero, x, y, wPhys, hPhys,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }
}
