namespace WhaleDesk.Interaction;

/// <summary>
/// The input plumbing for the hidden interactions, separated so the ordering rules can be
/// asserted. Two real bugs lived exactly here:
///
///  1. The click handler checked `e.ClickCount >= 2` FIRST and returned, so the combo
///     counter only ever saw the first click of a burst — 6 clicks could never register.
///  2. The long-press timer required `!dragging`, but a press always starts a drag, so it
///     could never fire either.
/// </summary>
internal static class InteractionInput
{
    /// <summary>
    /// Every press is counted, including the ones Windows reports as ClickCount 2, 3, …
    /// The double-click celebration rides along instead of swallowing the burst.
    /// </summary>
    public static (Reaction Reaction, bool Celebrate) OnPress(EasterEggs eggs, int clickCount, long now)
    {
        var reaction = eggs.RegisterClick(now);
        return (reaction, clickCount == 2);
    }

    /// <summary>A long press means: still held, nothing moved, no card open, and on the pet.</summary>
    public static bool IsLongPress(bool dragging, bool moved, bool cardOpen, bool overPet) =>
        dragging && !moved && !cardOpen && overPet;
}
