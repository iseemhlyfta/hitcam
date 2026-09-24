namespace HitCam.Vision;

/// <summary>Results per second over the last second or so.</summary>
internal sealed class RateMeter
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private readonly Queue<TimeSpan> _times = new();

    public double Add(TimeSpan now)
    {
        _times.Enqueue(now);
        while (_times.Count > 2 && now - _times.Peek() > Window)
            _times.Dequeue();
        if (_times.Count < 2)
            return 0;
        var span = (now - _times.Peek()).TotalSeconds;
        return span > 0 ? (_times.Count - 1) / span : 0;
    }

    public void Reset() => _times.Clear();
}
