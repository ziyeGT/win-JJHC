using System.Windows.Threading;
using Huaci.App.Models;
using Huaci.App.Views;

namespace Huaci.App.Services.Notebook;

/// <summary>
/// Polls the local alarm store and presents due reminders one at a time. An
/// overdue alarm is intentionally kept until the flying banner is explicitly
/// dismissed, so reminders survive application restarts.
/// </summary>
public sealed class QuickNotebookAlarmCoordinator : IDisposable
{
    private readonly IQuickNotebookAlarmService _alarmService;
    private readonly AlarmBannerWindow _bannerWindow;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Guid> _dismissedForCurrentProcess = [];

    private QuickNotebookAlarm? _activeAlarm;
    private DateTimeOffset? _nextAlarmDueAt;
    private bool _started;
    private bool _refreshInProgress;
    private bool _disposed;

    public QuickNotebookAlarmCoordinator(
        IQuickNotebookAlarmService alarmService,
        AlarmBannerWindow bannerWindow,
        Dispatcher dispatcher)
    {
        _alarmService = alarmService ?? throw new ArgumentNullException(nameof(alarmService));
        _bannerWindow = bannerWindow ?? throw new ArgumentNullException(nameof(bannerWindow));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += PollTimer_OnTick;
        _alarmService.AlarmsChanged += AlarmService_OnAlarmsChanged;
        _bannerWindow.Dismissed += BannerWindow_OnDismissed;
    }

    public QuickNotebookAlarm? ActiveAlarm => _activeAlarm;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _pollTimer.Start();
        QueueRefresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        _pollTimer.Stop();
        _pollTimer.Tick -= PollTimer_OnTick;
        _alarmService.AlarmsChanged -= AlarmService_OnAlarmsChanged;
        _bannerWindow.Dismissed -= BannerWindow_OnDismissed;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _activeAlarm = null;
        _dismissedForCurrentProcess.Clear();
    }

    private void PollTimer_OnTick(object? sender, EventArgs e)
    {
        if (_activeAlarm is null
            && (_nextAlarmDueAt is null || _nextAlarmDueAt <= DateTimeOffset.UtcNow))
        {
            QueueRefresh();
        }
    }

    private void AlarmService_OnAlarmsChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_started || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            HandleAlarmsChanged();
            return;
        }

        _ = _dispatcher.BeginInvoke(
            HandleAlarmsChanged,
            DispatcherPriority.Background);
    }

    private void HandleAlarmsChanged()
    {
        if (_disposed || !_started)
        {
            return;
        }

        _nextAlarmDueAt = null;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_disposed || !_started || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            _ = RefreshAsync();
            return;
        }

        _ = _dispatcher.BeginInvoke(
            () => _ = RefreshAsync(),
            DispatcherPriority.Background);
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !_started || _refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            IReadOnlyList<QuickNotebookAlarm> pending =
                await _alarmService.GetPendingAlarmsAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }

            if (_activeAlarm is { } active)
            {
                if (pending.Any(alarm => alarm.Id == active.Id))
                {
                    return;
                }

                _bannerWindow.HideReminder();
                _activeAlarm = null;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            QuickNotebookAlarm[] eligible = pending
                .Where(alarm => !_dismissedForCurrentProcess.Contains(alarm.Id))
                .OrderBy(alarm => alarm.DueAt)
                .ThenBy(alarm => alarm.CreatedAt)
                .ToArray();
            QuickNotebookAlarm? next = eligible.FirstOrDefault();
            _nextAlarmDueAt = next?.DueAt;
            if (next is null || next.DueAt > now)
            {
                return;
            }

            _activeAlarm = next;
            _nextAlarmDueAt = null;
            _bannerWindow.ShowReminder(next.Id, next.Message);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A temporarily inaccessible or malformed local alarm file must
            // not interrupt translation. The next timer tick retries it.
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void BannerWindow_OnDismissed()
    {
        if (_activeAlarm is not { } dismissed || _disposed)
        {
            return;
        }

        _activeAlarm = null;
        _nextAlarmDueAt = null;
        _dismissedForCurrentProcess.Add(dismissed.Id);
        try
        {
            // The alarm document is tiny. Complete this local write before the
            // close action returns so an immediate app exit cannot resurrect
            // an alarm that the user has already acknowledged.
            _ = _alarmService
                .DeleteAlarmAsync(dismissed.Id, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            // Keep the id suppressed for this process. If persistence failed,
            // the overdue reminder will safely return after a future restart.
        }

        QueueRefresh();
    }
}
