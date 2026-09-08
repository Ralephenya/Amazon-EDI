using Jumbo.AmazonEdi.Core.Invoicing;
using Jumbo.AmazonEdi.Core.Models;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

public class InvoiceBuilderTests
{
    private readonly InvoiceBuilder _builder = new(TestData.Options());

    [Fact]
    public void Build_maps_the_header_onto_the_amazon_payload()
    {
        var payload = _builder.Build(TestData.Invoice());

        Assert.Equal(InvoiceTypes.Invoice, payload.InvoiceType);
        Assert.Equal("INV-100045", payload.Id);
        Assert.Equal("JUMBO01", payload.RemitToParty.PartyId);
        Assert.Equal("AMZNZA", payload.BillToParty!.PartyId);
        Assert.Equal("ZAR", payload.InvoiceTotal.CurrencyCode);
        Assert.Equal(810.60m, payload.InvoiceTotal.Amount);
    }

    [Fact]
    public void Build_puts_our_vat_number_on_the_remit_to_party()
    {
        var payload = _builder.Build(TestData.Invoice());

        var registration = Assert.Single(payload.RemitToParty.TaxRegistrationDetails!);
        Assert.Equal("VAT", registration.TaxRegistrationType);
        Assert.Equal("4123456789", registration.TaxRegistrationNumber);
    }

    [Fact]
    public void Build_sends_unit_cost_excluding_tax_not_the_line_total()
    {
        var payload = _builder.Build(TestData.Invoice());

        var first = payload.Items![0];
        Assert.Equal(18.50m, first.NetCost.Amount);
        Assert.Equal(24, first.InvoicedQuantity.Amount);
        Assert.Equal(UnitsOfMeasure.Eaches, first.InvoicedQuantity.UnitOfMeasure);
    }

    [Fact]
    public void Build_puts_the_po_number_on_every_line_because_amazon_matches_on_it()
    {
        var payload = _builder.Build(TestData.Invoice());

        Assert.All(payload.Items!, item => Assert.Equal("7TX9K2LM", item.PurchaseOrderNumber));
    }

    [Fact]
    public void Build_numbers_lines_from_one_in_order()
    {
        var payload = _builder.Build(TestData.Invoice());

        Assert.Equal(new[] { 1, 2 }, payload.Items!.Select(item => item.ItemSequenceNumber));
    }

    [Fact]
    public void Build_breaks_header_tax_down_per_rate_so_zero_rated_lines_stay_visible()
    {
        var payload = _builder.Build(TestData.Invoice());

        Assert.Equal(2, payload.TaxDetails!.Count);

        var zeroRated = payload.TaxDetails.Single(tax => tax.TaxRate == 0m);
        Assert.Equal(0m, zeroRated.TaxAmount.Amount);
        Assert.Equal(300.00m, zeroRated.TaxableAmount!.Amount);

        var standardRated = payload.TaxDetails.Single(tax => tax.TaxRate == 15m);
        Assert.Equal(66.60m, standardRated.TaxAmount.Amount);
        Assert.Equal(444.00m, standardRated.TaxableAmount!.Amount);
    }

    [Fact]
    public void Build_omits_nulls_so_amazon_never_sees_an_empty_optional_object()
    {
        var json = AmazonJson.Serialize(_builder.Build(TestData.Invoice()));

        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hsnCode", json, StringComparison.Ordinal);
    }
}
