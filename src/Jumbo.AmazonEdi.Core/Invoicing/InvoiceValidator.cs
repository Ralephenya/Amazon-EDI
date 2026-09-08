using Jumbo.AmazonEdi.Core.Configuration;
using Jumbo.AmazonEdi.Core.Models;
using Jumbo.AmazonEdi.Core.Omni;

namespace Jumbo.AmazonEdi.Core.Invoicing;

/// <summary>Everything we check before an invoice is allowed anywhere near Amazon. Each rule
/// collects a specific reason rather than throwing, so one invoice reports all its problems at once
/// and a bad invoice never aborts the batch.</summary>
public sealed class InvoiceValidator
{
    /// <summary>Rounding tolerance when reconciling lines against the document total.</summary>
    private const decimal Tolerance = 0.01m;

    private readonly AmazonEdiOptions _options;

    public InvoiceValidator(AmazonEdiOptions options) => _options = options;

    public ValidationResult Validate(OmniInvoice source, Invoice payload)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payload);

        var failures = new List<string>();

        ValidateIdentity(source, failures);
        ValidateCurrency(source, failures);
        ValidateParties(failures);
        ValidateLines(source, failures);
        ValidateTotals(source, failures);

        return failures.Count == 0 ? ValidationResult.Success() : ValidationResult.Failed(failures);
    }

    private static void ValidateIdentity(OmniInvoice source, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(source.InvoiceNumber))
        {
            failures.Add("Omni invoice number is missing.");
        }

        if (source.InvoiceDate == default)
        {
            failures.Add("Invoice date is missing.");
        }

        // Amazon matches an invoice to a PO. A line with no PO number on it will be rejected on
        // Amazon's side with far less explanation than this.
        var linesWithoutPo = source.Lines
            .Where(line => string.IsNullOrWhiteSpace(line.PurchaseOrderNumber)
                        && string.IsNullOrWhiteSpace(source.PurchaseOrderNumber))
            .Select(line => line.LineNumber)
            .ToList();

        if (linesWithoutPo.Count > 0)
        {
            failures.Add(
                $"No Amazon PO number on line(s) {string.Join(", ", linesWithoutPo)} and none on the invoice header. " +
                "Capture the Amazon PO number against the order in Omni.");
        }
    }

    private void ValidateCurrency(OmniInvoice source, List<string> failures)
    {
        if (!string.Equals(source.CurrencyCode, _options.ExpectedCurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Invoice currency is '{source.CurrencyCode}' but Amazon is invoiced in '{_options.ExpectedCurrencyCode}'.");
        }
    }

    private void ValidateParties(List<string> failures)
    {
        ValidateParty(_options.RemitToParty, "remit-to", requireVatNumber: true, failures);
        ValidateParty(_options.BillToParty, "bill-to", requireVatNumber: false, failures);
    }

    private static void ValidateParty(PartyOptions party, string label, bool requireVatNumber, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(party.PartyId))
        {
            failures.Add($"Configuration: {label} party id is not set.");
        }

        if (party.Address is null)
        {
            failures.Add($"Configuration: {label} address is not set. Amazon rejects invoices with an incomplete {label} address.");
            return;
        }

        if (string.IsNullOrWhiteSpace(party.Address.Name)
            || string.IsNullOrWhiteSpace(party.Address.AddressLine1)
            || string.IsNullOrWhiteSpace(party.Address.CountryCode))
        {
            failures.Add($"Configuration: {label} address needs at least name, address line 1 and country code.");
        }

        if (requireVatNumber && string.IsNullOrWhiteSpace(party.VatNumber))
        {
            failures.Add($"Configuration: {label} VAT number is not set.");
        }
    }

    private void ValidateLines(OmniInvoice source, List<string> failures)
    {
        if (source.Lines.Count == 0)
        {
            failures.Add("Invoice has no lines.");
            return;
        }

        foreach (var line in source.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Barcode) && string.IsNullOrWhiteSpace(line.Asin))
            {
                failures.Add($"Line {line.LineNumber} ({line.StockCode}) has neither a barcode nor an ASIN, so Amazon cannot identify the item.");
            }

            if (line.Quantity <= 0)
            {
                failures.Add($"Line {line.LineNumber} ({line.StockCode}) has quantity {line.Quantity}.");
            }

            if (line.UnitPriceExcludingTax < 0)
            {
                failures.Add($"Line {line.LineNumber} ({line.StockCode}) has a negative unit price.");
            }

            if (!_options.AllowedTaxRates.Contains(line.TaxRate))
            {
                failures.Add(
                    $"Line {line.LineNumber} ({line.StockCode}) has VAT rate {line.TaxRate}%, which is not one of the expected rates " +
                    $"({string.Join(", ", _options.AllowedTaxRates.Select(rate => $"{rate}%"))}).");
            }

            var expectedLineTotal = decimal.Round(line.UnitPriceExcludingTax * line.Quantity, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(expectedLineTotal - line.LineTotalExcludingTax) > Tolerance)
            {
                failures.Add(
                    $"Line {line.LineNumber} ({line.StockCode}): unit price {line.UnitPriceExcludingTax} x qty {line.Quantity} = " +
                    $"{expectedLineTotal}, but Omni's line total is {line.LineTotalExcludingTax}.");
            }
        }
    }

    /// <summary>The check Erica does by eye today: do the lines add up to the document total.</summary>
    private static void ValidateTotals(OmniInvoice source, List<string> failures)
    {
        var linesExcludingTax = source.Lines.Sum(line => line.LineTotalExcludingTax);
        if (Math.Abs(linesExcludingTax - source.TotalExcludingTax) > Tolerance)
        {
            failures.Add($"Lines total {linesExcludingTax} excluding tax but the invoice header says {source.TotalExcludingTax}.");
        }

        var linesTax = source.Lines.Sum(line => line.LineTax);
        if (Math.Abs(linesTax - source.TotalTax) > Tolerance)
        {
            failures.Add($"Line tax totals {linesTax} but the invoice header says {source.TotalTax}.");
        }

        var expectedGross = source.TotalExcludingTax + source.TotalTax;
        if (Math.Abs(expectedGross - source.TotalIncludingTax) > Tolerance)
        {
            failures.Add($"Invoice total excluding tax {source.TotalExcludingTax} plus tax {source.TotalTax} = {expectedGross}, but the gross total is {source.TotalIncludingTax}.");
        }
    }
}
