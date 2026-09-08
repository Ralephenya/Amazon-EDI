using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

/// <summary>
/// Runs against real SQL Server - see <see cref="SqlServerTestDatabase"/> for why neither the EF
/// InMemory provider nor SQLite will do. Skips when no test server is configured.
/// </summary>
public sealed class EfAmazonInvoiceRepositoryTests : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _database;
    private readonly IDbContextFactory<AmazonEdiDbContext> _contextFactory;
    private readonly EfAmazonInvoiceRepository _repository;

    public EfAmazonInvoiceRepositoryTests(SqlServerTestDatabase database)
    {
        _database = database;
        _contextFactory = database.ContextFactory;
        _repository = SqlServerTestDatabase.IsAvailable
            ? new EfAmazonInvoiceRepository(_contextFactory)
            : null!;
    }

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SqlServerFact]
    public async Task An_invoice_is_registered_with_its_lines()
    {
        var registered = await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None);

        Assert.True(registered);

        var tracked = await _repository.GetByStatusAsync(AmazonInvoiceStatus.Pending, 10, CancellationToken.None);
        var invoice = Assert.Single(tracked);
        Assert.Equal("INV-100045", invoice.OmniInvoiceNumber);
        Assert.Equal(2, invoice.Source.Lines.Count);
    }

    [SqlServerFact]
    public async Task The_same_invoice_number_cannot_be_registered_twice()
    {
        Assert.True(await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None));
        Assert.False(await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None));

        var tracked = await _repository.GetByStatusAsync(AmazonInvoiceStatus.Pending, 10, CancellationToken.None);
        Assert.Single(tracked);
    }

    [SqlServerFact]
    public async Task Decimals_round_trip_without_being_rounded()
    {
        await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None);

        var tracked = await _repository.GetByStatusAsync(AmazonInvoiceStatus.Pending, 10, CancellationToken.None);
        var line = tracked[0].Source.Lines[0];

        Assert.Equal(18.50m, line.UnitPriceExcludingTax);
        Assert.Equal(66.60m, line.LineTax);
        Assert.Equal(15m, line.TaxRate);
        Assert.Equal(810.60m, tracked[0].Source.TotalIncludingTax);
    }

    [SqlServerFact]
    public async Task Setting_a_status_records_the_reason_alongside_it()
    {
        await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None);
        var tracked = await _repository.GetByStatusAsync(AmazonInvoiceStatus.Pending, 10, CancellationToken.None);

        await _repository.SetStatusAsync(tracked[0].Id, AmazonInvoiceStatus.Failed, "No Amazon PO number.", CancellationToken.None);

        await using var context = _contextFactory.CreateDbContext();
        var record = await context.Invoices.SingleAsync();
        Assert.Equal(AmazonInvoiceStatus.Failed, record.Status);
        Assert.Equal("No Amazon PO number.", record.LastError);
    }

    [SqlServerFact]
    public async Task A_successful_submission_is_recorded_with_its_transaction_id_and_an_attempt_row()
    {
        await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None);
        var tracked = await _repository.GetByStatusAsync(AmazonInvoiceStatus.Pending, 10, CancellationToken.None);

        await _repository.RecordSubmissionAsync(tracked[0].Id, new SubmitInvoicesResult
        {
            Succeeded = true,
            TransactionId = "txn-1",
            StatusCode = 202,
            RequestJson = """{"invoices":[]}""",
            ResponseJson = """{"payload":{"transactionId":"txn-1"}}""",
        }, CancellationToken.None);

        await using var context = _contextFactory.CreateDbContext();
        var record = await context.Invoices.Include(invoice => invoice.Attempts).SingleAsync();

        Assert.Equal(AmazonInvoiceStatus.Submitted, record.Status);
        Assert.Equal("txn-1", record.TransactionId);
        Assert.Equal(1, record.AttemptCount);
        Assert.NotNull(record.SubmittedAtUtc);

        var attempt = Assert.Single(record.Attempts);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.True(attempt.Succeeded);
        Assert.Contains("transactionId", attempt.ResponseJson!, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task A_failed_submission_goes_back_to_validated_and_numbers_its_attempts()
    {
        await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None);
        var tracked = await _repository.GetByStatusAsync(AmazonInvoiceStatus.Pending, 10, CancellationToken.None);

        var failure = new SubmitInvoicesResult
        {
            Succeeded = false,
            StatusCode = 500,
            RequestJson = "{}",
            ResponseJson = string.Empty,
            ErrorMessage = "Amazon is unavailable",
            IsRetryable = true,
        };

        await _repository.RecordSubmissionAsync(tracked[0].Id, failure, CancellationToken.None);
        await _repository.RecordSubmissionAsync(tracked[0].Id, failure, CancellationToken.None);

        await using var context = _contextFactory.CreateDbContext();
        var record = await context.Invoices.Include(invoice => invoice.Attempts).SingleAsync();

        Assert.Equal(AmazonInvoiceStatus.Validated, record.Status);
        Assert.Equal(2, record.AttemptCount);
        Assert.Equal(new[] { 1, 2 }, record.Attempts.Select(attempt => attempt.AttemptNumber).Order());
        Assert.Null(record.SubmittedAtUtc);
    }

    [SqlServerFact]
    public async Task The_watermark_is_the_latest_invoice_date_we_have_seen()
    {
        Assert.Null(await _repository.GetLatestInvoiceDateAsync(CancellationToken.None));

        await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None);

        var watermark = await _repository.GetLatestInvoiceDateAsync(CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(2)), watermark);
    }

}
