namespace Jumbo.AmazonEdi.Omni;

public sealed class OmniOptions
{
    public const string SectionName = "AmazonEdi:Omni";

    /// <summary>Read-only connection to Omni. Use an account with SELECT and nothing else.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public int CommandTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Header query. Kept in configuration because the Omni schema - in particular which field holds
    /// the Amazon PO number - is a discovery item, and getting it wrong should be a config change
    /// rather than a redeploy.
    ///
    /// Must accept @Since, @MaxResults and the @AccountN parameters produced from the configured
    /// Amazon customer accounts, and must return these columns, in any order:
    ///   InvoiceNumber, InvoiceDate, PurchaseOrderNumber, CustomerAccountCode, WarehouseCode,
    ///   CurrencyCode, TotalExcludingTax, TotalTax, TotalIncludingTax
    /// </summary>
    public string HeaderQuery { get; set; } = string.Empty;

    /// <summary>
    /// Line query, run once per invoice with @InvoiceNumber. Must return:
    ///   LineNumber, StockCode, Barcode, Asin, PurchaseOrderNumber, Quantity,
    ///   UnitPriceExcludingTax, LineTotalExcludingTax, LineTax, TaxRate
    /// </summary>
    public string LineQuery { get; set; } = string.Empty;
}
