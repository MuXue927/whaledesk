using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WhaleDesk;

/// <summary>
/// The single visual tree used by BOTH the offscreen renderer and the live
/// window. That is the point: rendering it offscreen validates exactly what the
/// window shows, not a parallel implementation that can drift.
///
/// Row order (top to bottom): spacer(*) / menu(auto) / bubble(auto) / pet(auto).
/// The spacer eats the leftover height so the pet stays glued to the window's
/// bottom edge; the menu and bubble stack directly above her.
/// </summary>
internal sealed class PetVisual : Grid
{
    private readonly AtlasLoader _atlas;
    private readonly Border _bubble;
    private readonly TextBlock _bubbleText;
    private readonly Image _petImage;
    public MonitorPanel Panel { get; } = new();
    public CleanupPanel Cleanup { get; } = new();
    public double PetHeight { get; }
    public double PetWidth { get; }
    public Image PetImage => _petImage;
    private bool _cardBelow;
    /// <summary>True when the card block currently sits below the pet.</summary>
    public bool CardBelow => _cardBelow;

    public PetVisual(AtlasLoader atlas, PetSpec spec, double petHeight)
    {
        _atlas = atlas;
        PetHeight = Math.Round(petHeight);
        PetWidth = Math.Round(petHeight * atlas.CellW / atlas.CellH);

        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // top spacer
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                          // bottom spacer

        _bubbleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xFF)),
            FontSize = 13,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 210,
            TextAlignment = TextAlignment.Left,
        };
        _bubble = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x16, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x7A, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 6, 10, 7),
            Margin = new Thickness(0, 0, 0, 7),
            Child = _bubbleText,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        _petImage = new Image
        {
            Width = PetWidth,
            Height = PetHeight,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        RenderOptions.SetBitmapScalingMode(_petImage, BitmapScalingMode.HighQuality);

        Panel.HorizontalAlignment = HorizontalAlignment.Right;
        Panel.VerticalAlignment = VerticalAlignment.Bottom;
        Cleanup.HorizontalAlignment = HorizontalAlignment.Right;
        Cleanup.VerticalAlignment = VerticalAlignment.Bottom;
        // 7 rows so the card block can sit either above or below the pet: the row order is
        // reassigned by SetCardSide and the free side keeps the '*' spacer.
        SetRow(Panel, 1);
        SetRow(Cleanup, 3);
        SetRow(_bubble, 4);
        SetRow(_petImage, 5);
        Children.Add(Panel);
        Children.Add(Cleanup);
        Children.Add(_bubble);
        Children.Add(_petImage);
        SetCardSide(below: false);
        SetFrame(0, 0);

        // Resize hover + drag for the cleanup card, decided at the WINDOW level so the band
        // also covers the area just OUTSIDE the card (inside-only hit areas felt wrong).
        PreviewMouseMove += OnResizePreviewMove;
        PreviewMouseLeftButtonDown += OnResizePreviewDown;
        PreviewMouseLeftButtonUp += OnResizePreviewUp;
    }

    private bool _resizingCard;
    private string? _hoverEdge;

    private void OnResizePreviewMove(object sender, MouseEventArgs e)
    {
        if (_resizingCard)
        {
            if (Win32.GetCursorPos(out var cursor)) Cleanup.UpdateResize(cursor.X, cursor.Y);
            return;
        }
        var edge = Cleanup.EdgeAt(e.GetPosition(Cleanup));
        if (edge != _hoverEdge)
        {
            _hoverEdge = edge;
            Cursor = edge is null
                ? null
                : CleanupPanel.IsHorizontalEdge(edge) ? Cursors.SizeWE : Cursors.SizeNS;
        }
    }

    private void OnResizePreviewDown(object sender, MouseButtonEventArgs e)
    {
        var edge = Cleanup.EdgeAt(e.GetPosition(Cleanup));
        if (edge is null) return;
        _resizingCard = true;
        _hoverEdge = edge;
        Cleanup.BeginResize(CleanupPanel.IsHorizontalEdge(edge), CleanupPanel.OutwardSign(edge));
        CaptureMouse();
        e.Handled = true;      // do not let the click reach a row or a button under the band
    }

    private void OnResizePreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizingCard) return;
        _resizingCard = false;
        Cleanup.EndResize();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>
    /// Put the card block above (default) or below the pet. The pet's own screen
    /// position is preserved by the window, so a pet near the top of the screen gets
    /// its panel underneath instead of having the panel run off the screen.
    /// </summary>
    public void SetCardSide(bool below)
    {
        if (_cardBelow == below) return;
        _cardBelow = below;
        if (below)
        {
            SetRow(_bubble, 1);
            SetRow(_petImage, 2);
            SetRow(Panel, 3);
            SetRow(Cleanup, 5);
            RowDefinitions[0].Height = GridLength.Auto;
            RowDefinitions[6].Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            SetRow(Panel, 1);
            SetRow(Cleanup, 3);
            SetRow(_bubble, 4);
            SetRow(_petImage, 5);
            RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            RowDefinitions[6].Height = GridLength.Auto;
        }
    }

    /// <summary>Which screen half the card hugs, so it grows toward the middle of the screen.</summary>
    public void SetCardAlignment(bool left)
    {
        var h = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Panel.HorizontalAlignment = h;
        Cleanup.HorizontalAlignment = h;
    }

    /// <summary>True (default) = pet hugs the bottom-right corner; false = centred (offscreen sheets).</summary>
    public void SetAnchorRight(bool right)
    {
        var h = right ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        _petImage.HorizontalAlignment = h;
        _bubble.HorizontalAlignment = h;
        Panel.HorizontalAlignment = h;
        Cleanup.HorizontalAlignment = h;
    }

    public void SetFrame(int row, int col) => _petImage.Source = _atlas.Frame(row, col);

    public void SetBubble(string? text)
    {
        var has = !string.IsNullOrWhiteSpace(text);
        _bubbleText.Text = has ? text : string.Empty;
        _bubble.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    public string BubbleText => _bubbleText.Text;
}
