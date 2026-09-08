using Jumbo.AmazonEdi.Core.Models;
using Jumbo.AmazonEdi.Core.Omni;

namespace Jumbo.AmazonEdi.Core.Abstractions;

/// <summary>Reads invoices out of Omni. Read-only by design - this integration never writes to the ERP.</summary>
public interface IOmniInvoiceSource
{
    /// <summary>Invoices raised against the given Amazon customer accounts on or after <paramref name="since"/>.</summary>
    Task<IReadOnlyList<OmniInvoice>> GetInvoicesAsync(
        IReadOnlyCollection<string> customerAccountCodes,
        DateTimeOffset since,
        int maxResults,
        CancellationToken cancellationToken);
}

/// <summary>Submits invoices to Amazon.</summary>
public interface IVendorInvoicesClient
{
    Task<SubmitInvoicesResult> SubmitAsync(IReadOnlyList<Invoice> invoices, CancellationToken cancellationToken);
}

public sealed class SubmitInvoicesResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Amazon's transaction id. Present on success. Acceptance is not confirmation.</summary>
    public string? TransactionId { get; init; }

    public int StatusCode { get; init; }

    /// <summary>Verbatim request body, for the archive.</summary>
    public required string RequestJson { get; init; }

    /// <summary>Verbatim response body, for the archive. Empty when the call never completed.</summary>
    public required string ResponseJson { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>True when the failure is worth another attempt later (throttling, 5xx, transport).</summary>
    public bool IsRetryable { get; init; }
}

/// <summary>Persisted state for invoices we intend to send, or have sent.</summary>
public interface IAmazonInvoiceRepository
{
    /// <summary>Records the intent to send. Returns false when the invoice is already tracked -
    /// the unique constraint on the Omni invoice number is what makes a double-send impossible.</summary>
    Task<bool> TryRegisterAsync(OmniInvoice invoice, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrackedInvoice>> GetByStatusAsync(AmazonInvoiceStatus status, int maxResults, CancellationToken cancellationToken);

    Task SetStatusAsync(long id, AmazonInvoiceStatus status, string? lastError, CancellationToken cancellationToken);

    Task RecordSubmissionAsync(long id, SubmitInvoicesResult result, CancellationToken cancellationToken);

    /// <summary>The most recent invoice date we have seen, used as the poll watermark.</summary>
    Task<DateTimeOffset?> GetLatestInvoiceDateAsync(CancellationToken cancellationToken);
}

public sealed class TrackedInvoice
{
    public required long Id { get; init; }
    public required string OmniInvoiceNumber { get; init; }
    public required AmazonInvoiceStatus Status { get; init; }
    public required int AttemptCount { get; init; }
    public required OmniInvoice Source { get; init; }
}

public enum AmazonInvoiceStatus
{
    /// <summary>Picked up from Omni, not yet validated.</summary>
    Pending,

    /// <summary>Passed validation. Waiting for approval, or ready to send if approval is off.</summary>
    Validated,

    AwaitingApproval,

    /// <summary>Accepted by the API. Amazon may still reject it downstream.</summary>
    Submitted,

    Accepted,

    Rejected,

    /// <summary>Retries exhausted, or a validation failure a human must resolve.</summary>
    Failed,

    /// <summary>Deliberately excluded by a human.</summary>
    Skipped,
}

/// <summary>Writes a verbatim copy of every request and response to disk. The equivalent archive in
/// the Checkers integration is what made past "did Amazon ever see this?" questions answerable.</summary>
public interface IPayloadArchive
{
    Task ArchiveAsync(string invoiceNumber, string kind, string content, CancellationToken cancellationToken);
}
