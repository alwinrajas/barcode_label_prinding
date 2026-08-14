namespace BarcodePrinter.Contracts;

/// <summary>Print job lifecycle (blueprint §8.1). Reprint is NOT a status —
/// it is a flag + source_job_id, so "a completed reprint" stays representable.</summary>
public enum PrintJobStatus
{
    Queued = 0,
    Dispatching = 1,
    Printing = 2,
    Completed = 3,
    PartiallyCompleted = 4,
    Failed = 5,
    Cancelled = 6,
}

public enum PrintJobItemStatus
{
    Pending = 0,
    Dispatched = 1,
    Confirmed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>How bytes physically reach the printer (blueprint §7.2).</summary>
public enum PrinterConnectionType
{
    /// <summary>Raw TCP to port 9100 — network Zebra with no Windows queue.</summary>
    NetworkTcp = 0,
    /// <summary>RAW passthrough via the Windows spooler — USB/shared Zebra.</summary>
    WindowsRaw = 1,
    /// <summary>GDI/XPS via System.Printing — normal Windows printers.</summary>
    WindowsGraphics = 2,
}

/// <summary>Which tier dispatches the job (blueprint §7.3, hybrid model).</summary>
public enum PrinterDispatchMode
{
    Server = 0,
    Client = 1,
}

public enum PrinterLanguage
{
    Zpl = 0,
    Windows = 1,
}

/// <summary>Format the client-supplied template artifact is in (C-2).</summary>
public enum TemplateFormat
{
    Zpl = 0,
    Epl = 1,
    WindowsDocument = 2,
    /// <summary>Reconstructed native template — the fallback path when the client
    /// supplies only PDF/image references (§4.2).</summary>
    Native = 3,
}

public enum ImportBatchStatus
{
    Uploaded = 0,
    Validating = 1,
    Committing = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>Excel import commit policy (C-13 — both implemented, default is a setting).</summary>
public enum ImportCommitPolicy
{
    AllOrNothing = 0,
    PartialCommit = 1,
}

/// <summary>What "Completed" means for a print job (C-17 / §8.5).</summary>
public enum CompletionSemantics
{
    /// <summary>Bytes accepted by the printer socket or Windows spooler. Universally available.</summary>
    Dispatched = 0,
    /// <summary>Printer confirmed batch complete with no error (~HQES). Per-printer capability.</summary>
    Confirmed = 1,
}

public enum OutboxStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    DeadLettered = 3,
}

public enum AuditSeverity
{
    Info = 0,
    Warning = 1,
    Security = 2,
}
