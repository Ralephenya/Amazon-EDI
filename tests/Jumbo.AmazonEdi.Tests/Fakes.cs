using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Models;
using Jumbo.AmazonEdi.Core.Omni;

namespace Jumbo.AmazonEdi.Tests;

internal sealed class FakeOmniInvoiceSource : IOmniInvoiceSource
{
    public List<OmniInvoice> Invoices { get; } = new();

    public Task<IReadOnlyList<OmniInvoice>> GetInvoicesAsync(
        IReadOnlyCollection<string> customerAccountCodes,
        DateTimeOffset since,
        int maxResults,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OmniInvoice>>(Invoices);
}

/// <summary>In-memory stand-in for the SQL repository. The dictionary keyed on invoice number
/// mirrors the unique constraint that does this job for real.</summary>
internal sealed class FakeAmazonInvoiceRepository : IAmazonInvoiceRepository
{
    private readonly Dictionary<string, Entry> _byInvoiceNumber = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;

    public List<SubmitInvoicesResult> Submissions { get; } = new();

    public IReadOnlyDictionary<string, Entry> Entries => _byInvoiceNumber;

    public Task<bool> TryRegisterAsync(OmniInvoice invoice, CancellationToken cancellationToken)
    {
        if (_byInvoiceNumber.ContainsKey(invoice.InvoiceNumber))
        {
            return Task.FromResult(false);
        }

        _byInvoiceNumber[invoice.InvoiceNumber] = new Entry
        {
            Id = _nextId++,
            Source = invoice,
            Status = AmazonInvoiceStatus.Pending,
        };

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TrackedInvoice>> GetByStatusAsync(
        AmazonInvoiceStatus status, int maxResults, CancellationToken cancellationToken)
    {
        var matches = _byInvoiceNumber.Values
            .Where(entry => entry.Status == status)
            .Take(maxResults)
            .Select(entry => new TrackedInvoice
            {
                Id = entry.Id,
                OmniInvoiceNumber = entry.Source.InvoiceNumber,
                Status = entry.Status,
                AttemptCount = entry.AttemptCount,
                Source = entry.Source,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<TrackedInvoice>>(matches);
    }

    public Task SetStatusAsync(long id, AmazonInvoiceStatus status, string? lastError, CancellationToken cancellationToken)
    {
        var entry = Find(id);
        entry.Status = status;
        entry.LastError = lastError;
        return Task.CompletedTask;
    }

    public Task RecordSubmissionAsync(long id, SubmitInvoicesResult result, CancellationToken cancellationToken)
    {
        var entry = Find(id);
        entry.AttemptCount++;
        entry.Status = result.Succeeded ? AmazonInvoiceStatus.Submitted : AmazonInvoiceStatus.Validated;
        entry.LastError = result.ErrorMessage;
        entry.TransactionId = result.TransactionId;
        Submissions.Add(result);
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLatestInvoiceDateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_byInvoiceNumber.Count == 0
            ? (DateTimeOffset?)null
            : _byInvoiceNumber.Values.Max(entry => entry.Source.InvoiceDate));

    public Entry Get(string invoiceNumber) => _byInvoiceNumber[invoiceNumber];

    private Entry Find(long id) => _byInvoiceNumber.Values.Single(entry => entry.Id == id);

    internal sealed class Entry
    {
        public required long Id { get; init; }
        public required OmniInvoice Source { get; init; }
        public AmazonInvoiceStatus Status { get; set; }
        public int AttemptCount { get; set; }
        public string? LastError { get; set; }
        public string? TransactionId { get; set; }
    }
}

internal sealed class FakeVendorInvoicesClient : IVendorInvoicesClient
{
    public List<IReadOnlyList<Invoice>> Calls { get; } = new();

    public Func<IReadOnlyList<Invoice>, SubmitInvoicesResult> Respond { get; set; } = invoices =>
        new SubmitInvoicesResult
        {
            Succeeded = true,
            TransactionId = "txn-" + invoices[0].Id,
            StatusCode = 202,
            RequestJson = AmazonJson.Serialize(new SubmitInvoicesRequest { Invoices = invoices.ToList() }),
            ResponseJson = """{"payload":{"transactionId":"txn"}}""",
        };

    public Task<SubmitInvoicesResult> SubmitAsync(IReadOnlyList<Invoice> invoices, CancellationToken cancellationToken)
    {
        Calls.Add(invoices);
        return Task.FromResult(Respond(invoices));
    }
}

internal sealed class FakePayloadArchive : IPayloadArchive
{
    public List<(string InvoiceNumber, string Kind, string Content)> Entries { get; } = new();

    public Task ArchiveAsync(string invoiceNumber, string kind, string content, CancellationToken cancellationToken)
    {
        Entries.Add((invoiceNumber, kind, content));
        return Task.CompletedTask;
    }
}
