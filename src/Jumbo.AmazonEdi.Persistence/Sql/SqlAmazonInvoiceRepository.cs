using System.Data;
using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Omni;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Persistence.Sql;

/// <summary>State store for invoices we intend to send. Registration and submission recording both
/// run in a transaction; the unique constraint on OmniInvoiceNumber - not application logic - is
/// what prevents a double send.</summary>
public sealed class SqlAmazonInvoiceRepository : IAmazonInvoiceRepository
{
    /// <summary>SQL Server error numbers for a unique index or unique constraint violation.</summary>
    private static readonly int[] DuplicateKeyErrorNumbers = { 2601, 2627 };

    private readonly PersistenceOptions _options;

    public SqlAmazonInvoiceRepository(IOptions<PersistenceOptions> options) => _options = options.Value;

    public async Task<bool> TryRegisterAsync(OmniInvoice invoice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long invoiceId;
        try
        {
            await using var command = NewCommand(connection, transaction, """
                INSERT INTO [AmazonEdi].[Invoice]
                    ([OmniInvoiceNumber],[AmazonPurchaseOrderNumber],[CustomerAccountCode],[WarehouseCode],
                     [InvoiceDate],[CurrencyCode],[TotalExcludingTax],[TotalTax],[TotalIncludingTax],[Status])
                OUTPUT INSERTED.[Id]
                VALUES
                    (@OmniInvoiceNumber,@PurchaseOrderNumber,@CustomerAccountCode,@WarehouseCode,
                     @InvoiceDate,@CurrencyCode,@TotalExcludingTax,@TotalTax,@TotalIncludingTax,'Pending');
                """);

            command.Parameters.AddWithValue("@OmniInvoiceNumber", invoice.InvoiceNumber);
            command.Parameters.AddWithValue("@PurchaseOrderNumber", (object?)invoice.PurchaseOrderNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@CustomerAccountCode", invoice.CustomerAccountCode);
            command.Parameters.AddWithValue("@WarehouseCode", (object?)invoice.WarehouseCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@InvoiceDate", invoice.InvoiceDate);
            command.Parameters.AddWithValue("@CurrencyCode", invoice.CurrencyCode);
            command.Parameters.AddWithValue("@TotalExcludingTax", invoice.TotalExcludingTax);
            command.Parameters.AddWithValue("@TotalTax", invoice.TotalTax);
            command.Parameters.AddWithValue("@TotalIncludingTax", invoice.TotalIncludingTax);

            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            invoiceId = Convert.ToInt64(scalar);
        }
        catch (SqlException ex) when (ex.Errors.Cast<SqlError>().Any(error => DuplicateKeyErrorNumbers.Contains(error.Number)))
        {
            // Already tracked. This is the guard working, not an error.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        foreach (var line in invoice.Lines)
        {
            await using var command = NewCommand(connection, transaction, """
                INSERT INTO [AmazonEdi].[InvoiceLine]
                    ([InvoiceId],[LineNumber],[StockCode],[Barcode],[Asin],[PurchaseOrderNumber],
                     [Quantity],[UnitPriceExcludingTax],[LineTotalExcludingTax],[LineTax],[TaxRate])
                VALUES
                    (@InvoiceId,@LineNumber,@StockCode,@Barcode,@Asin,@PurchaseOrderNumber,
                     @Quantity,@UnitPriceExcludingTax,@LineTotalExcludingTax,@LineTax,@TaxRate);
                """);

            command.Parameters.AddWithValue("@InvoiceId", invoiceId);
            command.Parameters.AddWithValue("@LineNumber", line.LineNumber);
            command.Parameters.AddWithValue("@StockCode", line.StockCode);
            command.Parameters.AddWithValue("@Barcode", (object?)line.Barcode ?? DBNull.Value);
            command.Parameters.AddWithValue("@Asin", (object?)line.Asin ?? DBNull.Value);
            command.Parameters.AddWithValue("@PurchaseOrderNumber", (object?)line.PurchaseOrderNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("@Quantity", line.Quantity);
            command.Parameters.AddWithValue("@UnitPriceExcludingTax", line.UnitPriceExcludingTax);
            command.Parameters.AddWithValue("@LineTotalExcludingTax", line.LineTotalExcludingTax);
            command.Parameters.AddWithValue("@LineTax", line.LineTax);
            command.Parameters.AddWithValue("@TaxRate", line.TaxRate);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<TrackedInvoice>> GetByStatusAsync(
        AmazonInvoiceStatus status, int maxResults, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = NewCommand(connection, transaction: null, """
            -- TOP is applied to the invoices, not to the joined rows: capping the join would drop
            -- lines off the last invoice and we would invoice Amazon short.
            SELECT i.[Id], i.[OmniInvoiceNumber], i.[AmazonPurchaseOrderNumber], i.[CustomerAccountCode],
                   i.[WarehouseCode], i.[InvoiceDate], i.[CurrencyCode], i.[TotalExcludingTax], i.[TotalTax],
                   i.[TotalIncludingTax], i.[Status], i.[AttemptCount],
                   l.[LineNumber], l.[StockCode], l.[Barcode], l.[Asin], l.[PurchaseOrderNumber],
                   l.[Quantity], l.[UnitPriceExcludingTax], l.[LineTotalExcludingTax], l.[LineTax], l.[TaxRate]
            FROM   (SELECT TOP (@MaxResults) *
                    FROM   [AmazonEdi].[Invoice]
                    WHERE  [Status] = @Status
                    ORDER BY [InvoiceDate], [Id]) AS i
            LEFT JOIN [AmazonEdi].[InvoiceLine] AS l ON l.[InvoiceId] = i.[Id]
            ORDER BY i.[InvoiceDate], i.[Id], l.[LineNumber];
            """);

        command.Parameters.AddWithValue("@Status", status.ToString());
        command.Parameters.AddWithValue("@MaxResults", maxResults);

        var invoices = new Dictionary<long, (TrackedInvoiceHeader Header, List<OmniInvoiceLine> Lines)>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            if (!invoices.TryGetValue(id, out var entry))
            {
                entry = (new TrackedInvoiceHeader
                {
                    Id = id,
                    OmniInvoiceNumber = reader.GetString(1),
                    PurchaseOrderNumber = reader.IsDBNull(2) ? null : reader.GetString(2),
                    CustomerAccountCode = reader.GetString(3),
                    WarehouseCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    InvoiceDate = reader.GetDateTimeOffset(5),
                    CurrencyCode = reader.GetString(6),
                    TotalExcludingTax = reader.GetDecimal(7),
                    TotalTax = reader.GetDecimal(8),
                    TotalIncludingTax = reader.GetDecimal(9),
                    Status = Enum.Parse<AmazonInvoiceStatus>(reader.GetString(10)),
                    AttemptCount = reader.GetInt32(11),
                }, new List<OmniInvoiceLine>());

                invoices[id] = entry;
            }

            if (reader.IsDBNull(12))
            {
                continue;
            }

            entry.Lines.Add(new OmniInvoiceLine
            {
                LineNumber = reader.GetInt32(12),
                StockCode = reader.GetString(13),
                Barcode = reader.IsDBNull(14) ? null : reader.GetString(14),
                Asin = reader.IsDBNull(15) ? null : reader.GetString(15),
                PurchaseOrderNumber = reader.IsDBNull(16) ? null : reader.GetString(16),
                Quantity = reader.GetInt32(17),
                UnitPriceExcludingTax = reader.GetDecimal(18),
                LineTotalExcludingTax = reader.GetDecimal(19),
                LineTax = reader.GetDecimal(20),
                TaxRate = reader.GetDecimal(21),
            });
        }

        return invoices.Values
            .Select(entry => new TrackedInvoice
            {
                Id = entry.Header.Id,
                OmniInvoiceNumber = entry.Header.OmniInvoiceNumber,
                Status = entry.Header.Status,
                AttemptCount = entry.Header.AttemptCount,
                Source = entry.Header.ToOmniInvoice(entry.Lines),
            })
            .ToList();
    }

    public async Task SetStatusAsync(long id, AmazonInvoiceStatus status, string? lastError, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = NewCommand(connection, transaction: null, """
            UPDATE [AmazonEdi].[Invoice]
            SET    [Status] = @Status,
                   [LastError] = @LastError,
                   [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE  [Id] = @Id;
            """);

        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@Status", status.ToString());
        command.Parameters.AddWithValue("@LastError", (object?)lastError ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordSubmissionAsync(long id, SubmitInvoicesResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = NewCommand(connection, transaction, """
            INSERT INTO [AmazonEdi].[InvoiceAttempt]
                ([InvoiceId],[AttemptNumber],[HttpStatusCode],[Succeeded],[TransactionId],
                 [RequestJson],[ResponseJson],[ErrorMessage])
            SELECT @InvoiceId,
                   ISNULL((SELECT MAX([AttemptNumber]) FROM [AmazonEdi].[InvoiceAttempt] WHERE [InvoiceId] = @InvoiceId), 0) + 1,
                   @HttpStatusCode,@Succeeded,@TransactionId,@RequestJson,@ResponseJson,@ErrorMessage;
            """))
        {
            command.Parameters.AddWithValue("@InvoiceId", id);
            command.Parameters.AddWithValue("@HttpStatusCode", result.StatusCode);
            command.Parameters.AddWithValue("@Succeeded", result.Succeeded);
            command.Parameters.AddWithValue("@TransactionId", (object?)result.TransactionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@RequestJson", result.RequestJson);
            command.Parameters.AddWithValue("@ResponseJson", (object?)result.ResponseJson ?? DBNull.Value);
            command.Parameters.AddWithValue("@ErrorMessage", (object?)result.ErrorMessage ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = NewCommand(connection, transaction, """
            UPDATE [AmazonEdi].[Invoice]
            SET    [AttemptCount] = [AttemptCount] + 1,
                   [Status] = @Status,
                   [TransactionId] = COALESCE(@TransactionId, [TransactionId]),
                   [SubmittedAtUtc] = CASE WHEN @Succeeded = 1 THEN SYSUTCDATETIME() ELSE [SubmittedAtUtc] END,
                   [LastError] = @ErrorMessage,
                   [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE  [Id] = @Id;
            """))
        {
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@Succeeded", result.Succeeded);
            command.Parameters.AddWithValue("@Status",
                result.Succeeded ? AmazonInvoiceStatus.Submitted.ToString() : AmazonInvoiceStatus.Validated.ToString());
            command.Parameters.AddWithValue("@TransactionId", (object?)result.TransactionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@ErrorMessage", (object?)result.ErrorMessage ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetLatestInvoiceDateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = NewCommand(connection, transaction: null,
            "SELECT MAX([InvoiceDate]) FROM [AmazonEdi].[Invoice];");

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTimeOffset date ? date : null;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException("AmazonEdi:Persistence:ConnectionString is not configured.");
        }

        var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private SqlCommand NewCommand(SqlConnection connection, SqlTransaction? transaction, string sql) => new(sql, connection, transaction)
    {
        CommandType = CommandType.Text,
        CommandTimeout = _options.CommandTimeoutSeconds,
    };

    private sealed class TrackedInvoiceHeader
    {
        public required long Id { get; init; }
        public required string OmniInvoiceNumber { get; init; }
        public string? PurchaseOrderNumber { get; init; }
        public required string CustomerAccountCode { get; init; }
        public string? WarehouseCode { get; init; }
        public required DateTimeOffset InvoiceDate { get; init; }
        public required string CurrencyCode { get; init; }
        public required decimal TotalExcludingTax { get; init; }
        public required decimal TotalTax { get; init; }
        public required decimal TotalIncludingTax { get; init; }
        public required AmazonInvoiceStatus Status { get; init; }
        public required int AttemptCount { get; init; }

        public OmniInvoice ToOmniInvoice(IReadOnlyList<OmniInvoiceLine> lines) => new()
        {
            InvoiceNumber = OmniInvoiceNumber,
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
