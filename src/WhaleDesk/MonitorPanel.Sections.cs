using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shapes;
using WhaleDesk.Cleanup;
using WhaleDesk.Monitoring;

namespace WhaleDesk;

/// <summary>Big-directory + usage + hog rows rendering, split out of MonitorPanel.</summary>
internal sealed partial class MonitorPanel
{

    private void RebuildHogs(IReadOnlyList<HogRow> families)
    {
        _hogs.Children.Clear();
        var shown = 0;
        var skipped = 0;
        foreach (var family in families)
        {
            // Never offer something we would refuse anyway: it wasted the three visible slots
            // on DSH/explorer rows the user could not act on.
            if (!Performance.MemoryGuard.CanKill(family.Name, family.Pids.FirstOrDefault(), family.MaxCpuPercent, out var reason))
            {
                skipped++;
                continue;
            }
            if (shown >= 3) { skipped++; continue; }
            shown++;

            var line = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x3A, 0x8A, 0xB4, 0xFF)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // name
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // memory
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                        // tag

            var name = new TextBlock
            {
                Text = family.Count > 1 ? $"{family.Name} ×{family.Count}" : family.Name,
                FontFamily = Font,
                FontSize = 11,
                Foreground = Value,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var mem = new TextBlock
            {
                Text = Fmt.Bytes(family.WorkingSet),
                FontFamily = Font,
                FontSize = 11,
                Foreground = Label,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var tag = new TextBlock
            {
                Text = "",
                FontFamily = Font,
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x6A)),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(mem, 1);
            Grid.SetColumn(tag, 2);
            grid.Children.Add(name);
            grid.Children.Add(mem);
            grid.Children.Add(tag);
            line.Child = grid;
            line.ToolTip = reason;

            var captured = family;
            line.MouseLeftButtonUp += (_, _) => KillRequested?.Invoke(captured);
            _hogs.Children.Add(line);
        }

        if (shown == 0)
        {
            _hogs.Children.Add(new TextBlock
            {
                Text = "没有可以安全结束的大户",
                FontFamily = Font, FontSize = 10.5, Foreground = Label,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        if (skipped > 0)
        {
            // The user asked to be told about them: they are real memory users, we just will
            // not touch them.
            _hogs.Children.Add(new TextBlock
            {
                Text = $"另有 {skipped} 个受保护进程（系统/DSH/我自己，不会动它们）",
                FontFamily = Font, FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xA8, 0xB6, 0xD8)),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }
    /// <summary>Token/cost line for today, from the dsh-usage ledger.</summary>
    /// <summary>Cap the card height (the window is sized from it, and the pet lives below).</summary>
    public void SetMaxHeight(double dip) => _scroll.MaxHeight = Math.Max(200, Math.Min(DefaultMaxHeight * 2, dip));

    public void SetUsage(UsageTotals usage)
    {
        if (usage.Calls == 0)
        {
            _usage.Text = "今日 tokens：暂无记账（未安装 dsh-usage 或今天还没调用）";
            return;
        }
        _usage.Text = $"今日 tokens {Fmt.Tokens(usage.TotalTokens)}（入 {Fmt.Tokens(usage.InputTokens)} · 出 {Fmt.Tokens(usage.OutputTokens)} · 缓存 {Fmt.Tokens(usage.CacheReadTokens)}）· {usage.Calls} 次 · 成本 ≈¥{usage.Cost:0.00}";
    }

    /// <summary>Big directories, biggest first; clicking one opens it.</summary>
    public void SetBigDirectories(IReadOnlyList<BigDirectory> directories, bool scanning)
    {
        _bigDirs.Children.Clear();
        if (scanning)
        {
            _bigDirs.Children.Add(new TextBlock
            {
                Text = "正在测量大目录…（只读，不会删除）",
                FontFamily = Font, FontSize = 10, Foreground = Label, Margin = new Thickness(0, 1, 0, 1),
            });
            return;
        }
        if (directories.Count == 0)
        {
            _bigDirs.Children.Add(new TextBlock
            {
                Text = "没有测到（可能被权限挡住）",
                FontFamily = Font, FontSize = 10, Foreground = Label, Margin = new Thickness(0, 1, 0, 1),
            });
            return;
        }

        var biggest = directories[0].Bytes;
        foreach (var dir in directories.Take(8))
        {
            var line = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x2E, 0x8A, 0xB4, 0xFF)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                ToolTip = dir.Path,
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = dir.Label, FontFamily = Font, FontSize = 10.5, Foreground = Value,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            var size = new TextBlock
            {
                Text = (dir.Partial ? "≥" : "") + Fmt.Bytes(dir.Bytes), FontFamily = Font, FontSize = 10.5,
                Foreground = dir.Bytes > biggest * 0.5 ? new SolidColorBrush(Color.FromRgb(0xE0, 0xA8, 0x6A)) : Label,
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(size, 1);
            grid.Children.Add(name);
            grid.Children.Add(size);
            line.Child = grid;
            var captured = dir.Path;
            line.MouseLeftButtonUp += (_, _) => AnalyzeRequested?.Invoke(captured);
            line.MouseRightButtonUp += (_, _) => OpenPathRequested?.Invoke(captured);
            _bigDirs.Children.Add(line);
        }
    }

    /// <summary>Same thin translucent scrollbar as the cleanup card (the user disliked the white one).</summary>
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
        }
        catch { /* fall back to the system scrollbar rather than failing to show the card */ }
    }

}
