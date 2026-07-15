namespace Huaci.App.Views;

/// <summary>
/// Pure state machine for the toast's pointer-leave and fallback timeout behavior.
/// Keeping this independent from WPF makes the interaction boundaries deterministic.
/// </summary>
public sealed class ToastDismissPolicy
{
    private readonly double _nearDistance;
    private readonly double _leaveDistance;
    private readonly TimeSpan _leaveDebounce;

    private DateTimeOffset _shownAt;
    private DateTimeOffset? _mouseLeaveAt;
    private DateTimeOffset? _cursorOutsideAt;
    private bool _cursorWasNear;

    public ToastDismissPolicy(
        double nearDistance = 10,
        double leaveDistance = 24,
        TimeSpan? leaveDebounce = null)
    {
        if (nearDistance < 0 || leaveDistance <= nearDistance)
        {
            throw new ArgumentOutOfRangeException(nameof(leaveDistance));
        }

        _nearDistance = nearDistance;
        _leaveDistance = leaveDistance;
        _leaveDebounce = leaveDebounce ?? TimeSpan.Zero;
    }

    public bool IsPinned { get; private set; }

    public void BeginPresentation(DateTimeOffset now, bool pointerIsOver)
    {
        _shownAt = now;
        _mouseLeaveAt = null;
        _cursorOutsideAt = null;
        _cursorWasNear = pointerIsOver;
    }

    public void PointerEntered()
    {
        if (IsPinned)
        {
            return;
        }

        _cursorWasNear = true;
        _mouseLeaveAt = null;
        _cursorOutsideAt = null;
    }

    public bool PointerLeft(DateTimeOffset now)
    {
        if (IsPinned || !_cursorWasNear)
        {
            return false;
        }

        _mouseLeaveAt ??= now;
        return true;
    }

    public void SetPinned(bool pinned, DateTimeOffset now, bool pointerIsOver)
    {
        IsPinned = pinned;
        _mouseLeaveAt = null;
        _cursorOutsideAt = null;

        if (!pinned)
        {
            _shownAt = now;
            _cursorWasNear = pointerIsOver;
        }
    }

    public bool ShouldDismiss(
        DateTimeOffset now,
        bool pointerIsOver,
        double? cursorDistance,
        TimeSpan fallbackLifetime)
    {
        if (IsPinned)
        {
            return false;
        }

        if (pointerIsOver)
        {
            PointerEntered();
            return false;
        }

        if (cursorDistance is { } distance)
        {
            if (distance <= _nearDistance)
            {
                PointerEntered();
            }
            else if (_cursorWasNear && distance >= _leaveDistance)
            {
                _cursorOutsideAt ??= now;
            }
        }

        return (_mouseLeaveAt is { } mouseLeaveAt && now - mouseLeaveAt >= _leaveDebounce)
            || (_cursorOutsideAt is { } cursorOutsideAt && now - cursorOutsideAt >= _leaveDebounce)
            || now - _shownAt >= fallbackLifetime;
    }
}
