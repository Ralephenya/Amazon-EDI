using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Configuration;
using Jumbo.AmazonEdi.Core.Invoicing;
using Jumbo.AmazonEdi.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Jobs;

/// <summary>
/// The whole pipeline: pull from Omni, validate, then submit whatever is approved.
///
/// Hosted by Hangfire in Jumbo Hub:
///   RecurringJob.AddOrUpdate&lt;AmazonInvoiceSubmissionJob&gt;(
///       "amazon-invoices", job => job.RunAsync(CancellationToken.None), "*/15 * * * *");
///
/// Every invoice is handled independently - one bad invoice never stops the batch.
/// </summary>
public sealed class AmazonInvoiceSubmissionJob
{
    private readonly IOmniInvoiceSource _omni;
    private readonly IAmazonInvoiceRepository _repository;
    private readonly IVendorInvoicesClient _client;
    private readonly IPayloadArchive _archive;
    private readonly InvoiceBuilder _builder;
    private readonly InvoiceValidator _validator;
    private readonly AmazonEdiOptions _options;
    private readonly ILogger<AmazonInvoiceSubmissionJob> _logger;
    private readonly TimeProvider _timeProvider;

    public AmazonInvoiceSubmissionJob(
        IOmniInvoiceSource omni,
        IAmazonInvoiceRepository repository,
        IVendorInvoicesClient client,
        IPayloadArchive archive,
        InvoiceBuilder builder,
        InvoiceValidator validator,
        IOptions<AmazonEdiOptions> options,
        ILogger<AmazonInvoiceSubmissionJob> logger,
        TimeProvider? timeProvider = null)
    {
        _omni = omni;
        _repository = repository;
        _client = client;
        _archive = archive;
        _builder = builder;
        _validator = validator;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<JobRunSummary> RunAsync(CancellationToken cancellationToken)
    {
        var summary = new JobRunSummary();

        await IngestAsync(summary, cancellationToken).ConfigureAwait(false);
        await ValidatePendingAsync(summary, cancellationToken).ConfigureAwait(false);
        await SubmitApprovedAsync(summary, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Amazon invoice run finished. Ingested {Ingested}, validated {Validated}, held {Held}, submitted {Submitted}, failed {Failed}.",
            summary.Ingested, summary.Validated, summary.HeldForReview, summary.Submitted, summary.Failed);

        return summary;
    }

    /// <summary>Pick up invoices Omni has raised against Amazon that we have not seen before.</summary>
    private async Task IngestAsync(JobRunSummary summary, CancellationToken cancellationToken)
    {
        var watermark = await _repository.GetLatestInvoiceDateAsync(cancellationToken).ConfigureAwait(false);
        var since = watermark ?? _timeProvider.GetUtcNow().AddDays(-_options.Poll.LookbackDays);

        var invoices = await _omni.GetInvoicesAsync(
            _options.AmazonCustomerAccountCodes,
            since,
            _options.Poll.BatchSize,
            cancellationToken).ConfigureAwait(false);

        // "Nothing to send" and "the call failed" must never look the same in the log - conflating
        // those two is what made the old Checkers puller so hard to diagnose.
        if (invoices.Count == 0)
        {
            _logger.LogInformation("Omni returned no new Amazon invoices since {Since:u}.", since);
            return;
        }

        foreach (var invoice in invoices)
        {
            // Registering before we do anything else is what makes a double send impossible: the
            // unique constraint on the invoice number rejects the second attempt.
            var isNew = await _repository.TryRegisterAsync(invoice, cancellationToken).ConfigureAwait(false);
            if (isNew)
            {
                summary.Ingested++;
            }
        }

        _logger.LogInformation(
            "Omni returned {Total} invoice(s) since {Since:u}; {New} were new.",
            invoices.Count, since, summary.Ingested);
    }

    private async Task ValidatePendingAsync(JobRunSummary summary, CancellationToken cancellationToken)
    {
        var pending = await _repository
            .GetByStatusAsync(AmazonInvoiceStatus.Pending, _options.Poll.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        foreach (var tracked in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var payload = _builder.Build(tracked.Source);
                var result = _validator.Validate(tracked.Source, payload);

                if (!result.IsValid)
                {
                    // Failed, not Rejected: nothing has been sent, a human needs to fix the data.
                    await _repository
                        .SetStatusAsync(tracked.Id, AmazonInvoiceStatus.Failed, result.Message, cancellationToken)
                        .ConfigureAwait(false);

                    summary.Failed++;
                    _logger.LogWarning(
                        "Invoice {InvoiceNumber} failed validation and will not be sent: {Reasons}",
                        tracked.OmniInvoiceNumber, result.Message);
                    continue;
                }

                var nextStatus = _options.RequireApproval
                    ? AmazonInvoiceStatus.AwaitingApproval
                    : AmazonInvoiceStatus.Validated;

                await _repository.SetStatusAsync(tracked.Id, nextStatus, null, cancellationToken).ConfigureAwait(false);

                summary.Validated++;
                if (_options.RequireApproval)
                {
                    summary.HeldForReview++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One malformed invoice must not take the batch down with it.
                await _repository
                    .SetStatusAsync(tracked.Id, AmazonInvoiceStatus.Failed, $"Unexpected error while validating: {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);

                summary.Failed++;
                _logger.LogError(ex, "Unexpected error validating invoice {InvoiceNumber}.", tracked.OmniInvoiceNumber);
            }
        }
    }

    /// <summary>Submits invoices in the Validated state. When RequireApproval is on, a human moves an
    /// invoice from AwaitingApproval to Validated in Jumbo Hub - nothing here does that on its own.</summary>
    private async Task SubmitApprovedAsync(JobRunSummary summary, CancellationToken cancellationToken)
    {
        var ready = await _repository
            .GetByStatusAsync(AmazonInvoiceStatus.Validated, _options.Poll.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        foreach (var tracked in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (tracked.AttemptCount >= _options.MaxAttempts)
            {
                await _repository.SetStatusAsync(
                    tracked.Id,
                    AmazonInvoiceStatus.Failed,
                    $"Giving up after {tracked.AttemptCount} attempts. Check Vendor Central before resending.",
                    cancellationToken).ConfigureAwait(false);

                summary.Failed++;
                _logger.LogError(
                    "Invoice {InvoiceNumber} has failed {Attempts} times and now needs a human.",
                    tracked.OmniInvoiceNumber, tracked.AttemptCount);
                continue;
            }

            var payload = _builder.Build(tracked.Source);

            // One invoice per call for now. The API accepts a batch, but a single-invoice call means
            // a failure is unambiguous about which document it applies to.
            var result = await _client.SubmitAsync(new[] { payload }, cancellationToken).ConfigureAwait(false);

            await _archive.ArchiveAsync(tracked.OmniInvoiceNumber, "request", result.RequestJson, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.ResponseJson))
            {
                await _archive.ArchiveAsync(tracked.OmniInvoiceNumber, "response", result.ResponseJson, cancellationToken)
                    .ConfigureAwait(false);
            }

            await _repository.RecordSubmissionAsync(tracked.Id, result, cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                summary.Submitted++;

                if (_options.Mode == OperatingMode.ParallelTest)
                {
                    _logger.LogInformation(
                        "Invoice {InvoiceNumber} submitted (transaction {TransactionId}). Parallel testing is on - " +
                        "this invoice must still be captured in Vendor Central by hand.",
                        tracked.OmniInvoiceNumber, result.TransactionId);
                }
                else
                {
                    _logger.LogInformation(
                        "Invoice {InvoiceNumber} submitted (transaction {TransactionId}).",
                        tracked.OmniInvoiceNumber, result.TransactionId);
                }
            }
            else
            {
                summary.Failed++;
                _logger.LogError(
                    "Invoice {InvoiceNumber} was not accepted (HTTP {Status}): {Error}",
                    tracked.OmniInvoiceNumber, result.StatusCode, result.ErrorMessage);
            }
        }
    }
}

public sealed class JobRunSummary
{
    public int Ingested { get; set; }
    public int Validated { get; set; }
    public int HeldForReview { get; set; }
    public int Submitted { get; set; }
    public int Failed { get; set; }
}
