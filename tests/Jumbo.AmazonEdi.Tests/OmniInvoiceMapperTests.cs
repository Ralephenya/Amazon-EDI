using Jumbo.AmazonEdi.Omni;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

/// <summary>The mapping from the stored procedure's two result sets onto invoices. This is the part
/// that can quietly produce a wrong invoice, so it is tested directly rather than through SQL.</summary>
public class OmniInvoiceMapperTests
{
    private static InvoiceHeaderRow Header(string invoiceNumber) => new()
    {
        InvoiceNumber = invoiceNumber,
        InvoiceDate = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(2)),
        PurchaseOrderNumber = "7TX9K2LM",
        CustomerAccountCode = "AMZN",
        WarehouseCode = "JHB",
        CurrencyCode = "ZAR",
        TotalExcludingTax = 444.00m,
        TotalTax = 66.60m,
        TotalIncludingTax = 510.60m,
    };

    private static InvoiceLineRow Line(string invoiceNumber, int lineNumber) => new()
    {
        InvoiceNumber = invoiceNumber,
        LineNumber = lineNumber,
        StockCode = $"JB-{lineNumber:000}",
        Barcode = "6001234567890",
        Quantity = 24,
        UnitPriceExcludingTax = 18.50m,
        LineTotalExcludingTax = 444.00m,
        LineTax = 66.60m,
        TaxRate = 15m,
    };

    [Fact]
    public void Lines_are_attached_to_the_right_invoice()
    {
        var invoices = OmniInvoiceMapper.Combine(
            new[] { Header("INV-1"), Header("INV-2") },
            new[] { Line("INV-1", 1), Line("INV-2", 1), Line("INV-2", 2) });

        Assert.Equal(2, invoices.Count);
        Assert.Single(invoices.Single(invoice => invoice.InvoiceNumber == "INV-1").Lines);
        Assert.Equal(2, invoices.Single(invoice => invoice.InvoiceNumber == "INV-2").Lines.Count);
    }

    [Fact]
    public void Lines_come_back_in_line_number_order_whatever_order_the_procedure_returned_them_in()
    {
        var invoices = OmniInvoiceMapper.Combine(
            new[] { Header("INV-1") },
            new[] { Line("INV-1", 3), Line("INV-1", 1), Line("INV-1", 2) });

        Assert.Equal(new[] { 1, 2, 3 }, invoices[0].Lines.Select(line => line.LineNumber));
    }

    [Fact]
    public void An_invoice_with_no_lines_is_kept_rather_than_silently_dropped()
    {
        // It is then held by the validator with "Invoice has no lines", which is visible.
        // Dropping it here would be exactly the silent loss this rebuild exists to avoid.
        var invoices = OmniInvoiceMapper.Combine(new[] { Header("INV-1") }, Array.Empty<InvoiceLineRow>());

        var invoice = Assert.Single(invoices);
        Assert.Empty(invoice.Lines);
    }

    [Fact]
    public void Every_money_field_is_carried_across_untouched()
    {
        var line = Line("INV-1", 1);
        line.UnitPriceExcludingTax = 18.5075m;
        line.LineTotalExcludingTax = 444.18m;
        line.LineTax = 66.6270m;
        line.TaxRate = 15m;

        var mapped = OmniInvoiceMapper.Combine(new[] { Header("INV-1") }, new[] { line })[0].Lines[0];

        Assert.Equal(18.5075m, mapped.UnitPriceExcludingTax);
        Assert.Equal(444.18m, mapped.LineTotalExcludingTax);
        Assert.Equal(66.6270m, mapped.LineTax);
        Assert.Equal(15m, mapped.TaxRate);
    }

    [Fact]
    public void Nullable_columns_survive_as_nulls()
    {
        var header = Header("INV-1");
        header.WarehouseCode = null;
        header.PurchaseOrderNumber = null;

        var line = Line("INV-1", 1);
        line.Barcode = null;
        line.Asin = null;
        line.PurchaseOrderNumber = null;

        var invoice = OmniInvoiceMapper.Combine(new[] { header }, new[] { line })[0];

        Assert.Null(invoice.WarehouseCode);
        Assert.Null(invoice.PurchaseOrderNumber);
        Assert.Null(invoice.Lines[0].Barcode);
        Assert.Null(invoice.Lines[0].Asin);
    }

    [Fact]
    public void Invoice_numbers_are_matched_case_insensitively()
    {
        var invoices = OmniInvoiceMapper.Combine(new[] { Header("inv-1") }, new[] { Line("INV-1", 1) });

        Assert.Single(invoices[0].Lines);
    }
}
