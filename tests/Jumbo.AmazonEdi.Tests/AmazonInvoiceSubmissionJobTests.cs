using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Configuration;
using Jumbo.AmazonEdi.Core.Invoicing;
using Jumbo.AmazonEdi.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

public class AmazonInvoiceSubmissionJobTests
{
    private readonly FakeOmniInvoiceSource _omni = new();
    private readonly FakeAmazonInvoiceRepository _repository = new();
    private readonly FakeVendorInvoicesClient _client = new();
    private readonly FakePayloadArchive _archive = new();

    private AmazonInvoiceSubmissionJob CreateJob(AmazonEdiOptions options) => new(
        _omni,
        _repository,
        _client,
        _archive,
        new InvoiceBuilder(options),
        new InvoiceValidator(options),
        Options.Create(options),
        NullLogger<AmazonInvoiceSubmissionJob>.Instance);

    private static AmazonEdiOptions OptionsWithoutApproval()
    {
        var options = TestData.Options();
        options.RequireApproval = false;
        return options;
    }

    [Fact]
    public async Task A_clean_invoice_is_ingested_validated_and_submitted_in_one_run()
    {
        _omni.Invoices.Add(TestData.Invoice());

        var summary = await CreateJob(OptionsWithoutApproval()).RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Ingested);
        Assert.Equal(1, summary.Submitted);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(AmazonInvoiceStatus.Submitted, _repository.Get("INV-100045").Status);
    }

    [Fact]
    public async Task Nothing_is_sent_while_approval_is_required()
    {
        _omni.Invoices.Add(TestData.Invoice());

        var summary = await CreateJob(TestData.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.HeldForReview);
        Assert.Equal(0, summary.Submitted);
        Assert.Empty(_client.Calls);
        Assert.Equal(AmazonInvoiceStatus.AwaitingApproval, _repository.Get("INV-100045").Status);
    }

    [Fact]
    public async Task An_invoice_is_never_submitted_twice_across_runs()
    {
        _omni.Invoices.Add(TestData.Invoice());
        var job = CreateJob(OptionsWithoutApproval());

        await job.RunAsync(CancellationToken.None);
        var second = await job.RunAsync(CancellationToken.None);

        Assert.Equal(0, second.Ingested);
        Assert.Single(_client.Calls);
    }

    [Fact]
    public async Task A_failing_invoice_does_not_stop_the_rest_of_the_batch()
    {
        _omni.Invoices.Add(TestData.Invoice(builder =>
        {
            builder.InvoiceNumber = "INV-BAD";
            builder.PurchaseOrderNumber = null;
        }));
        _omni.Invoices.Add(TestData.Invoice());

        var summary = await CreateJob(OptionsWithoutApproval()).RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Submitted);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(AmazonInvoiceStatus.Failed, _repository.Get("INV-BAD").Status);
        Assert.Contains("Amazon PO number", _repository.Get("INV-BAD").LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_the_request_and_the_response_are_archived()
    {
        _omni.Invoices.Add(TestData.Invoice());

        await CreateJob(OptionsWithoutApproval()).RunAsync(CancellationToken.None);

        Assert.Contains(_archive.Entries, entry => entry.Kind == "request");
        Assert.Contains(_archive.Entries, entry => entry.Kind == "response");
    }

    [Fact]
    public async Task A_rejected_submission_is_recorded_and_left_for_the_next_run()
    {
        _omni.Invoices.Add(TestData.Invoice());
        _client.Respond = invoices => new SubmitInvoicesResult
        {
            Succeeded = false,
            StatusCode = 400,
            RequestJson = "{}",
            ResponseJson = """{"errors":[{"code":"InvalidInput","message":"billToParty is incomplete"}]}""",
            ErrorMessage = "InvalidInput billToParty is incomplete",
            IsRetryable = false,
        };

        var summary = await CreateJob(OptionsWithoutApproval()).RunAsync(CancellationToken.None);

        Assert.Equal(0, summary.Submitted);
        Assert.Equal(1, summary.Failed);

        var entry = _repository.Get("INV-100045");
        Assert.Equal(AmazonInvoiceStatus.Validated, entry.Status);
        Assert.Equal(1, entry.AttemptCount);
        Assert.Contains("billToParty", entry.LastError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invoice_that_keeps_failing_is_handed_to_a_human_rather_than_retried_forever()
    {
        var options = OptionsWithoutApproval();
        options.MaxAttempts = 2;

        _omni.Invoices.Add(TestData.Invoice());
        _client.Respond = invoices => new SubmitInvoicesResult
        {
            Succeeded = false,
            StatusCode = 500,
            RequestJson = "{}",
            ResponseJson = string.Empty,
            ErrorMessage = "Amazon is unavailable",
            IsRetryable = true,
        };

        var job = CreateJob(options);
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None);

        var entry = _repository.Get("INV-100045");
        Assert.Equal(AmazonInvoiceStatus.Failed, entry.Status);
        Assert.Equal(2, entry.AttemptCount);
        Assert.Contains("Giving up after 2 attempts", entry.LastError!, StringComparison.Ordinal);
    }
}
