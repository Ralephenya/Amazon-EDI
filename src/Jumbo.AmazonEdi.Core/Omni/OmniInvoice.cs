namespace Jumbo.AmazonEdi.Core.Omni;

/// <summary>An invoice as Omni holds it. This - not the Amazon PO - is the source of truth for what
/// we bill: lines can be short-shipped or declined, so PO quantities are not authoritative.</summary>
public sealed class OmniInvoice
{
    /// <summary>Omni invoice number. Becomes the Amazon invoice id and our idempotency key.</summary>
    public required string InvoiceNumber { get; init; }

    public required DateTimeOffset InvoiceDate { get; init; }

    /// <summary>The Amazon PO number captured against the order in Omni. Amazon matches on this.</summary>
    public string? PurchaseOrderNumber { get; init; }

    /// <summary>Omni customer/debtor account, used to pick the vendor code and ship-from warehouse.</summary>
    public required string CustomerAccountCode { get; init; }

    public string? WarehouseCode { get; init; }

    /// <summary>ISO currency code, e.g. "ZAR".</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>Gross document total, including tax, as Omni computed it. We reconcile against this
    /// rather than trusting our own arithmetic over the lines.</summary>
    public required decimal TotalIncludingTax { get; init; }

    public required decimal TotalExcludingTax { get; init; }

    public required decimal TotalTax { get; init; }

    public required IReadOnlyList<OmniInvoiceLine> Lines { get; init; }
}

public sealed class OmniInvoiceLine
{
    public required int LineNumber { get; init; }

    /// <summary>Our stock code, for diagnostics only - not sent to Amazon.</summary>
    public required string StockCode { get; init; }

    public string? Barcode { get; init; }

    public string? Asin { get; init; }

    /// <summary>Per-line PO number where Omni carries one; falls back to the header PO.</summary>
    public string? PurchaseOrderNumber { get; init; }

    public required int Quantity { get; init; }

    /// <summary>Unit price excluding tax.</summary>
    public required decimal UnitPriceExcludingTax { get; init; }

    /// <summary>Line total excluding tax, as Omni computed it (may include rounding we must not redo).</summary>
    public required decimal LineTotalExcludingTax { get; init; }

    public required decimal LineTax { get; init; }

    /// <summary>VAT rate as a percentage, e.g. 15.</summary>
    public required decimal TaxRate { get; init; }
}
