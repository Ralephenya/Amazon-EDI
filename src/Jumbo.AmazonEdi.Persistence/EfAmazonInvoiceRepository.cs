using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Omni;
using Jumbo.AmazonEdi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jumbo.AmazonEdi.Persistence;

/// <summary>
/// State store for invoices we intend to send. Uses a context factory rather than a scoped context:
/// this is a singleton and Hangfire runs jobs on background threads, so each operation gets its own
/// short-lived context.
/// </summary>
public sealed class EfAmazonInvoiceRepository : IAmazonInvoiceRepository
{
    private readonly IDbContextFactory<AmazonEdiDbContext> _contextFactory;

    public EfAmazonInvoiceRepository(IDbContextFactory<AmazonEdiDbContext> contextFactory) =>
        _contextFactory = contextFactory;

    public async Task<bool> TryRegisterAsync(OmniInvoice invoice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var record = new InvoiceRecord
        {
            OmniInvoiceNumber = invoice.InvoiceNumber,
            AmazonPurchaseOrderNumber = invoice.PurchaseOrderNumber,
            CustomerAccountCode = invoice.CustomerAccountCode,
            WarehouseCode = invoice.WarehouseCode,
            InvoiceDate = invoice.InvoiceDate,
            CurrencyCode = invoice.CurrencyCode,
            TotalExcludingTax = invoice.TotalExcludingTax,
            TotalTax = invoice.TotalTax,
            TotalIncludingTax = invoice.TotalIncludingTax,
            Status = AmazonInvoiceStatus.Pending,
            Lines = invoice.Lines.Select(line => new InvoiceLineRecord
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
            }).ToList(),
        };

        context.Invoices.Add(record);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Already tracked. This is the unique index doing its job, not an error.
            return false;
        }
    }

    public async Task<IReadOnlyList<TrackedInvoice>> GetByStatusAsync(
        AmazonInvoiceStatus status, int maxResults, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var records = await context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Where(invoice => invoice.Status == status)
            .OrderBy(invoice => invoice.InvoiceDate)
            .ThenBy(invoice => invoice.Id)
            .Take(maxResults)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToTrackedInvoice).ToList();
    }

    public async Task SetStatusAsync(long id, AmazonInvoiceStatus status, string? lastError, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var record = await context.Invoices.SingleAsync(invoice => invoice.Id == id, cancellationToken)
            .ConfigureAwait(false);

        record.Status = status;
        record.LastError = lastError;

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSubmissionAsync(long id, SubmitInvoicesResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var record = await context.Invoices
            .Include(invoice => invoice.Attempts)
            .SingleAsync(invoice => invoice.Id == id, cancellationToken)
            .ConfigureAwait(false);

        var attemptNumber = record.Attempts.Count == 0 ? 1 : record.Attempts.Max(attempt => attempt.AttemptNumber) + 1;

        record.Attempts.Add(new InvoiceAttemptRecord
        {
            AttemptNumber = attemptNumber,
            HttpStatusCode = result.StatusCode,
            Succeeded = result.Succeeded,
            TransactionId = result.TransactionId,
            RequestJson = result.RequestJson,
            ResponseJson = string.IsNullOrEmpty(result.ResponseJson) ? null : result.ResponseJson,
            ErrorMessage = result.ErrorMessage,
        });

        record.AttemptCount++;
        record.LastError = result.ErrorMessage;

        if (result.Succeeded)
        {
            record.Status = AmazonInvoiceStatus.Submitted;
            record.TransactionId = result.TransactionId ?? record.TransactionId;
            record.SubmittedAtUtc = DateTime.UtcNow;
        }
        else
        {
            // Back to Validated so the next run can try again, up to MaxAttempts.
            record.Status = AmazonInvoiceStatus.Validated;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetLatestInvoiceDateAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (!await context.Invoices.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await context.Invoices
            .MaxAsync(invoice => invoice.InvoiceDate, cancellationToken)
            .ConfigureAwait(false);
    }

    private static TrackedInvoice ToTrackedInvoice(InvoiceRecord record) => new()
    {
        Id = record.Id,
        OmniInvoiceNumber = record.OmniInvoiceNumber,
        Status = record.Status,
        AttemptCount = record.AttemptCount,
        Source = new OmniInvoice
        {
            InvoiceNumber = record.OmniInvoiceNumber,
            InvoiceDate = record.InvoiceDate,
            PurchaseOrderNumber = record.AmazonPurchaseOrderNumber,
            CustomerAccountCode = record.CustomerAccountCode,
            WarehouseCode = record.WarehouseCode,
            CurrencyCode = record.CurrencyCode,
            TotalExcludingTax = record.TotalExcludingTax,
            TotalTax = record.TotalTax,
            TotalIncludingTax = record.TotalIncludingTax,
            Lines = record.Lines
                .OrderBy(line => line.LineNumber)
                .Select(line => new OmniInvoiceLine
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
        },
    };

    /// <summary>SQL Server reports a unique violation as 2601 or 2627; SQLite (used by the tests)
    /// reports 19. Matching on the provider exception would tie this to one provider, so we match on
    /// the numbers both use.</summary>
    private static bool IsDuplicateKey(DbUpdateException exception)
    {
        var sqlErrorNumbers = new[] { 2601, 2627, 19 };

        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            var numberProperty = inner.GetType().GetProperty("Number") ?? inner.GetType().GetProperty("SqliteErrorCode");
            if (numberProperty?.GetValue(inner) is int number && sqlErrorNumbers.Contains(number))
            {
                return true;
            }
        }

        return false;
    }
}
