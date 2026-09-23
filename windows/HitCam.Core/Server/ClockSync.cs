namespace HitCam.Core.Server;

/// <summary>
/// Estimates the offset between the phone clock and the PC clock from Ping/Pong round trips
/// (NTP-style, keeping the sample with the smallest round trip), so frame latency can be measured.
/// </summary>
public sealed class ClockSync
{
    private const int Window = 16;
    private readonly Queue<(long Rtt, long Offset)> _samples = new();

    /// <summary>Phone clock minus PC clock, in microseconds; null until the first sample.</summary>
    public long? OffsetMicros { get; private set; }

    public long? RoundTripMicros { get; private set; }

    /// <param name="pcSent">PC clock when the ping was sent.</param>
    /// <param name="phoneReplied">Phone clock when it sent the pong.</param>
    /// <param name="pcReceived">PC clock when the pong arrived.</param>
    public void AddSample(ulong pcSent, ulong phoneReplied, ulong pcReceived)
    {
        if (pcReceived < pcSent)
            return;

        var rtt = (long)(pcReceived - pcSent);
        var midpoint = (long)pcSent + rtt / 2;
        _samples.Enqueue((rtt, (long)phoneReplied - midpoint));
        if (_samples.Count > Window)
            _samples.Dequeue();

        var best = _samples.MinBy(s => s.Rtt);
        OffsetMicros = best.Offset;
        RoundTripMicros = rtt;
    }

    /// <summary>Converts a phone timestamp to PC time, or null if the offset is not known yet.</summary>
    public ulong? ToLocal(ulong phoneTimestamp) =>
        OffsetMicros is { } offset ? (ulong)((long)phoneTimestamp - offset) : null;
}
