using System.Windows.Media.Imaging;
using Huaci.App.Models;

namespace Huaci.App.Services.Notebook;

public interface IQuickNotebookService : IQuickNotebookAlarmService
{
    string StorageDirectory { get; }

    Task<IReadOnlyList<QuickNotebookEntry>> GetHistoryAsync(
        CancellationToken cancellationToken = default);

    Task<QuickNotebookEntry> SaveTextAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<QuickNotebookEntry> SaveImageAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default);

    Task<string> ReadTextAsync(
        QuickNotebookEntry entry,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadImageAsync(
        QuickNotebookEntry entry,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        QuickNotebookEntry entry,
        CancellationToken cancellationToken = default);

    void OpenStorageFolder();
}
