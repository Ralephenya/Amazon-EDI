using Jumbo.AmazonEdi.Core.Abstractions;

namespace Jumbo.AmazonEdi.Persistence.Entities;

/// <summary>One row per Omni invoice we intend to send to Amazon, or have sent.</summary>
public class InvoiceRecord
{
    public long Id { get; set; }

    /// <summary>Unique. This is the idempotency key that makes a double send impossible.</summary>
    public string OmniInvoiceNumber { get; set; } = string.Empty;

    public string? AmazonPurchaseOrderNumber { get; set; }

    public string CustomerAccountCode { get; set; } = string.Empty;

    public string? WarehouseCode { get; set; }

    public DateTimeOffset InvoiceDate { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public decimal TotalExcludingTax { get; set; }

    public decimal TotalTax { get; set; }

    public decimal TotalIncludingTax { get; set; }

    public AmazonInvoiceStatus Status { get; set; } = AmazonInvoiceStatus.Pending;

    /// <summary>Amazon's transaction id from the accepted submission.</summary>
    public string? TransactionId { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Why this invoice is stuck, in words a human can act on.</summary>
    public string? LastError { get; set; }

    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<InvoiceLineRecord> Lines { get; set; } = new();

    public List<InvoiceAttemptRecord> Attempts { get; set; } = new();
}
