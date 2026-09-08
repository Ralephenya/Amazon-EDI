using Jumbo.AmazonEdi.Core.Configuration;
using Jumbo.AmazonEdi.Core.Models;
using Jumbo.AmazonEdi.Core.Omni;

namespace Jumbo.AmazonEdi.Tests;

internal static class TestData
{
    public static AmazonEdiOptions Options() => new()
    {
        ExpectedCurrencyCode = "ZAR",
        AllowedTaxRates = new List<decimal> { 0m, 15m },
        UnitOfMeasure = UnitsOfMeasure.Eaches,
        AmazonCustomerAccountCodes = new List<string> { "AMZN" },
        RemitToParty = new PartyOptions
        {
            PartyId = "JUMBO01",
            VatNumber = "4123456789",
            Address = new AddressOptions
            {
                Name = "Jumbo Brands (Pty) Ltd",
                AddressLine1 = "1 Jumbo Road",
                City = "Johannesburg",
                PostalOrZipCode = "2001",
                CountryCode = "ZA",
            },
        },
        BillToParty = new PartyOptions
        {
            PartyId = "AMZNZA",
            Address = new AddressOptions
            {
                Name = "Amazon.com Sales, Inc.",
                AddressLine1 = "1 Amazon Way",
                City = "Cape Town",
                PostalOrZipCode = "8001",
                CountryCode = "ZA",
            },
        },
    };

    /// <summary>A realistic two-line ZAR invoice: one standard-rated line, one zero-rated.</summary>
    public static OmniInvoice Invoice(Action<OmniInvoiceBuilder>? customise = null)
    {
        var builder = new OmniInvoiceBuilder();
        customise?.Invoke(builder);
        return builder.Build();
    }

    internal sealed class OmniInvoiceBuilder
    {
        public string InvoiceNumber { get; set; } = "INV-100045";
        public string? PurchaseOrderNumber { get; set; } = "7TX9K2LM";
        public string CurrencyCode { get; set; } = "ZAR";
        public List<OmniInvoiceLine> Lines { get; set; } = new()
        {
            new OmniInvoiceLine
            {
                LineNumber = 1,
                StockCode = "JB-CHOC-100",
                Barcode = "6001234567890",
                Quantity = 24,
                UnitPriceExcludingTax = 18.50m,
                LineTotalExcludingTax = 444.00m,
                LineTax = 66.60m,
                TaxRate = 15m,
            },
            new OmniInvoiceLine
            {
                LineNumber = 2,
                StockCode = "JB-MILK-500",
                Barcode = "6009876543210",
                Quantity = 12,
                UnitPriceExcludingTax = 25.00m,
                LineTotalExcludingTax = 300.00m,
                LineTax = 0m,
                TaxRate = 0m,
            },
        };

        public OmniInvoice Build()
        {
            var excludingTax = Lines.Sum(line => line.LineTotalExcludingTax);
            var tax = Lines.Sum(line => line.LineTax);

            return new OmniInvoice
            {
                InvoiceNumber = InvoiceNumber,
                InvoiceDate = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(2)),
                PurchaseOrderNumber = PurchaseOrderNumber,
                CustomerAccountCode = "AMZN",
                WarehouseCode = "JHB",
                CurrencyCode = CurrencyCode,
                TotalExcludingTax = excludingTax,
                TotalTax = tax,
                TotalIncludingTax = excludingTax + tax,
                Lines = Lines,
            };
        }
    }
}
