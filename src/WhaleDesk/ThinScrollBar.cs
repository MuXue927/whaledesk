using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace WhaleDesk;

/// <summary>
/// The thin translucent scrollbar used across all cards/windows.
///
/// It lives in one place now: the analysis window showed the default white system scrollbar,
/// which clashed with the dark UI the user already complained about once.
/// </summary>
internal static class ThinScrollBar
{
    private const string Xaml = """
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

    /// <summary>Applies the dark thin scrollbar; falls back to the system one on failure.</summary>
    public static string Apply(ScrollViewer scroll)
    {
        try
        {
            var style = (Style)XamlReader.Parse(Xaml);
            scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
            return "thin translucent scrollbar applied";
        }
        catch (Exception ex)
        {
            return "scrollbar style FAILED (system scrollbar in use): " + ex.Message;
        }
    }
}
