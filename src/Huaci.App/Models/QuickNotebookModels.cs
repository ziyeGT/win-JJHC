namespace Huaci.App.Models;

public enum QuickNotebookContentType
{
    Text,
    Image
}

public sealed record QuickNotebookEntry(
    string FilePath,
    string FileName,
    QuickNotebookContentType ContentType,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    string Preview)
{
    public bool IsText => ContentType == QuickNotebookContentType.Text;

    public bool IsImage => ContentType == QuickNotebookContentType.Image;
}

public sealed record QuickNotebookAlarm(
    Guid Id,
    string Message,
    DateTimeOffset DueAt,
    DateTimeOffset CreatedAt);
