namespace Jumbo.AmazonEdi.Omni;

// Shapes of the two result sets the stored procedure returns. Dapper materialises these by column
// name, so the property names are part of the procedure's contract - see
// docs/omni-stored-procedure.md before renaming anything.

internal sealed class InvoiceHeaderRow
{
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTimeOffset InvoiceDate { get; set; }
    public string? PurchaseOrderNumber { get; set; }
    public string CustomerAccountCode { get; set; } = string.Empty;
    public string? WarehouseCode { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public decimal TotalExcludingTax { get; set; }
    public decimal TotalTax { get; set; }
    public decimal TotalIncludingTax { get; set; }
}

internal sealed class InvoiceLineRow
{
    /// <summary>Ties the line back to its header. Present on the line result set for that reason.</summary>
    public string InvoiceNumber { get; set; } = string.Empty;

    public int LineNumber { get; set; }
    public string StockCode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string? Asin { get; set; }
    public string? PurchaseOrderNumber { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPriceExcludingTax { get; set; }
    public decimal LineTotalExcludingTax { get; set; }
    public decimal LineTax { get; set; }
    public decimal TaxRate { get; set; }
}
