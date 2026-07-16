using Huaci.App.Models;

namespace Huaci.App.Services.Notebook;

public interface IQuickNotebookAlarmService
{
    event EventHandler? AlarmsChanged;

    string AlarmStoragePath { get; }

    Task<IReadOnlyList<QuickNotebookAlarm>> GetPendingAlarmsAsync(
        CancellationToken cancellationToken = default);

    Task<QuickNotebookAlarm> ScheduleAlarmAsync(
        string message,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAlarmAsync(
        Guid alarmId,
        CancellationToken cancellationToken = default);
}
