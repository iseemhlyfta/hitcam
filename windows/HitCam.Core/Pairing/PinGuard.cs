using System.Security.Cryptography;
using System.Text;

namespace HitCam.Core.Pairing;

public enum PinCheckResult
{
    Ok,
    Wrong,
    /// <summary>Too many wrong attempts: the connection must be closed.</summary>
    Exhausted,
}

/// <summary>
/// Issues 6-digit pairing PINs and limits guessing across connections: wrong attempts add up over
/// reconnects, and every <see cref="MaxAttempts"/> of them within <see cref="FailureWindow"/> block pairing,
/// first for <see cref="Lockout"/>, then twice as long each time up to <see cref="MaxLockout"/>.
/// A correct PIN, or a quiet <see cref="FailureWindow"/>, starts over.
/// </summary>
public sealed class PinGuard(TimeProvider? timeProvider = null)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;
    private string? _pin;
    // Wrong attempts since the last lockout, and lockouts in a row; neither is reset by a disconnect.
    private int _failures;
    private int _lockouts;

    /// <summary>Wrong PINs still allowed before the next lockout.</summary>
    public int AttemptsLeft
    {
        get
        {
            lock (_gate)
            {
                Forget(_time.GetUtcNow());
                return MaxAttempts - _failures;
            }
        }
    }

    /// <summary>Starts a pairing window and returns the PIN to show on screen, or null while locked out.</summary>
    public string? Begin()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now < _lockedUntil)
                return null;
            Forget(now);
            _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            return _pin;
        }
    }

    public PinCheckResult Check(string? candidate)
    {
        lock (_gate)
        {
            if (_pin is null)
                return PinCheckResult.Exhausted;

            if (candidate is { Length: 6 } && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_pin)))
            {
                _pin = null;
                _failures = 0;
                _lockouts = 0;
                _lastFailure = DateTimeOffset.MinValue;
                return PinCheckResult.Ok;
            }

            var now = _time.GetUtcNow();
            _lastFailure = now;
            if (++_failures < MaxAttempts)
                return PinCheckResult.Wrong;

            _pin = null;
            _failures = 0;
            var lockout = Lockout * Math.Pow(2, Math.Min(_lockouts, 16));
            _lockedUntil = now + (lockout < MaxLockout ? lockout : MaxLockout);
            _lockouts++;
            return PinCheckResult.Exhausted;
        }
    }

    /// <summary>Ends the pairing window (e.g. the phone disconnected). Wrong attempts made so far still count.</summary>
    public void Cancel()
    {
        lock (_gate)
            _pin = null;
    }

    /// <summary>Forgets old failures once nothing went wrong for a whole window after the last failure or lockout.</summary>
    private void Forget(DateTimeOffset now)
    {
        var quietSince = _lastFailure > _lockedUntil ? _lastFailure : _lockedUntil;
        if (quietSince != DateTimeOffset.MinValue && now - quietSince >= FailureWindow)
        {
            _failures = 0;
            _lockouts = 0;
            _lastFailure = DateTimeOffset.MinValue;
            _lockedUntil = DateTimeOffset.MinValue;
        }
    }
}
