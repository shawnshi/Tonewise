namespace Tonewise.SyncHost.Workbench;

public enum SleepTimerMode
{
    Off,
    Countdown,
    EndOfTrack,
    EndOfWork,
}

public sealed record SleepTimerTickResult(
    bool IsActive,
    bool Expired,
    double FadeGainLinear,
    TimeSpan Remaining,
    string BadgeText
);

/// <summary>
/// Thread-safe sleep timer coordinator supporting timed countdown with 10-second fade-out,
/// end-of-track stop, and classical end-of-work-block stop.
/// </summary>
public sealed class SleepTimerEngine
{
    private const double DefaultFadeWindowSeconds = 10.0;

    private readonly Lock _lock = new();
    private SleepTimerMode _mode = SleepTimerMode.Off;
    private DateTime _targetEndUtc;
    private string? _armedWorkKey;

    public SleepTimerMode Mode
    {
        get { lock (_lock) return _mode; }
    }

    public void SetCountdown(int minutes, DateTime? utcNow = null)
    {
        int clampedMin = Math.Clamp(minutes, 1, 720);
        DateTime now = utcNow ?? DateTime.UtcNow;
        lock (_lock)
        {
            _mode = SleepTimerMode.Countdown;
            _targetEndUtc = now.AddMinutes(clampedMin);
            _armedWorkKey = null;
        }
    }

    public void SetEndOfTrack()
    {
        lock (_lock)
        {
            _mode = SleepTimerMode.EndOfTrack;
            _targetEndUtc = default;
            _armedWorkKey = null;
        }
    }

    public void SetEndOfWork(string? activeWorkKey)
    {
        lock (_lock)
        {
            _mode = SleepTimerMode.EndOfWork;
            _targetEndUtc = default;
            _armedWorkKey = string.IsNullOrWhiteSpace(activeWorkKey) ? null : activeWorkKey.Trim();
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _mode = SleepTimerMode.Off;
            _targetEndUtc = default;
            _armedWorkKey = null;
        }
    }

    public SleepTimerTickResult Tick(DateTime? utcNow = null)
    {
        DateTime now = utcNow ?? DateTime.UtcNow;
        lock (_lock)
        {
            switch (_mode)
            {
                case SleepTimerMode.EndOfTrack:
                    return new SleepTimerTickResult(true, false, 1.0, TimeSpan.Zero, "🌙 本曲");
                case SleepTimerMode.EndOfWork:
                    return new SleepTimerTickResult(true, false, 1.0, TimeSpan.Zero, "🌙 本作品");
                case SleepTimerMode.Countdown:
                {
                    TimeSpan remaining = _targetEndUtc - now;
                    if (remaining <= TimeSpan.Zero)
                    {
                        _mode = SleepTimerMode.Off;
                        _targetEndUtc = default;
                        _armedWorkKey = null;
                        return new SleepTimerTickResult(false, true, 0.0, TimeSpan.Zero, "");
                    }
                    double fadeGain = remaining.TotalSeconds < DefaultFadeWindowSeconds
                        ? Math.Clamp(remaining.TotalSeconds / DefaultFadeWindowSeconds, 0.0, 1.0)
                        : 1.0;
                    string badge = remaining.TotalHours >= 1.0
                        ? $"🌙 {(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                        : $"🌙 {remaining.Minutes:D2}:{remaining.Seconds:D2}";
                    return new SleepTimerTickResult(true, false, fadeGain, remaining, badge);
                }
                default:
                    return new SleepTimerTickResult(false, false, 1.0, TimeSpan.Zero, "");
            }
        }
    }

    public bool WillStopAtTransition(string? previousWorkKey, string? nextWorkKey, bool isQueueWrapAround = false)
    {
        lock (_lock)
        {
            return WillStopAtTransitionUnlocked(previousWorkKey, nextWorkKey, isQueueWrapAround);
        }
    }

    public bool ShouldStopAtTrackTransition(string? previousWorkKey, string? nextWorkKey, bool isQueueWrapAround = false)
    {
        lock (_lock)
        {
            if (!WillStopAtTransitionUnlocked(previousWorkKey, nextWorkKey, isQueueWrapAround))
            {
                return false;
            }
            _mode = SleepTimerMode.Off;
            _targetEndUtc = default;
            _armedWorkKey = null;
            return true;
        }
    }

    private bool WillStopAtTransitionUnlocked(string? previousWorkKey, string? nextWorkKey, bool isQueueWrapAround)
    {
        return _mode switch
        {
            SleepTimerMode.EndOfTrack => true,
            SleepTimerMode.EndOfWork =>
                ResolveStopForEndOfWork(previousWorkKey, nextWorkKey, isQueueWrapAround),
            _ => false,
        };
    }

    private bool ResolveStopForEndOfWork(string? previousWorkKey, string? nextWorkKey, bool isQueueWrapAround)
    {
        if (isQueueWrapAround)
        {
            return true;
        }
        string? effectivePrev = string.IsNullOrWhiteSpace(previousWorkKey)
            ? _armedWorkKey
            : previousWorkKey.Trim();
        if (string.IsNullOrWhiteSpace(effectivePrev))
        {
            return true;
        }
        string normalizedNext = (nextWorkKey ?? "").Trim();
        return !string.Equals(effectivePrev, normalizedNext, StringComparison.OrdinalIgnoreCase);
    }
}
