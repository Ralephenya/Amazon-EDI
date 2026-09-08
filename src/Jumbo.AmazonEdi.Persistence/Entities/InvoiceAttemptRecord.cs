namespace Jumbo.AmazonEdi.Persistence.Entities;

/// <summary>One row per API call, successful or not, with the raw request and response. This is what
/// makes "did Amazon ever actually receive this?" answerable months later.</summary>
public class InvoiceAttemptRecord
{
    public long Id { get; set; }

    public long InvoiceId { get; set; }

    public InvoiceRecord? Invoice { get; set; }

    public int AttemptNumber { get; set; }

    public int HttpStatusCode { get; set; }

    public bool Succeeded { get; set; }

    public string? TransactionId { get; set; }

    public string RequestJson { get; set; } = string.Empty;

    public string? ResponseJson { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
