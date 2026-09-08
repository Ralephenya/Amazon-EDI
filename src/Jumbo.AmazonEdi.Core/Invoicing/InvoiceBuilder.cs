using Jumbo.AmazonEdi.Core.Configuration;
using Jumbo.AmazonEdi.Core.Models;
using Jumbo.AmazonEdi.Core.Omni;

namespace Jumbo.AmazonEdi.Core.Invoicing;

/// <summary>Maps an Omni invoice onto the Amazon wire format. Pure - no I/O, no clock, no
/// arithmetic of its own beyond copying Omni's figures across.</summary>
public sealed class InvoiceBuilder
{
    private readonly AmazonEdiOptions _options;

    public InvoiceBuilder(AmazonEdiOptions options) => _options = options;

    public Invoice Build(OmniInvoice source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var invoice = new Invoice
        {
            InvoiceType = InvoiceTypes.Invoice,
            Id = source.InvoiceNumber,
            Date = source.InvoiceDate,
            ReferenceNumber = source.PurchaseOrderNumber,
            RemitToParty = BuildParty(_options.RemitToParty),
            BillToParty = BuildParty(_options.BillToParty),
            ShipFromParty = BuildShipFromParty(source.WarehouseCode),
            InvoiceTotal = new Money
            {
                CurrencyCode = source.CurrencyCode,
                Amount = source.TotalIncludingTax,
            },
            TaxDetails = BuildHeaderTaxDetails(source),
            Items = source.Lines.Select((line, index) => BuildItem(source, line, index + 1)).ToList(),
        };

        return invoice;
    }

    private InvoiceItem BuildItem(OmniInvoice source, OmniInvoiceLine line, int sequence) => new()
    {
        ItemSequenceNumber = sequence,
        AmazonProductIdentifier = NullIfBlank(line.Asin),
        VendorProductIdentifier = NullIfBlank(line.Barcode),
        PurchaseOrderNumber = NullIfBlank(line.PurchaseOrderNumber) ?? NullIfBlank(source.PurchaseOrderNumber),
        InvoicedQuantity = new ItemQuantity
        {
            Amount = line.Quantity,
            UnitOfMeasure = _options.UnitOfMeasure,
        },
        NetCost = new Money
        {
            CurrencyCode = source.CurrencyCode,
            Amount = line.UnitPriceExcludingTax,
        },
        TaxDetails = new List<TaxDetails>
        {
            new()
            {
                TaxType = TaxTypes.Vat,
                TaxRate = line.TaxRate,
                TaxAmount = new Money { CurrencyCode = source.CurrencyCode, Amount = line.LineTax },
                TaxableAmount = new Money { CurrencyCode = source.CurrencyCode, Amount = line.LineTotalExcludingTax },
            },
        },
    };

    /// <summary>One header tax block per distinct VAT rate, summed from the lines. Amazon accepts a
    /// single blended block, but a per-rate breakdown is what a zero-rated line needs.</summary>
    private static List<TaxDetails> BuildHeaderTaxDetails(OmniInvoice source) =>
        source.Lines
            .GroupBy(line => line.TaxRate)
            .OrderBy(group => group.Key)
            .Select(group => new TaxDetails
            {
                TaxType = TaxTypes.Vat,
                TaxRate = group.Key,
                TaxAmount = new Money
                {
                    CurrencyCode = source.CurrencyCode,
                    Amount = group.Sum(line => line.LineTax),
                },
                TaxableAmount = new Money
                {
                    CurrencyCode = source.CurrencyCode,
                    Amount = group.Sum(line => line.LineTotalExcludingTax),
                },
            })
            .ToList();

    private PartyIdentification? BuildShipFromParty(string? warehouseCode)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return null;
        }

        return _options.ShipFromPartiesByWarehouse.TryGetValue(warehouseCode, out var party)
            ? BuildParty(party)
            : null;
    }

    private static PartyIdentification BuildParty(PartyOptions options) => new()
    {
        PartyId = options.PartyId,
        Address = BuildAddress(options.Address),
        TaxRegistrationDetails = string.IsNullOrWhiteSpace(options.VatNumber)
            ? null
            : new List<TaxRegistrationDetails>
            {
                new() { TaxRegistrationType = "VAT", TaxRegistrationNumber = options.VatNumber },
            },
    };

    private static Address? BuildAddress(AddressOptions? options) => options is null
        ? null
        : new Address
        {
            Name = options.Name,
            AddressLine1 = options.AddressLine1,
            AddressLine2 = NullIfBlank(options.AddressLine2),
            AddressLine3 = NullIfBlank(options.AddressLine3),
            City = NullIfBlank(options.City),
            StateOrRegion = NullIfBlank(options.StateOrRegion),
            PostalOrZipCode = NullIfBlank(options.PostalOrZipCode),
            CountryCode = options.CountryCode,
            Phone = NullIfBlank(options.Phone),
        };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
