namespace Jumbo.AmazonEdi.Core.Abstractions;

/// <summary>
/// The human review step: what Jumbo Hub's approval queue is built on. Kept here rather than in the
/// UI so the rules - what may be approved, what may not - are testable and cannot be bypassed by a
/// page doing its own thing.
/// </summary>
public interface IInvoiceReviewService
{
    /// <summary>Invoices waiting for a person to approve them.</summary>
    Task<IReadOnlyList<InvoiceSummary>> GetApprovalQueueAsync(int maxResults, CancellationToken cancellationToken);

    /// <summary>Invoices a human needs to look at because they failed - validation or submission.</summary>
    Task<IReadOnlyList<InvoiceSummary>> GetFailedAsync(int maxResults, CancellationToken cancellationToken);

    /// <summary>Recent invoices in any state, newest first.</summary>
    Task<IReadOnlyList<InvoiceSummary>> GetRecentAsync(int maxResults, CancellationToken cancellationToken);

    /// <summary>One invoice with its lines and every submission attempt, for the detail page.</summary>
    Task<InvoiceDetail?> GetDetailAsync(long id, CancellationToken cancellationToken);

    /// <summary>Releases an invoice for submission on the next job run.</summary>
    Task<ReviewActionResult> ApproveAsync(long id, string approvedBy, CancellationToken cancellationToken);

    /// <summary>Excludes an invoice from submission until someone changes their mind.</summary>
    Task<ReviewActionResult> SkipAsync(long id, string skippedBy, string reason, CancellationToken cancellationToken);

    /// <summary>Puts a previously skipped or failed invoice back in the queue after the underlying
    /// data has been fixed. It is re-validated on the next run rather than trusted.</summary>
    Task<ReviewActionResult> RetryAsync(long id, string requestedBy, CancellationToken cancellationToken);
}

public sealed class InvoiceSummary
{
    public required long Id { get; init; }
    public required string OmniInvoiceNumber { get; init; }
    public string? AmazonPurchaseOrderNumber { get; init; }
    public required DateTimeOffset InvoiceDate { get; init; }
    public required string CurrencyCode { get; init; }
    public required decimal TotalIncludingTax { get; init; }
    public required AmazonInvoiceStatus Status { get; init; }
    public required int LineCount { get; init; }
    public required int AttemptCount { get; init; }
    public string? LastError { get; init; }
    public string? TransactionId { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
}

public sealed class InvoiceDetail
{
    public required InvoiceSummary Summary { get; init; }
    public required IReadOnlyList<InvoiceDetailLine> Lines { get; init; }
    public required IReadOnlyList<InvoiceAttemptSummary> Attempts { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
}

public sealed class InvoiceDetailLine
{
    public required int LineNumber { get; init; }
    public required string StockCode { get; init; }
    public string? Barcode { get; init; }
    public string? Asin { get; init; }
    public string? PurchaseOrderNumber { get; init; }
    public required int Quantity { get; init; }
    public required decimal UnitPriceExcludingTax { get; init; }
    public required decimal LineTotalExcludingTax { get; init; }
    public required decimal LineTax { get; init; }
    public required decimal TaxRate { get; init; }
}

public sealed class InvoiceAttemptSummary
{
    public required int AttemptNumber { get; init; }
    public required bool Succeeded { get; init; }
    public required int HttpStatusCode { get; init; }
    public string? TransactionId { get; init; }
    public string? ErrorMessage { get; init; }
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The raw request we sent. Present so the detail page can show exactly what went to
    /// Amazon without anyone going to the file archive.</summary>
    public string? RequestJson { get; init; }

    public string? ResponseJson { get; init; }
}

/// <summary>Result of an approve/skip/retry. A refusal is a normal outcome, not an exception: two
/// people looking at the same queue will sometimes act on the same invoice.</summary>
public sealed class ReviewActionResult
{
    private ReviewActionResult(bool succeeded, string? reason)
    {
        Succeeded = succeeded;
        Reason = reason;
    }

    public bool Succeeded { get; }

    /// <summary>Why the action was refused, in words that can be shown to the user as-is.</summary>
    public string? Reason { get; }

    public static ReviewActionResult Success() => new(true, null);

    public static ReviewActionResult Refused(string reason) => new(false, reason);
}
