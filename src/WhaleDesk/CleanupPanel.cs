using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhaleDesk.Cleanup;

namespace WhaleDesk;

/// <summary>
/// The cleanup card.
///
/// Three things it must do, all requested after the first version was tried out:
///  * every item explains what it IS and what deleting it DOES, with a verdict, so the
///    user is never asked to delete something they cannot evaluate;
///  * confirming is reversible — the armed row shows 确认 and 取消, the arm expires on
///    its own and the row visibly reverts (the first version had no way out at all);
///  * rows needing elevation or that are outright a bad idea are listed but refused.
/// </summary>
internal sealed partial class CleanupPanel : Border
{
    public const double CardWidth = 344;          // default width in DIP
    private const double DefaultListMaxHeight = 340;
    private const double MinListHeight = 110;
    private const double MaxListHeight = 720;
    private const double MinCardWidth = 300;
    private const double MaxCardWidth = 620;

    private sealed class Row
    {
        public CleanupTarget Target = null!;
        public TextBlock Name = null!;
        public TextBlock Size = null!;
        public TextBlock ActionText = null!;
        public Border ActionChip = null!;
        public Border ConfirmChip = null!;
        public Border CancelChip = null!;
        public TextBlock VerdictText = null!;
        public Border DetailBox = null!;
        public bool Expanded;
        public bool Elevated;
    }

    private sealed class Group
    {
        public string Title = "";
        public StackPanel Body = null!;
        public TextBlock Header = null!;
        public bool Collapsed;
    }

    private readonly StackPanel _groups = new();
    private readonly ScrollViewer _scroll;
    private readonly List<Row> _rows = new();
    private readonly List<Group> _groupList = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _hint = new();
    private readonly Border _rescan;
    private readonly Border _safeBatch;
    private readonly TextBlock _safeBatchText = new();
    private readonly Border _showAll;
    private readonly TextBlock _showAllText = new();
    private readonly Border _ageChip;
    private readonly TextBlock _ageText = new();
    private readonly CleanupArmState _arm = new();
    private List<CleanupTarget> _targets = CleanupCatalog.Build().ToList();
    private int _tempAgeDays = CleanupCatalog.DefaultTempAgeDays;

    private bool _showAbsent;
    private IReadOnlyList<(CleanupTarget Target, CleanupScan Scan)> _scans = Array.Empty<(CleanupTarget, CleanupScan)>();

