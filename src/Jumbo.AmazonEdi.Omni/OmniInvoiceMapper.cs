using Jumbo.AmazonEdi.Core.Omni;

namespace Jumbo.AmazonEdi.Omni;

/// <summary>Joins the two result sets into invoices. Kept pure and separate from the database call
/// so the mapping - the part that can quietly produce a wrong invoice - is directly testable.</summary>
internal static class OmniInvoiceMapper
{
    public static IReadOnlyList<OmniInvoice> Combine(
        IEnumerable<InvoiceHeaderRow> headers,
        IEnumerable<InvoiceLineRow> lines)
    {
        var linesByInvoice = lines
            .GroupBy(line => line.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OmniInvoiceLine>)group
                    .OrderBy(line => line.LineNumber)
                    .Select(MapLine)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        return headers.Select(header => MapHeader(header, linesByInvoice)).ToList();
    }

    private static OmniInvoice MapHeader(
        InvoiceHeaderRow header,
        IReadOnlyDictionary<string, IReadOnlyList<OmniInvoiceLine>> linesByInvoice) => new()
    {
        InvoiceNumber = header.InvoiceNumber,
        InvoiceDate = header.InvoiceDate,
        PurchaseOrderNumber = header.PurchaseOrderNumber,
        CustomerAccountCode = header.CustomerAccountCode,
        WarehouseCode = header.WarehouseCode,
        CurrencyCode = header.CurrencyCode,
        TotalExcludingTax = header.TotalExcludingTax,
        TotalTax = header.TotalTax,
        TotalIncludingTax = header.TotalIncludingTax,

        // An invoice whose lines are missing comes back empty rather than being dropped: the
        // validator then holds it with "Invoice has no lines", which is visible. Dropping it here
        // would be a silent loss, which is the failure mode this whole rebuild exists to avoid.
        Lines = linesByInvoice.TryGetValue(header.InvoiceNumber, out var lines)
            ? lines
            : Array.Empty<OmniInvoiceLine>(),
    };

    // Written out field by field on purpose. Relying on name matching here would let a renamed or
    // mistyped column silently arrive as zero, and a zero LineTax ships a wrong invoice to Amazon.
    private static OmniInvoiceLine MapLine(InvoiceLineRow line) => new()
    {
        LineNumber = line.LineNumber,
        StockCode = line.StockCode,
        Barcode = line.Barcode,
        Asin = line.Asin,
        PurchaseOrderNumber = line.PurchaseOrderNumber,
        Quantity = line.Quantity,
        UnitPriceExcludingTax = line.UnitPriceExcludingTax,
        LineTotalExcludingTax = line.LineTotalExcludingTax,
        LineTax = line.LineTax,
        TaxRate = line.TaxRate,
    };
}
