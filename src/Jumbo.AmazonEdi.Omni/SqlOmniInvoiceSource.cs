using System.Data;
using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Omni;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Omni;

/// <summary>Reads invoices out of Omni. Read-only: this integration never writes to the ERP.
/// The queries live in configuration (see <see cref="OmniOptions"/>) because the exact Omni schema,
/// especially where the Amazon PO number is captured, is still being confirmed.</summary>
public sealed class SqlOmniInvoiceSource : IOmniInvoiceSource
{
    private readonly OmniOptions _options;
    private readonly ILogger<SqlOmniInvoiceSource> _logger;

    public SqlOmniInvoiceSource(IOptions<OmniOptions> options, ILogger<SqlOmniInvoiceSource> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OmniInvoice>> GetInvoicesAsync(
        IReadOnlyCollection<string> customerAccountCodes,
        DateTimeOffset since,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customerAccountCodes);

        if (customerAccountCodes.Count == 0)
        {
            // Not an error worth throwing over, but it means the integration is misconfigured and
            // will silently do nothing forever - which is exactly the failure mode we are replacing.
            _logger.LogWarning("No Amazon customer account codes are configured, so no invoices will ever be found.");
            return Array.Empty<OmniInvoice>();
        }

        EnsureConfigured();

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var headers = await ReadHeadersAsync(connection, customerAccountCodes, since, maxResults, cancellationToken)
            .ConfigureAwait(false);

        var invoices = new List<OmniInvoice>(headers.Count);
        foreach (var header in headers)
        {
            var lines = await ReadLinesAsync(connection, header.InvoiceNumber, cancellationToken).ConfigureAwait(false);
            invoices.Add(header.WithLines(lines));
        }

        return invoices;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("AmazonEdi:Omni:ConnectionString is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.HeaderQuery) || string.IsNullOrWhiteSpace(_options.LineQuery))
        {
            throw new InvalidOperationException(
                "AmazonEdi:Omni:HeaderQuery and LineQuery are not configured. These depend on the Omni schema - " +
                "see docs/discovery.md for the fields that still need confirming.");
        }
    }

    private async Task<List<InvoiceHeader>> ReadHeadersAsync(
        SqlConnection connection,
        IReadOnlyCollection<string> customerAccountCodes,
        DateTimeOffset since,
        int maxResults,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(_options.HeaderQuery, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

        command.Parameters.AddWithValue("@Since", since);
        command.Parameters.AddWithValue("@MaxResults", maxResults);

        // Account codes are parameterised individually; the query is expected to reference
        // @Account0, @Account1, ... The codes never reach the SQL text itself.
        var index = 0;
        foreach (var code in customerAccountCodes)
        {
            command.Parameters.AddWithValue($"@Account{index++}", code);
        }

        var headers = new List<InvoiceHeader>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            headers.Add(new InvoiceHeader
            {
                InvoiceNumber = reader.GetFieldValue<string>("InvoiceNumber"),
                InvoiceDate = reader.GetFieldValue<DateTimeOffset>("InvoiceDate"),
                PurchaseOrderNumber = reader.GetNullableString("PurchaseOrderNumber"),
                CustomerAccountCode = reader.GetFieldValue<string>("CustomerAccountCode"),
                WarehouseCode = reader.GetNullableString("WarehouseCode"),
                CurrencyCode = reader.GetFieldValue<string>("CurrencyCode"),
                TotalExcludingTax = reader.GetFieldValue<decimal>("TotalExcludingTax"),
                TotalTax = reader.GetFieldValue<decimal>("TotalTax"),
                TotalIncludingTax = reader.GetFieldValue<decimal>("TotalIncludingTax"),
            });
        }

        return headers;
    }

    private async Task<List<OmniInvoiceLine>> ReadLinesAsync(
        SqlConnection connection, string invoiceNumber, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(_options.LineQuery, connection)
        {
            CommandType = CommandType.Text,
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

        command.Parameters.AddWithValue("@InvoiceNumber", invoiceNumber);

        var lines = new List<OmniInvoiceLine>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(new OmniInvoiceLine
            {
                LineNumber = reader.GetFieldValue<int>("LineNumber"),
                StockCode = reader.GetFieldValue<string>("StockCode"),
                Barcode = reader.GetNullableString("Barcode"),
                Asin = reader.GetNullableString("Asin"),
                PurchaseOrderNumber = reader.GetNullableString("PurchaseOrderNumber"),
                Quantity = reader.GetFieldValue<int>("Quantity"),
                UnitPriceExcludingTax = reader.GetFieldValue<decimal>("UnitPriceExcludingTax"),
                LineTotalExcludingTax = reader.GetFieldValue<decimal>("LineTotalExcludingTax"),
                LineTax = reader.GetFieldValue<decimal>("LineTax"),
                TaxRate = reader.GetFieldValue<decimal>("TaxRate"),
            });
        }

        return lines;
    }

    private sealed class InvoiceHeader
    {
        public required string InvoiceNumber { get; init; }
        public required DateTimeOffset InvoiceDate { get; init; }
        public string? PurchaseOrderNumber { get; init; }
        public required string CustomerAccountCode { get; init; }
        public string? WarehouseCode { get; init; }
        public required string CurrencyCode { get; init; }
        public required decimal TotalExcludingTax { get; init; }
        public required decimal TotalTax { get; init; }
        public required decimal TotalIncludingTax { get; init; }

        public OmniInvoice WithLines(IReadOnlyList<OmniInvoiceLine> lines) => new()
        {
            InvoiceNumber = InvoiceNumber,
            InvoiceDate = InvoiceDate,
            PurchaseOrderNumber = PurchaseOrderNumber,
            CustomerAccountCode = CustomerAccountCode,
            WarehouseCode = WarehouseCode,
            CurrencyCode = CurrencyCode,
            TotalExcludingTax = TotalExcludingTax,
            TotalTax = TotalTax,
            TotalIncludingTax = TotalIncludingTax,
            Lines = lines,
        };
    }
}

internal static class SqlDataReaderExtensions
{
    public static T GetFieldValue<T>(this SqlDataReader reader, string columnName) =>
        reader.GetFieldValue<T>(reader.GetOrdinal(columnName));

    public static string? GetNullableString(this SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
