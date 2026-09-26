using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

/// <summary>The approval queue's rules. The one that matters most: nothing in the UI may cause an
/// invoice Amazon already has to be sent a second time.</summary>
public sealed class EfInvoiceReviewServiceTests : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    private readonly SqlServerTestDatabase _database;
    private readonly IDbContextFactory<AmazonEdiDbContext> _contextFactory;
    private readonly EfAmazonInvoiceRepository _repository;
    private readonly EfInvoiceReviewService _review;

    public EfInvoiceReviewServiceTests(SqlServerTestDatabase database)
    {
        _database = database;
        _contextFactory = database.ContextFactory;
        _repository = SqlServerTestDatabase.IsAvailable ? new EfAmazonInvoiceRepository(_contextFactory) : null!;
        _review = SqlServerTestDatabase.IsAvailable
            ? new EfInvoiceReviewService(_contextFactory, NullLogger<EfInvoiceReviewService>.Instance)
            : null!;
    }

    public Task InitializeAsync() => _database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> GivenInvoice(AmazonInvoiceStatus status)
    {
        await _repository.TryRegisterAsync(TestData.Invoice(), CancellationToken.None);

        await using var context = _contextFactory.CreateDbContext();
        var record = await context.Invoices.SingleAsync();
        record.Status = status;
        await context.SaveChangesAsync();

        return record.Id;
    }

    [SqlServerFact]
    public async Task The_queue_shows_only_invoices_awaiting_approval()
    {
        await GivenInvoice(AmazonInvoiceStatus.AwaitingApproval);

        var queue = await _review.GetApprovalQueueAsync(50, CancellationToken.None);

        var item = Assert.Single(queue);
        Assert.Equal("INV-100045", item.OmniInvoiceNumber);
        Assert.Equal(2, item.LineCount);
        Assert.Equal(810.60m, item.TotalIncludingTax);
    }

    [SqlServerFact]
    public async Task Approving_releases_the_invoice_and_records_who_did_it()
    {
        var id = await GivenInvoice(AmazonInvoiceStatus.AwaitingApproval);

        var result = await _review.ApproveAsync(id, "erica", CancellationToken.None);

        Assert.True(result.Succeeded);

        await using var context = _contextFactory.CreateDbContext();
        var record = await context.Invoices.SingleAsync();
        Assert.Equal(AmazonInvoiceStatus.Validated, record.Status);
        Assert.Equal("erica", record.ApprovedBy);
        Assert.NotNull(record.ApprovedAtUtc);
    }

    [SqlServerTheory]
    [InlineData(AmazonInvoiceStatus.Submitted)]
    [InlineData(AmazonInvoiceStatus.Accepted)]
    public async Task An_invoice_amazon_already_has_can_never_be_approved_again(AmazonInvoiceStatus status)
    {
        var id = await GivenInvoice(status);

        var result = await _review.ApproveAsync(id, "erica", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("already", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerTheory]
    [InlineData(AmazonInvoiceStatus.Submitted)]
    [InlineData(AmazonInvoiceStatus.Accepted)]
    public async Task An_invoice_amazon_already_has_can_never_be_retried(AmazonInvoiceStatus status)
    {
        var id = await GivenInvoice(status);

        var result = await _review.RetryAsync(id, "erica", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [SqlServerFact]
    public async Task Approving_the_same_invoice_twice_is_refused_rather_than_throwing()
    {
        // Two people looking at the same queue is normal, not exceptional.
        var id = await GivenInvoice(AmazonInvoiceStatus.AwaitingApproval);

        Assert.True((await _review.ApproveAsync(id, "erica", CancellationToken.None)).Succeeded);

        var second = await _review.ApproveAsync(id, "steve", CancellationToken.None);
        Assert.False(second.Succeeded);
        Assert.NotNull(second.Reason);
    }

    [SqlServerFact]
    public async Task Skipping_records_who_and_why_where_the_next_person_will_see_it()
    {
        var id = await GivenInvoice(AmazonInvoiceStatus.AwaitingApproval);

        var result = await _review.SkipAsync(id, "erica", "Credit note coming, do not bill", CancellationToken.None);

        Assert.True(result.Succeeded);

        await using var context = _contextFactory.CreateDbContext();
        var record = await context.Invoices.SingleAsync();
        Assert.Equal(AmazonInvoiceStatus.Skipped, record.Status);
        Assert.Contains("erica", record.LastError!, StringComparison.Ordinal);
        Assert.Contains("Credit note coming", record.LastError!, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Skipping_without_a_reason_is_rejected_outright()
    {
        var id = await GivenInvoice(AmazonInvoiceStatus.AwaitingApproval);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _review.SkipAsync(id, "erica", "  ", CancellationToken.None));
    }

    [SqlServerFact]
    public async Task Retrying_a_failed_invoice_sends_it_back_for_revalidation_not_straight_to_amazon()
    {
        var id = await GivenInvoice(AmazonInvoiceStatus.Failed);

        var result = await _review.RetryAsync(id, "steve", CancellationToken.None);

        Assert.True(result.Succeeded);

        await using var context = _contextFactory.CreateDbContext();
        var record = await context.Invoices.SingleAsync();

        // Pending, not Validated: the data was presumably fixed, so it gets checked again.
        Assert.Equal(AmazonInvoiceStatus.Pending, record.Status);
        Assert.Equal(0, record.AttemptCount);
        Assert.Null(record.LastError);
    }

    [SqlServerFact]
    public async Task Acting_on_an_invoice_that_no_longer_exists_is_refused_politely()
    {
        var result = await _review.ApproveAsync(9999, "erica", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no longer exists", result.Reason!, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task The_detail_view_carries_the_lines_and_every_attempt()
    {
        var id = await GivenInvoice(AmazonInvoiceStatus.Validated);

        await _repository.RecordSubmissionAsync(id, new SubmitInvoicesResult
        {
            Succeeded = false,
            StatusCode = 400,
            RequestJson = """{"invoices":[]}""",
            ResponseJson = """{"errors":[{"code":"InvalidInput"}]}""",
            ErrorMessage = "InvalidInput",
        }, CancellationToken.None);

        var detail = await _review.GetDetailAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Lines.Count);
        var attempt = Assert.Single(detail.Attempts);
        Assert.False(attempt.Succeeded);
        Assert.Equal(400, attempt.HttpStatusCode);
        Assert.Contains("InvalidInput", attempt.ResponseJson!, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task An_unknown_invoice_has_no_detail()
    {
        Assert.Null(await _review.GetDetailAsync(9999, CancellationToken.None));
    }

}
