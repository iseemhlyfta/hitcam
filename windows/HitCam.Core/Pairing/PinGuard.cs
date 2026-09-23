using System.Security.Cryptography;

namespace HitCam.Core.Pairing;

public enum PinCheckResult
{
    Ok,
    Wrong,
    /// <summary>Too many wrong attempts: the connection must be closed.</summary>
    Exhausted,
}

/// <summary>
/// Issues 6-digit pairing PINs and limits guessing: 5 wrong attempts close the connection and
/// block new pairing attempts for 30 seconds.
/// </summary>
public sealed class PinGuard(TimeProvider? timeProvider = null)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;
    private string? _pin;
    private int _attemptsLeft;

    public bool IsLockedOut
    {
        get { lock (_gate) return _time.GetUtcNow() < _lockedUntil; }
    }

    public int AttemptsLeft
    {
        get { lock (_gate) return _attemptsLeft; }
    }

    /// <summary>Starts a pairing window and returns the PIN to show on screen, or null while locked out.</summary>
    public string? Begin()
    {
        lock (_gate)
        {
            if (_time.GetUtcNow() < _lockedUntil)
                return null;
            _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _attemptsLeft = MaxAttempts;
            return _pin;
        }
    }

    public PinCheckResult Check(string? candidate)
    {
        lock (_gate)
        {
            if (_pin is null || _attemptsLeft <= 0)
                return PinCheckResult.Exhausted;

            if (candidate is { Length: 6 } && candidate == _pin)
            {
                _pin = null;
                return PinCheckResult.Ok;
            }

            _attemptsLeft--;
            if (_attemptsLeft > 0)
                return PinCheckResult.Wrong;

            _pin = null;
            _lockedUntil = _time.GetUtcNow() + Lockout;
            return PinCheckResult.Exhausted;
        }
    }

    public void Cancel()
    {
        lock (_gate)
            _pin = null;
    }
}
