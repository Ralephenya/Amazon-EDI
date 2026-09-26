using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jumbo.AmazonEdi.Persistence;

/// <summary>
/// Backs the Jumbo Hub approval queue. Every state transition a human can trigger goes through here,
/// so the rules live in one tested place rather than in a Razor page.
///
/// The rule that matters: an invoice that has already been submitted can never be approved or
/// retried from the UI. Amazon has it; sending it again would be a duplicate document, and no button
/// should be able to cause that.
/// </summary>
public sealed class EfInvoiceReviewService : IInvoiceReviewService
{
    private readonly IDbContextFactory<AmazonEdiDbContext> _contextFactory;
    private readonly ILogger<EfInvoiceReviewService> _logger;

    public EfInvoiceReviewService(
        IDbContextFactory<AmazonEdiDbContext> contextFactory,
        ILogger<EfInvoiceReviewService> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public Task<IReadOnlyList<InvoiceSummary>> GetApprovalQueueAsync(int maxResults, CancellationToken cancellationToken) =>
        QueryAsync(
            query => query.Where(invoice => invoice.Status == AmazonInvoiceStatus.AwaitingApproval)
                .OrderBy(invoice => invoice.InvoiceDate),
            maxResults,
            cancellationToken);

    public Task<IReadOnlyList<InvoiceSummary>> GetFailedAsync(int maxResults, CancellationToken cancellationToken) =>
        QueryAsync(
            query => query.Where(invoice =>
                    invoice.Status == AmazonInvoiceStatus.Failed || invoice.Status == AmazonInvoiceStatus.Rejected)
                .OrderByDescending(invoice => invoice.UpdatedAtUtc),
            maxResults,
            cancellationToken);

    public Task<IReadOnlyList<InvoiceSummary>> GetRecentAsync(int maxResults, CancellationToken cancellationToken) =>
        QueryAsync(query => query.OrderByDescending(invoice => invoice.InvoiceDate).ThenByDescending(invoice => invoice.Id),
            maxResults,
            cancellationToken);

    public async Task<InvoiceDetail?> GetDetailAsync(long id, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var record = await context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Attempts)
            .SingleOrDefaultAsync(invoice => invoice.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        return new InvoiceDetail
        {
            Summary = ToSummary(record),
            ApprovedBy = record.ApprovedBy,
            ApprovedAtUtc = record.ApprovedAtUtc,
            Lines = record.Lines
                .OrderBy(line => line.LineNumber)
                .Select(line => new InvoiceDetailLine
                {
                    LineNumber = line.LineNumber,
                    StockCode = line.StockCode,
                    Barcode = line.Barcode,
                    Asin = line.Asin,
                    PurchaseOrderNumber = line.PurchaseOrderNumber,
                    Quantity = line.Quantity,
                    UnitPriceExcludingTax = line.UnitPriceExcludingTax,
                    LineTotalExcludingTax = line.LineTotalExcludingTax,
                    LineTax = line.LineTax,
                    TaxRate = line.TaxRate,
                })
                .ToList(),
            Attempts = record.Attempts
                .OrderBy(attempt => attempt.AttemptNumber)
                .Select(attempt => new InvoiceAttemptSummary
                {
                    AttemptNumber = attempt.AttemptNumber,
                    Succeeded = attempt.Succeeded,
                    HttpStatusCode = attempt.HttpStatusCode,
                    TransactionId = attempt.TransactionId,
                    ErrorMessage = attempt.ErrorMessage,
                    CreatedAtUtc = attempt.CreatedAtUtc,
                    RequestJson = attempt.RequestJson,
                    ResponseJson = attempt.ResponseJson,
                })
                .ToList(),
        };
    }

    public async Task<ReviewActionResult> ApproveAsync(long id, string approvedBy, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(approvedBy))
        {
            throw new ArgumentException("An approval must record who made it.", nameof(approvedBy));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var record = await context.Invoices.SingleOrDefaultAsync(invoice => invoice.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return ReviewActionResult.Refused("That invoice no longer exists.");
        }

        if (record.Status != AmazonInvoiceStatus.AwaitingApproval)
        {
            return ReviewActionResult.Refused(RefusalFor(record, "approved"));
        }

        record.Status = AmazonInvoiceStatus.Validated;
        record.ApprovedBy = approvedBy;
        record.ApprovedAtUtc = DateTime.UtcNow;
        record.LastError = null;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Invoice {InvoiceNumber} approved by {ApprovedBy}; it will be submitted on the next run.",
            record.OmniInvoiceNumber, approvedBy);

        return ReviewActionResult.Success();
    }

