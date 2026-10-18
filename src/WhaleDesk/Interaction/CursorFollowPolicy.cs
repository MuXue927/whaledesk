namespace WhaleDesk.Interaction;

/// <summary>One follow decision: move the pet by (Dx, Dy) pixels, or not, plus the reason.</summary>
internal sealed record FollowDecision(bool Move, int Dx, int Dy, string Why);

/// <summary>
/// The "follow the cursor" easter egg, as a pure decision function so it can be tested and probed
/// without moving a window.
///
/// It only creeps when the user is away (no input for a while), the pet is free (not dragged, no
/// card open, not paused), and the cursor is *far* away. Once close it stops - otherwise the pet
/// would jitter around the pointer forever, which is annoying rather than cute.
/// </summary>
internal static class CursorFollowPolicy
{
    public static FollowDecision Decide(
        bool enabled,
        bool paused,
        bool dragging,
        bool cardOpen,
        int idleSeconds,
        int petLeft,
        int petTop,
        int petWidth,
        int petHeight,
        int cursorX,
        int cursorY,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom,
        int stepPx = 8,
        int nearPx = 110,
        int farPx = 280,
        int minIdleSeconds = 10)
    {
        if (!enabled) return new FollowDecision(false, 0, 0, "off");
        if (paused) return new FollowDecision(false, 0, 0, "paused");
        if (dragging) return new FollowDecision(false, 0, 0, "dragging");
        if (cardOpen) return new FollowDecision(false, 0, 0, "card-open");
        if (idleSeconds < minIdleSeconds) return new FollowDecision(false, 0, 0, "user-active");

        var petCenterX = petLeft + petWidth / 2;
        var petCenterY = petTop + petHeight / 2;
        var dx = cursorX - petCenterX;
        var dy = cursorY - petCenterY;

        var distance = (int)Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (distance <= nearPx) return new FollowDecision(false, 0, 0, "arrived");
        if (distance < farPx) return new FollowDecision(false, 0, 0, "close-enough");

        var stepX = Math.Clamp(dx, -stepPx, stepPx);
        var stepY = Math.Clamp(dy / 2, -stepPx, stepPx);   // vertical movement is gentler

        var nextX = Math.Clamp(petLeft + stepX, workLeft, Math.Max(workLeft, workRight - petWidth));
        var nextY = Math.Clamp(petTop + stepY, workTop, Math.Max(workTop, workBottom - petHeight));
        if (nextX == petLeft && nextY == petTop) return new FollowDecision(false, 0, 0, "edge");

        return new FollowDecision(true, nextX - petLeft, nextY - petTop, "following");
    }
}