    private static readonly FontFamily Font = new("Microsoft YaHei UI, Segoe UI");
    private static readonly Brush Label = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xD8));
    private static readonly Brush Value = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x6A, 0xA8, 0xFF));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x8A, 0xD9, 0x8A));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x6A));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x74, 0x80, 0x9A));

    public event Action? ScanRequested;
    public event Action<CleanupTarget>? CleanRequested;
    public event Action? SafeBatchRequested;
    /// <summary>Raised with the new age threshold (0 = everything, regardless of age).</summary>
    public event Action<int>? TempAgeChanged;
    /// <summary>Raised when the user resizes the card, so the window can follow.</summary>
    public event Action? Resized;
    /// <summary>Raised when an admin-only row asks for an elevated cleanup.</summary>
    public event Action<CleanupTarget>? ElevatedCleanRequested;

    /// <summary>User-chosen card width (persisted).</summary>
    /// <summary>User-chosen card width (persisted). Kept separate from the live Width.</summary>
    public double PreferredWidth => _desiredWidth;
    private double _desiredWidth = CardWidth;
    private double _desiredListHeight = DefaultListMaxHeight;
    private double _effectiveListHeight = DefaultListMaxHeight;

    /// <summary>How far INSIDE an edge still counts as "on the edge".</summary>
    public const double EdgeBandInside = 12;
    /// <summary>How far OUTSIDE the card still counts — the old version only reacted inside.</summary>
    public const double EdgeBandOutside = 16;

    /// <summary>
    /// Which resize edge a point is on, or null. The band extends OUTSIDE the card as well
    /// as inside, because hunting for a cursor change just inside the border is exactly what
    /// felt wrong. The right edge keeps a small inner band so the scrollbar stays grabbable,
    /// and the bottom keeps a small one so the footer buttons stay clickable.
    /// </summary>
    public string? EdgeAt(Point p)
    {
        if (Visibility != Visibility.Visible) return null;
        var w = ActualWidth > 1 ? ActualWidth : _desiredWidth;
        var h = ActualHeight > 1 ? ActualHeight : 400;
        const double outer = EdgeBandOutside;
        var leftIn = 8.0;
        var rightIn = 2.0;      // the scrollbar lives here
        var topIn = EdgeBandInside;
        var bottomIn = 6.0;     // the footer chips live here

        var onLeft = p.X >= -outer && p.X <= leftIn;
        var onRight = p.X >= w - rightIn && p.X <= w + outer;
        var onTop = p.Y >= -outer && p.Y <= topIn;
        var onBottom = p.Y >= h - bottomIn && p.Y <= h + outer;
        if (!onLeft && !onRight && !onTop && !onBottom) return null;

        // At a corner pick the nearer edge.
        var dLeft = Math.Abs(p.X);
        var dRight = Math.Abs(p.X - w);
        var dTop = Math.Abs(p.Y);
        var dBottom = Math.Abs(p.Y - h);
        var candidates = new List<(string Edge, double Dist)>();
        if (onLeft) candidates.Add(("left", dLeft));
        if (onRight) candidates.Add(("right", dRight));
        if (onTop) candidates.Add(("top", dTop));
        if (onBottom) candidates.Add(("bottom", dBottom));
        return candidates.OrderBy(c => c.Dist).First().Edge;
    }

    /// <summary>Outward sign for an edge: which drag direction makes the card bigger.</summary>
    public static int OutwardSign(string edge) => edge switch
    {
        "left" => -1,     // dragging left grows
        "right" => +1,
        "top" => -1,      // dragging up grows
        _ => +1,
    };

    public static bool IsHorizontalEdge(string edge) => edge is "left" or "right";

    public CleanupPanel()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x14, 0x18, 0x2B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x8A, 0x8A, 0xB4, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10, 8, 10, 9);
        Margin = new Thickness(0, 0, 10, 7);   // 10 DIP on the right: room to grab the edge from OUTSIDE
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Visibility = Visibility.Collapsed;
        Width = CardWidth;

        var root = new StackPanel();

        root.Children.Add(new TextBlock
        {
            Text = "小鲸娘 · C 盘大清理",
            FontFamily = Font, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Value,
        });
        _summary.FontFamily = Font; _summary.FontSize = 10.5; _summary.Foreground = Label;
        _summary.TextWrapping = TextWrapping.Wrap; _summary.Margin = new Thickness(0, 3, 0, 4);
        _summary.Text = "先点「扫描」：只读检查，不会删任何东西。";
        root.Children.Add(_summary);

        // ---- the list ----
        var scrollContent = new StackPanel();
        foreach (var groupTitle in CleanupCatalog.Build().Select(t => t.Group).Distinct())
        {
            var body = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            var header = new TextBlock
            {
                FontFamily = Font, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Accent, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2),
            };
            var headerBorder = new Border { Child = header, Cursor = Cursors.Hand, Padding = new Thickness(0, 1, 0, 1) };
            var group = new Group { Title = groupTitle, Body = body, Header = header };
            headerBorder.MouseLeftButtonUp += (_, _) =>
            {
                group.Collapsed = !group.Collapsed;
                body.Visibility = group.Collapsed ? Visibility.Collapsed : Visibility.Visible;
                UpdateGroupHeader(group);
            };
            scrollContent.Children.Add(headerBorder);
            scrollContent.Children.Add(body);
            _groupList.Add(group);
        }
        _scroll = new ScrollViewer
        {
            Content = scrollContent,
            MaxHeight = _effectiveListHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        ApplyThinScrollBar(_scroll);
        root.Children.Add(_scroll);

        // ---- footer ----
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var rescanText = new TextBlock { Text = "重新扫描" };
        _rescan = Chip(rescanText, () => ScanRequested?.Invoke());
        _safeBatchText.Text = "一键清理放心项";
        _safeBatch = Chip(_safeBatchText, () => SafeBatchRequested?.Invoke());
        _showAllText.Text = "显示未发现的";
        _showAll = Chip(_showAllText, ToggleShowAbsent);
        _ageText.Text = AgeLabel();
        _ageChip = Chip(_ageText, CycleAge);
        footer.Children.Add(_rescan);
        footer.Children.Add(_safeBatch);
        footer.Children.Add(_showAll);
        footer.Children.Add(_ageChip);
        root.Children.Add(footer);

        _hint.FontFamily = Font; _hint.FontSize = 10; _hint.Foreground = Muted;
        _hint.TextWrapping = TextWrapping.Wrap; _hint.Margin = new Thickness(0, 5, 0, 0);
        _hint.Text = "拖动四个边框可调整面板大小（向外变大、向内变小）；点任意一行可展开它的完整说明（作用 / 删除影响 / 结论）。"
                   + (RecycleBin.CanUndelete()
                        ? "删除默认进回收站，可还原。"
                        : "注意：这台机器的回收站已被组策略关闭，删除是永久删除、无法还原。");
        root.Children.Add(_hint);

        BuildRows();

        // The old version put four invisible 7 DIP strips INSIDE the card, so the cursor
        // only changed once it was already inside the border. Hover/drag is now decided by
        // hit-testing a band that extends outside the card as well (see EdgeAt), driven by
        // PetVisual, which sees mouse events for the whole window.
        Child = root;
    }

    /// <summary>Kept for source compatibility; the edges are handled by PetVisual now.</summary>
    private Border EdgeHandleUnused(Cursor cursor, HorizontalAlignment? h, VerticalAlignment? v, string tip)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
            Cursor = cursor,
            ToolTip = tip,
        };
        if (h is not null) { border.HorizontalAlignment = h.Value; border.Width = 7; }
        else { border.VerticalAlignment = v!.Value; border.Height = 7; }
        if (h is not null && v is null) border.VerticalAlignment = VerticalAlignment.Stretch;
        if (v is not null && h is null) border.HorizontalAlignment = HorizontalAlignment.Stretch;
        return border;
    }

    // ---------------------------------------------------------------- resize (testable)

    private bool _resizeHorizontal;
    private int _resizeSign;
    private int _resizeStartCursorX;
    private int _resizeStartCursorY;
    private double _resizeStartWidth;
    private double _resizeStartListHeight;

    /// <summary>Start a resize from the current cursor position and the current size.</summary>
    public void BeginResize(bool horizontal, int outwardSign)
    {
        _resizeHorizontal = horizontal;
        _resizeSign = outwardSign;
        if (Win32.GetCursorPos(out var cursor)) { _resizeStartCursorX = cursor.X; _resizeStartCursorY = cursor.Y; }
        _resizeStartWidth = _desiredWidth;
        _resizeStartListHeight = _desiredListHeight;
    }

    /// <summary>
    /// Apply a resize from an absolute screen position. The delta is measured from the
    /// position captured in <see cref="BeginResize"/>, converted once with the DPI scale,
    /// and ignored below a 1 px dead zone — so no motion means no change, and a given
    /// cursor travel always produces the same size change.
    /// </summary>
    public void UpdateResize(int cursorScreenX, int cursorScreenY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_resizeHorizontal)
        {
            var deltaPx = cursorScreenX - _resizeStartCursorX;
            if (Math.Abs(deltaPx) < 1) return;                       // dead zone
            var scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            SetCardWidth(_resizeStartWidth + _resizeSign * deltaPx / scale);
        }
        else
        {
            var deltaPx = cursorScreenY - _resizeStartCursorY;
            if (Math.Abs(deltaPx) < 1) return;
            var scale = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
            SetListHeight(_resizeStartListHeight + _resizeSign * deltaPx / scale);
        }
    }

    public void EndResize() { }

    /// <summary>The screen position the current resize started from (probe hook).</summary>
    public (int X, int Y) ResizeStartCursor => (_resizeStartCursorX, _resizeStartCursorY);

    /// <summary>
    /// A thin translucent scrollbar so it matches the card instead of the system's light
    /// theme. NOTE the xmlns:x declaration: without it `x:Name="PART_Track"` makes
    /// XamlReader throw, and the old version swallowed that in a bare catch and silently
    /// fell back to the white system scrollbar the user complained about.
    /// </summary>
    private void ApplyThinScrollBar(ScrollViewer scroll)
    {
        const string xaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="ScrollBar">
              <Setter Property="Width" Value="8"/>
              <Setter Property="Background" Value="Transparent"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="Transparent">
                      <Track x:Name="PART_Track" IsDirectionReversed="True">
                        <Track.DecreaseRepeatButton>
                          <RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/>
                        </Track.DecreaseRepeatButton>
                        <Track.Thumb>
                          <Thumb MinHeight="24">
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb">
                                <Border Background="#59A8B6D8" CornerRadius="4" Margin="1,0,1,0"/>
                              </ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton>
                          <RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/>
                        </Track.IncreaseRepeatButton>
                      </Track>
                    </Grid>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """;
        try
        {
            var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
            StyleDiagnostics = "thin translucent scrollbar applied";
        }
        catch (Exception ex)
        {
            StyleDiagnostics = "scrollbar style FAILED (system scrollbar in use): " + ex.Message;
        }
    }

    /// <summary>Set the visible list height (the user's DESIRED height; persisted).</summary>
    public void SetListHeight(double dip)
    {
        var clamped = Math.Clamp(dip, MinListHeight, MaxListHeight);
        if (Math.Abs(clamped - _desiredListHeight) < 0.5) return;
        _desiredListHeight = clamped;
        _effectiveListHeight = clamped;
        _scroll.MaxHeight = clamped;
        Resized?.Invoke();
    }

    /// <summary>Set the card width (the user's DESIRED width; persisted).</summary>
    public void SetCardWidth(double dip)
    {
        var clamped = Math.Clamp(dip, MinCardWidth, MaxCardWidth);
        if (Math.Abs(clamped - _desiredWidth) < 0.5) return;
        _desiredWidth = clamped;
        Width = clamped;
        Resized?.Invoke();
    }

    /// <summary>
    /// The height actually applied right now. The window may have to cap it when the
    /// card would not fit beside the pet, but that cap must NOT overwrite what the user
    /// asked for — doing so made a drag feel like it was being eaten.
    /// </summary>
    public double ListHeight => _desiredListHeight;
    public double EffectiveListHeight => _effectiveListHeight;

    /// <summary>Re-apply the user's desired height when space frees up again.</summary>
    public void RestoreDesiredListHeight()
    {
        if (Math.Abs(_effectiveListHeight - _desiredListHeight) < 0.5) return;
        _effectiveListHeight = _desiredListHeight;
        _scroll.MaxHeight = _desiredListHeight;
    }

    /// <summary>Diagnostics for the scrollbar style (logged so a silent fallback cannot hide).</summary>
    public string? StyleDiagnostics { get; private set; }

    /// <summary>
    /// Give back DIP to the scrollable list when the cards would not fit — a TEMPORARY cap.
    /// Returns how much was actually reclaimed.
    /// </summary>
    public double ShrinkList(double dip)
    {
        var target = Math.Max(MinListHeight, _effectiveListHeight - dip);
        var applied = _effectiveListHeight - target;
        if (applied <= 0.5) return 0;
        _effectiveListHeight = target;
        _scroll.MaxHeight = target;
        return applied;
    }

    private Border Chip(TextBlock text, Action onClick)
    {
        var border = new Border
        {
            Child = text,
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x6A, 0xA8, 0xFF)),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 5, 0),
        };
        text.FontFamily = Font; text.FontSize = 11; text.Foreground = Value;
        border.MouseLeftButtonUp += (_, _) => onClick();
        return border;
    }

    private string AgeLabel() => _tempAgeDays <= 0 ? "临时文件：全部" : $"临时文件：{_tempAgeDays} 天前";

    /// <summary>7 days -> 1 day -> everything -> 7 days. Deepening is what makes this a big cleanup.</summary>
    private void CycleAge()
    {
        _tempAgeDays = _tempAgeDays switch { 7 => 1, 1 => 0, _ => 7 };
        SetTempAge(_tempAgeDays);
        TempAgeChanged?.Invoke(_tempAgeDays);
    }

    /// <summary>Rebuild the target definitions with a new temp age and relabel that row.</summary>
    public void SetTempAge(int days)
    {
        _tempAgeDays = days;
        _targets = CleanupCatalog.Build(days).ToList();
        foreach (var row in _rows)
        {
            var updated = _targets.FirstOrDefault(t => t.Id == row.Target.Id);
            if (updated is null) continue;
            row.Target = updated;
            if (updated.Id == "user-temp") row.Name.Text = updated.Label;
        }
        _ageText.Text = AgeLabel();
    }


    private void BuildRows()
    {
        foreach (var group in _groupList)
        {
            foreach (var target in _targets.Where(t => t.Group == group.Title))
            {
                var row = BuildRow(target);
                _rows.Add(row);
                group.Body.Children.Add(row.DetailBox.Parent as UIElement ?? throw new InvalidOperationException());
            }
        }
    }

    private Row BuildRow(CleanupTarget target)
    {
        var container = new StackPanel { Margin = new Thickness(0, 2, 0, 3) };

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // clicking the name area expands the full explanation
        var name = new TextBlock
        {
            Text = target.Label, FontFamily = Font, FontSize = 11, Foreground = Value,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = Cursors.Hand,
        };
        Grid.SetColumn(name, 0);
        line.Children.Add(name);

        var size = new TextBlock
        {
            Text = "—", FontFamily = Font, FontSize = 11, Foreground = Label,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
        };
        Grid.SetColumn(size, 1);
        line.Children.Add(size);

        var elevatable = CleanupCatalog.MayExecuteElevated(target);
        var clickable = target.Executable || elevatable;
        var actionText = new TextBlock
        {
            Text = target.Tier switch
            {
                CleanupTier.AdminOnly => elevatable ? "提权清理" : "需管理员",
                CleanupTier.HandsOff => "不建议",
                _ => target.Method == CleanupMethod.EmptyRecycleBin ? "清空" : "清理",
            },
            FontFamily = Font, FontSize = 11,
            Foreground = target.Tier switch
            {
                CleanupTier.AdminOnly => elevatable ? Warn : Muted,
                CleanupTier.HandsOff => Muted,
                _ => Accent,
            },
        };
        var actionChip = new Border
        {
            Child = actionText,
            Padding = new Thickness(7, 3, 7, 3),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x6A, 0xA8, 0xFF)),
            Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(actionChip, 2);
        line.Children.Add(actionChip);
        container.Children.Add(line);

        // ---- verdict + impact, always visible (this is the part that was missing) ----
        var explain = new TextBlock
        {
            FontFamily = Font, FontSize = 10.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var verdictRun = new System.Windows.Documents.Run(Badge(target) + "  ")
        {
            Foreground = target.Tier switch
            {
                CleanupTier.Safe => Good,
                CleanupTier.Caution => Warn,
                CleanupTier.AdminOnly => Muted,
                _ => Muted,
            },
        };
        explain.Inlines.Add(verdictRun);
        explain.Inlines.Add(new System.Windows.Documents.Run(target.Impact) { Foreground = Label });
        container.Children.Add(explain);

        // ---- full explanation, shown when expanded ----
        var detailText = new TextBlock
        {
            FontFamily = Font, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = Label,
            Margin = new Thickness(6, 4, 4, 2),
        };
        detailText.Inlines.Add(new System.Windows.Documents.Run("作用：") { Foreground = Accent, FontWeight = FontWeights.SemiBold });
        detailText.Inlines.Add(new System.Windows.Documents.Run(target.Detail));
        detailText.Inlines.Add(new System.Windows.Documents.Run("\n删除影响：") { Foreground = Accent, FontWeight = FontWeights.SemiBold });
        detailText.Inlines.Add(new System.Windows.Documents.Run(target.Impact));
        detailText.Inlines.Add(new System.Windows.Documents.Run("\n结论：") { Foreground = Accent, FontWeight = FontWeights.SemiBold });
        detailText.Inlines.Add(new System.Windows.Documents.Run(target.Verdict));
        if (!target.Executable)
            detailText.Inlines.Add(new System.Windows.Documents.Run(target.Tier == CleanupTier.HandsOff
                ? "\n程序不会清理这一项，只报告它占用多少。"
                : "\n需要管理员权限，程序不代你执行。"));
        var detailBox = new Border
        {
            Child = detailText,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x6A, 0xA8, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        container.Children.Add(detailBox);

        // ---- the confirm / cancel pair ----
        var confirmText = new TextBlock { Text = "确认", FontFamily = Font, FontSize = 11, Foreground = Danger };
        var confirmChip = new Border
        {
            Child = confirmText, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x8A, 0x8A)),
            Cursor = Cursors.Hand, Margin = new Thickness(0, 3, 4, 0), Visibility = Visibility.Collapsed,
        };
        var cancelText = new TextBlock { Text = "取消", FontFamily = Font, FontSize = 11, Foreground = Label };
        var cancelChip = new Border
        {
            Child = cancelText, Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x9A, 0xB4, 0xE0)),
            Cursor = Cursors.Hand, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed,
        };

        var row = new Row
        {
            Target = target, Name = name, Size = size, ActionText = actionText, ActionChip = actionChip,
            ConfirmChip = confirmChip, CancelChip = cancelChip, VerdictText = explain, DetailBox = detailBox,
            Elevated = elevatable,
        };

        if (clickable)
        {
            actionChip.MouseLeftButtonUp += (_, _) => OnActionClicked(row);
            confirmChip.MouseLeftButtonUp += (_, _) => Execute(row);
            cancelChip.MouseLeftButtonUp += (_, _) => CancelArm(row, "取消");
        }
        else
        {
            ToolTipService.SetToolTip(actionChip, target.Tier == CleanupTier.HandsOff
                ? "程序不会清理这一项，理由见展开说明"
                : "需要管理员权限，这里只报告大小");
        }
        name.MouseLeftButtonUp += (_, _) =>
        {
            row.Expanded = !row.Expanded;
            detailBox.Visibility = row.Expanded ? Visibility.Visible : Visibility.Collapsed;
        };

        var confirmRow = new StackPanel { Orientation = Orientation.Horizontal };
        confirmRow.Children.Add(confirmChip);
        confirmRow.Children.Add(cancelChip);
        container.Children.Add(confirmRow);

        // keep a handle on the container for the caller
        row.DetailBox.Tag = container;
        return row;
    }

    private static string Badge(CleanupTarget target) => target.Tier switch
    {
        CleanupTier.Safe => "✔ 可放心定期清理",
        CleanupTier.Caution => "! 会重建或需重下",
        CleanupTier.AdminOnly => "· 需管理员",
        _ => "× 不建议清理",
    };

    private void OnActionClicked(Row row)
    {
        var now = Environment.TickCount64;
        // Clicking a different row's 清理 cancels whatever was armed first.
        if (_arm.ArmedId is not null && !_arm.IsArmed(row.Target.Id)) RevertVisual(_arm.ArmedId);
        if (_arm.Click(row.Target.Id, now)) Execute(row);
        else ArmVisual(row, _arm.SecondsLeft(now));
    }

    private void ArmVisual(Row row, int secondsLeft)
    {
        row.ActionChip.Visibility = Visibility.Collapsed;
        row.ConfirmChip.Visibility = Visibility.Visible;
        row.CancelChip.Visibility = Visibility.Visible;
        row.ConfirmChip.Opacity = 1;
        row.ActionText.Text = "确认";
        _summary.Text = $"「{row.Target.Label}」确认删除？{Math.Max(1, secondsLeft)} 秒内点「确认」执行，或点「取消」退出。";
    }

    private void RevertVisual(string id)
    {
        var row = _rows.FirstOrDefault(r => r.Target.Id == id);
        if (row is null) return;
        row.ActionChip.Visibility = Visibility.Visible;
        row.ConfirmChip.Visibility = Visibility.Collapsed;
        row.CancelChip.Visibility = Visibility.Collapsed;
        row.ActionText.Text = row.Target.Method == CleanupMethod.EmptyRecycleBin ? "清空" : (row.Elevated ? "提权清理" : "清理");
    }

    private void CancelArm(Row row, string why)
    {
        _arm.Cancel();
        RevertVisual(row.Target.Id);
        _summary.Text = $"已取消（{why}），没有删除任何东西。";
    }

    private void Execute(Row row)
    {
        _arm.Cancel();
        RevertVisual(row.Target.Id);
        row.ActionText.Text = "处理中…";
        row.ActionText.Foreground = Label;
        row.ActionChip.IsEnabled = false;
        if (row.Elevated)
        {
            _summary.Text = $"正在为「{row.Target.Label}」请求管理员权限（会弹出 UAC 确认框）…";
            ElevatedCleanRequested?.Invoke(row.Target);
            return;
        }
        _summary.Text = $"正在清理「{row.Target.Label}」…";
        CleanRequested?.Invoke(row.Target);
    }

    private void ToggleShowAbsent()
    {
        _showAbsent = !_showAbsent;
        ApplyPresence();
    }

    private void UpdateGroupHeader(Group group)
    {
        var items = _rows.Where(r => r.Target.Group == group.Title && IsPresent(r.Target)).ToList();
        long total = 0;
        foreach (var row in items)
        {
            var hit = _scans.FirstOrDefault(s => s.Target.Id == row.Target.Id);
            if (hit.Target is not null) total += Math.Max(0, hit.Scan.Bytes);
        }
        var arrow = group.Collapsed ? "▸" : "▾";
        group.Header.Text = items.Count == 0
            ? $"{arrow} {group.Title}"
            : $"{arrow} {group.Title} · {items.Count} 项 · {Monitoring.Fmt.Bytes((ulong)total)}";
    }

    private bool IsPresent(CleanupTarget target)
    {
        var hit = _scans.FirstOrDefault(s => s.Target.Id == target.Id);
        if (hit.Target is null) return true;                      // not scanned yet
        return hit.Scan.Exists || hit.Scan.Bytes > 0;
    }

    private void ApplyPresence()
    {
        foreach (var group in _groupList)
        {
            var container = group.Body;
            var visible = 0;
            foreach (var child in container.Children.OfType<FrameworkElement>())
            {
                var target = _rows.FirstOrDefault(r => ReferenceEquals(r.DetailBox.Tag, child))?.Target;
                if (target is null) continue;
                var show = _showAbsent || IsPresent(target);
                child.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show) visible++;
            }
            group.Body.Visibility = group.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            UpdateGroupHeader(group);
        }
        var absent = _rows.Count(r => !IsPresent(r.Target));
        _showAllText.Text = _showAbsent ? "隐藏未发现的" : $"显示未发现的 {absent} 项";
        _showAll.Visibility = absent == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Called from the window's hit timer: expires a stale arm and reverts the row.</summary>
    public void Tick()
    {
        var now = Environment.TickCount64;
        if (_arm.ArmedId is not null)
        {
            var id = _arm.ArmedId;
            var row = _rows.FirstOrDefault(r => r.Target.Id == id);
            if (row is not null && row.ConfirmChip.Visibility == Visibility.Visible)
            {
                var left = _arm.SecondsLeft(now);
                if (left > 0) row.ActionText.Text = $"确认({left})";
            }
        }
        if (_arm.ExpireIfNeeded(now))
        {
            // visual revert of the row that timed out
            foreach (var row in _rows)
                if (row.ConfirmChip.Visibility == Visibility.Visible)
                {
                    RevertVisual(row.Target.Id);
                    _summary.Text = "确认超时，已自动取消（没有删除任何东西）。";
                }
        }
    }

    public string SummaryText => _summary.Text;

    public void SetBusy(string message) => _summary.Text = message;

    public void SetScans(IReadOnlyList<(CleanupTarget Target, CleanupScan Scan)> scans)
    {
        _scans = scans;
        long total = 0;
        long safeTotal = 0;
        var biggest = "";
        long biggestBytes = 0;

        foreach (var row in _rows)
        {
            var hit = scans.FirstOrDefault(s => s.Target.Id == row.Target.Id);
            if (hit.Target is null) { row.Size.Text = "—"; continue; }
            var scan = hit.Scan;
            if (scan.Bytes == 0 && scan.Error is not null)
            {
                // e.g. WinSxS: the size is not measurable (and would be meaningless)
                row.Size.Text = "需分析";
                row.ActionText.Text = row.Target.Tier == CleanupTier.AdminOnly ? "需管理员" : "";
                continue;
            }
            if (!scan.Exists && scan.Bytes == 0)
            {
                row.Size.Text = "无";
                row.ActionText.Text = row.Target.Tier == CleanupTier.AdminOnly ? "需管理员" : "";
                continue;
            }
            row.Size.Text = Monitoring.Fmt.Bytes((ulong)Math.Max(0, scan.Bytes));
            if (scan.Bytes > biggestBytes) { biggestBytes = scan.Bytes; biggest = row.Target.Label; }
            if (row.Target.Executable)
            {
                total += scan.Bytes;
                if (row.Target.SafeRegularly) safeTotal += scan.Bytes;
            }
        }

        ApplyPresence();
        _safeBatchText.Text = $"一键清理放心项（{Monitoring.Fmt.Bytes((ulong)safeTotal)}）";
        _summary.Text = total == 0
            ? "没有可回收的内容（或都不在允许范围内）"
            : $"可回收 {Monitoring.Fmt.Bytes((ulong)total)}" + (biggest.Length > 0 ? $" · 最大项：{biggest}" : "")
              + $"；其中放心项 {Monitoring.Fmt.Bytes((ulong)safeTotal)}。";
    }

    public void SetResult(CleanupResult result)
    {
        var row = _rows.FirstOrDefault(r => r.Target.Id == result.TargetId);
        if (row is not null)
        {
            row.ActionChip.IsEnabled = true;
            row.ActionText.Foreground = result.Error is null ? Good : Danger;
            row.ActionText.Text = result.Error is null
                ? (result.Deleted > 0 ? $"已清理 {result.Deleted}" : "完成")
                : "失败";
        }
        _summary.Text = result.Error is null
            ? $"已清理 {Monitoring.Fmt.Bytes((ulong)Math.Max(0, result.Bytes))}"
              + (result.Skipped > 0 ? $"，{result.Skipped} 项被跳过（占用中）" : "")
              // 只有回收站真的接住了才敢说"可还原"（组策略关掉回收站时 Shell 是永久删除）。
              + (RecycleBin.CanUndelete() ? "（已进回收站，可还原）" : "（已永久删除，回收站已被关闭，无法还原）")
            : (row?.Target.Label ?? result.TargetId) + "：" + result.Error;
    }

    /// <summary>Probe helper: report the scan table the UI is showing.</summary>
    public string DescribeScans(IReadOnlyList<(CleanupTarget Target, CleanupScan Scan)> scans)
    {
        var parts = scans.Select(s =>
            $"{s.Target.Id}={(s.Scan.Exists || s.Scan.Bytes > 0 ? Monitoring.Fmt.Bytes((ulong)Math.Max(0, s.Scan.Bytes)) : "absent")}/{s.Scan.Files}f/{s.Target.Tier}"
            + (s.Scan.Error is null ? "" : $"({s.Scan.Error})"));
        return string.Join("  ", parts);
}
}