    public async Task<ReviewActionResult> SkipAsync(long id, string skippedBy, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(skippedBy))
        {
            throw new ArgumentException("Skipping an invoice must record who did it.", nameof(skippedBy));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // Without a reason, the next person to look at this has no idea why it was excluded.
            throw new ArgumentException("Skipping an invoice must record why.", nameof(reason));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var record = await context.Invoices.SingleOrDefaultAsync(invoice => invoice.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return ReviewActionResult.Refused("That invoice no longer exists.");
        }

        if (HasReachedAmazon(record.Status))
        {
            return ReviewActionResult.Refused(RefusalFor(record, "skipped"));
        }

        record.Status = AmazonInvoiceStatus.Skipped;
        record.LastError = $"Skipped by {skippedBy}: {reason}";

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Invoice {InvoiceNumber} skipped by {SkippedBy}: {Reason}",
            record.OmniInvoiceNumber, skippedBy, reason);

        return ReviewActionResult.Success();
    }

    public async Task<ReviewActionResult> RetryAsync(long id, string requestedBy, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            throw new ArgumentException("A retry must record who asked for it.", nameof(requestedBy));
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var record = await context.Invoices.SingleOrDefaultAsync(invoice => invoice.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return ReviewActionResult.Refused("That invoice no longer exists.");
        }

        if (HasReachedAmazon(record.Status))
        {
            return ReviewActionResult.Refused(RefusalFor(record, "retried"));
        }

        // Back to Pending, not Validated: the underlying data has presumably changed, so it is
        // re-validated on the next run rather than taken on trust.
        record.Status = AmazonInvoiceStatus.Pending;
        record.AttemptCount = 0;
        record.LastError = null;
        record.ApprovedBy = null;
        record.ApprovedAtUtc = null;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Invoice {InvoiceNumber} put back in the queue by {RequestedBy}; it will be re-validated on the next run.",
            record.OmniInvoiceNumber, requestedBy);

        return ReviewActionResult.Success();
    }

    /// <summary>Amazon already has this document. Nothing in the UI may cause it to be sent again.</summary>
    private static bool HasReachedAmazon(AmazonInvoiceStatus status) =>
        status is AmazonInvoiceStatus.Submitted or AmazonInvoiceStatus.Accepted;

    private static string RefusalFor(InvoiceRecord record, string verb) => record.Status switch
    {
        AmazonInvoiceStatus.Submitted =>
            $"Invoice {record.OmniInvoiceNumber} has already been submitted to Amazon"
            + (record.TransactionId is null ? "" : $" (transaction {record.TransactionId})")
            + $" and cannot be {verb}.",
        AmazonInvoiceStatus.Accepted =>
            $"Invoice {record.OmniInvoiceNumber} has already been accepted by Amazon and cannot be {verb}.",
        _ => $"Invoice {record.OmniInvoiceNumber} is {record.Status} and cannot be {verb} from here.",
    };

    private async Task<IReadOnlyList<InvoiceSummary>> QueryAsync(
        Func<IQueryable<InvoiceRecord>, IOrderedQueryable<InvoiceRecord>> filter,
        int maxResults,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var records = await filter(context.Invoices.AsNoTracking().Include(invoice => invoice.Lines))
            .Take(maxResults)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToSummary).ToList();
    }

    private static InvoiceSummary ToSummary(InvoiceRecord record) => new()
    {
        Id = record.Id,
        OmniInvoiceNumber = record.OmniInvoiceNumber,
        AmazonPurchaseOrderNumber = record.AmazonPurchaseOrderNumber,
        InvoiceDate = record.InvoiceDate,
        CurrencyCode = record.CurrencyCode,
        TotalIncludingTax = record.TotalIncludingTax,
        Status = record.Status,
        LineCount = record.Lines.Count,
        AttemptCount = record.AttemptCount,
        LastError = record.LastError,
        TransactionId = record.TransactionId,
        SubmittedAtUtc = record.SubmittedAtUtc,
    };
}
