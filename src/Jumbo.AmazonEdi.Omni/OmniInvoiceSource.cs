using System.Data;
using Dapper;
using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Omni;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Omni;

/// <summary>
/// Reads invoices out of Omni by executing one stored procedure that returns two result sets
/// (headers, then lines) in a single round trip. The OPENQUERY calls into the JOLLYJUMBO linked
/// server live inside that procedure, so changing how Omni is queried is a procedure change rather
/// than a redeploy.
///
/// Read-only: this integration never writes to the ERP.
/// </summary>
public sealed class OmniInvoiceSource : IOmniInvoiceSource
{
    private readonly OmniOptions _options;
    private readonly ILogger<OmniInvoiceSource> _logger;

    public OmniInvoiceSource(IOptions<OmniOptions> options, ILogger<OmniInvoiceSource> logger)
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
            // Not worth throwing over, but it means the integration is misconfigured and would
            // otherwise sit there finding nothing forever without anyone noticing.
            _logger.LogWarning("No Amazon customer account codes are configured, so no invoices will ever be found.");
            return Array.Empty<OmniInvoice>();
        }

        EnsureConfigured();

        await using var connection = new SqlConnection(_options.ConnectionString);

        var command = new CommandDefinition(
            _options.StoredProcedureName,
            new
            {
                Since = since,
                MaxResults = maxResults,
                AccountCodes = string.Join(',', customerAccountCodes),
            },
            commandType: CommandType.StoredProcedure,
            commandTimeout: _options.CommandTimeoutSeconds,
            cancellationToken: cancellationToken);

        await using var results = await connection.QueryMultipleAsync(command).ConfigureAwait(false);

        var headers = (await results.ReadAsync<InvoiceHeaderRow>().ConfigureAwait(false)).ToList();
        var lines = (await results.ReadAsync<InvoiceLineRow>().ConfigureAwait(false)).ToList();

        var invoices = OmniInvoiceMapper.Combine(headers, lines);

        _logger.LogInformation(
            "{Procedure} returned {InvoiceCount} invoice(s) and {LineCount} line(s) dated on or after {Since:u}.",
            _options.StoredProcedureName, invoices.Count, lines.Count, since);

        return invoices;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("AmazonEdi:Omni:ConnectionString is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.StoredProcedureName))
        {
            throw new InvalidOperationException("AmazonEdi:Omni:StoredProcedureName is not configured.");
        }
    }
}
