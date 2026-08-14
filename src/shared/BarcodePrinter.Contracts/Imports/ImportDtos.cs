namespace BarcodePrinter.Contracts.Imports;

/// <summary>Live status of an import batch — pushed over SignalR and served
/// by GET for the polling fallback (B-16).</summary>
public sealed record ImportBatchDto(
    long Id,
    string FileName,
    string Status,            // ImportBatchStatus name
    string CommitPolicy,      // ImportCommitPolicy name
    int TotalRows,
    int ProcessedRows,
    int ValidRows,
    int InvalidRows,
    int InsertedRows,
    int UpdatedRows,
    DateTime UploadedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    string? ErrorMessage,
    bool HasErrorReport);

public sealed record ImportAcceptedResponse(long BatchId);
