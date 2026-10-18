namespace WhaleDesk;

/// <summary>
/// Frame clock for one track at a time. Uses a monotonic millisecond clock so a
/// system clock change or a long sleep cannot rewind the animation. Non-looping
/// tracks (jumping / failed) fall back to their declared fallback track once.
/// </summary>
internal sealed class TrackPlayer
{
    private readonly PetSpec _spec;
    private PetTrack _track;
    private int _frame;
    private long _frameStartMs;

    public event Action<string>? TrackFinished;

    public TrackPlayer(PetSpec spec)
    {
        _spec = spec;
        _track = spec.Tracks[0];
    }

    public PetTrack Track => _track;
    public string TrackName => _track.Name;
    public int FrameIndex => _frame;
    public int FrameColumn => _track.Frames[Math.Clamp(_frame, 0, _track.Frames.Length - 1)];

    /// <summary>Switch track. Replaying the same track is a no-op unless restart is set.</summary>
    public bool Play(string? name, long nowMs, bool restart = false)
    {
        var next = _spec.Track(name);
        if (!restart && ReferenceEquals(next, _track)) return false;
        _track = next;
        _frame = 0;
        _frameStartMs = nowMs;
        return true;
    }

    /// <summary>Advance to the frame that belongs to <paramref name="nowMs"/>. True when the frame changed.</summary>
    public bool Tick(long nowMs)
    {
        var changed = false;
        var guard = 0;
        while (guard++ < 512)
        {
            var duration = _track.Durations[Math.Clamp(_frame, 0, _track.Durations.Length - 1)];
            if (nowMs - _frameStartMs < duration) break;
            _frameStartMs += duration;
            if (_frame + 1 < _track.Frames.Length)
            {
                _frame++;
                changed = true;
                continue;
            }
            if (_track.Loop)
            {
                _frame = 0;
                changed = true;
                continue;
            }
            var fallback = _track.Fallback ?? _spec.Tracks[0].Name;
            _track = _spec.Track(fallback);
            _frame = 0;
            _frameStartMs = nowMs;
            changed = true;
            TrackFinished?.Invoke(_track.Name);
            break;
        }
        // After a sleep/resume the frame clock can lag minutes behind; never spin.
        if (nowMs - _frameStartMs > 5000) _frameStartMs = nowMs;
        return changed;
    }
}
