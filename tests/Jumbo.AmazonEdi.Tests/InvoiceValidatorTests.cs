using Jumbo.AmazonEdi.Core.Invoicing;
using Jumbo.AmazonEdi.Core.Omni;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

public class InvoiceValidatorTests
{
    private readonly InvoiceBuilder _builder = new(TestData.Options());
    private readonly InvoiceValidator _validator = new(TestData.Options());

    private ValidationResult Validate(OmniInvoice invoice) => _validator.Validate(invoice, _builder.Build(invoice));

    [Fact]
    public void A_clean_invoice_passes()
    {
        var result = Validate(TestData.Invoice());

        Assert.True(result.IsValid, result.Message);
    }

    [Fact]
    public void A_missing_po_number_is_rejected_with_an_actionable_message()
    {
        // The test lines carry no PO of their own, so clearing the header leaves nothing to match on.
        var invoice = TestData.Invoice(builder => builder.PurchaseOrderNumber = null);

        var result = Validate(invoice);

        Assert.False(result.IsValid);
        Assert.Contains("Amazon PO number", result.Message, StringComparison.Ordinal);
        Assert.Contains("Omni", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_one_cent_mismatch_between_lines_and_the_header_is_caught()
    {
        // This is the check Erica does by eye today.
        var invoice = TestData.Invoice();
        var tampered = new OmniInvoice
        {
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            PurchaseOrderNumber = invoice.PurchaseOrderNumber,
            CustomerAccountCode = invoice.CustomerAccountCode,
            WarehouseCode = invoice.WarehouseCode,
            CurrencyCode = invoice.CurrencyCode,
            TotalExcludingTax = invoice.TotalExcludingTax + 0.10m,
            TotalTax = invoice.TotalTax,
            TotalIncludingTax = invoice.TotalIncludingTax + 0.10m,
            Lines = invoice.Lines,
        };

        var result = Validate(tampered);

        Assert.False(result.IsValid);
        Assert.Contains("excluding tax", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_whose_price_times_quantity_does_not_match_its_total_is_caught()
    {
        var invoice = TestData.Invoice(builder =>
            builder.Lines[0] = new OmniInvoiceLine
            {
                LineNumber = 1,
                StockCode = "JB-CHOC-100",
                Barcode = "6001234567890",
                Quantity = 24,
                UnitPriceExcludingTax = 18.50m,
                LineTotalExcludingTax = 450.00m, // should be 444.00
                LineTax = 67.50m,
                TaxRate = 15m,
            });

        var result = Validate(invoice);

        Assert.False(result.IsValid);
        Assert.Contains("JB-CHOC-100", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_with_no_barcode_and_no_asin_is_rejected()
    {
        var invoice = TestData.Invoice(builder =>
            builder.Lines[1] = new OmniInvoiceLine
            {
                LineNumber = 2,
                StockCode = "JB-MILK-500",
                Barcode = null,
                Asin = null,
                Quantity = 12,
                UnitPriceExcludingTax = 25.00m,
                LineTotalExcludingTax = 300.00m,
                LineTax = 0m,
                TaxRate = 0m,
            });

        var result = Validate(invoice);

        Assert.False(result.IsValid);
        Assert.Contains("cannot identify the item", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_vat_rate_is_held_for_review()
    {
        var invoice = TestData.Invoice(builder =>
            builder.Lines[0] = new OmniInvoiceLine
            {
                LineNumber = 1,
                StockCode = "JB-CHOC-100",
                Barcode = "6001234567890",
                Quantity = 24,
                UnitPriceExcludingTax = 18.50m,
                LineTotalExcludingTax = 444.00m,
                LineTax = 62.16m,
                TaxRate = 14m, // the old SA rate
            });

        var result = Validate(invoice);

        Assert.False(result.IsValid);
        Assert.Contains("VAT rate 14%", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_zar_invoice_is_rejected()
    {
        var invoice = TestData.Invoice(builder => builder.CurrencyCode = "USD");

        var result = Validate(invoice);

        Assert.False(result.IsValid);
        Assert.Contains("USD", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void All_problems_are_reported_at_once_rather_than_the_first_one()
    {
        var invoice = TestData.Invoice(builder =>
        {
            builder.PurchaseOrderNumber = null;
            builder.CurrencyCode = "USD";
        });

        var result = Validate(invoice);

        Assert.False(result.IsValid);
        Assert.True(result.Failures.Count >= 2, result.Message);
    }
}
