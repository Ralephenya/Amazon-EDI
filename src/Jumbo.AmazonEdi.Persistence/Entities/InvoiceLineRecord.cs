namespace Jumbo.AmazonEdi.Persistence.Entities;

/// <summary>A snapshot of what we sent, so a later reconciliation against Amazon does not depend on
/// Omni still holding the same figures.</summary>
public class InvoiceLineRecord
{
    public long Id { get; set; }

    public long InvoiceId { get; set; }

    public InvoiceRecord? Invoice { get; set; }

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
