using System.Text.Json.Serialization;

namespace Jumbo.AmazonEdi.Core.Models;

// Wire models for the Amazon SP-API Vendor Invoices API (POST /vendor/payments/v1/invoices).
// Property names and enum values are taken verbatim from Amazon's published model:
// https://github.com/amzn/selling-partner-api-models/blob/main/models/vendor-invoices-api-model/vendorInvoices.json
// InvoicePayloadContractTests asserts they still match that file - do not rename anything here
// without updating the vendored copy under tests/.
//
// Nulls are omitted on serialization (see AmazonJson.SerializerOptions): Amazon rejects some
// optional objects when present but empty, so "absent" and "null" must not be conflated.

public sealed class SubmitInvoicesRequest
{
    [JsonPropertyName("invoices")]
    public List<Invoice> Invoices { get; set; } = new();
}

public sealed class Invoice
{
    /// <summary>"Invoice" or "CreditNote".</summary>
    [JsonPropertyName("invoiceType")]
    public string InvoiceType { get; set; } = InvoiceTypes.Invoice;

    /// <summary>Our invoice number. Must be unique - Amazon treats a repeat as a duplicate document.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("referenceNumber")]
    public string? ReferenceNumber { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("remitToParty")]
    public PartyIdentification RemitToParty { get; set; } = new();

    [JsonPropertyName("shipToParty")]
    public PartyIdentification? ShipToParty { get; set; }

    [JsonPropertyName("shipFromParty")]
    public PartyIdentification? ShipFromParty { get; set; }

    [JsonPropertyName("billToParty")]
    public PartyIdentification? BillToParty { get; set; }

    [JsonPropertyName("paymentTerms")]
    public PaymentTerms? PaymentTerms { get; set; }

    /// <summary>Gross invoice total, including tax.</summary>
    [JsonPropertyName("invoiceTotal")]
    public Money InvoiceTotal { get; set; } = new();

    [JsonPropertyName("taxDetails")]
    public List<TaxDetails>? TaxDetails { get; set; }

    [JsonPropertyName("additionalDetails")]
    public List<AdditionalDetails>? AdditionalDetails { get; set; }

    [JsonPropertyName("chargeDetails")]
    public List<ChargeDetails>? ChargeDetails { get; set; }

    [JsonPropertyName("allowanceDetails")]
    public List<AllowanceDetails>? AllowanceDetails { get; set; }

    [JsonPropertyName("items")]
    public List<InvoiceItem>? Items { get; set; }
}

public sealed class InvoiceItem
{
    /// <summary>1-based line number within the invoice.</summary>
    [JsonPropertyName("itemSequenceNumber")]
    public int ItemSequenceNumber { get; set; }

    /// <summary>ASIN.</summary>
    [JsonPropertyName("amazonProductIdentifier")]
    public string? AmazonProductIdentifier { get; set; }

    /// <summary>Our own item identifier as Amazon holds it (typically the barcode / EAN).</summary>
    [JsonPropertyName("vendorProductIdentifier")]
    public string? VendorProductIdentifier { get; set; }

    [JsonPropertyName("invoicedQuantity")]
    public ItemQuantity InvoicedQuantity { get; set; } = new();

    /// <summary>Unit cost, excluding tax.</summary>
    [JsonPropertyName("netCost")]
    public Money NetCost { get; set; } = new();

    /// <summary>The Amazon PO this line is billed against. Amazon matches invoices on this.</summary>
    [JsonPropertyName("purchaseOrderNumber")]
    public string? PurchaseOrderNumber { get; set; }

    [JsonPropertyName("hsnCode")]
    public string? HsnCode { get; set; }

    [JsonPropertyName("creditNoteDetails")]
    public CreditNoteDetails? CreditNoteDetails { get; set; }

    [JsonPropertyName("taxDetails")]
    public List<TaxDetails>? TaxDetails { get; set; }

    [JsonPropertyName("chargeDetails")]
    public List<ChargeDetails>? ChargeDetails { get; set; }

    [JsonPropertyName("allowanceDetails")]
    public List<AllowanceDetails>? AllowanceDetails { get; set; }
}

public sealed class PartyIdentification
{
    [JsonPropertyName("partyId")]
    public string PartyId { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public Address? Address { get; set; }

    [JsonPropertyName("taxRegistrationDetails")]
    public List<TaxRegistrationDetails>? TaxRegistrationDetails { get; set; }
}

public sealed class Address
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("addressLine1")]
    public string AddressLine1 { get; set; } = string.Empty;

    [JsonPropertyName("addressLine2")]
    public string? AddressLine2 { get; set; }

    [JsonPropertyName("addressLine3")]
    public string? AddressLine3 { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("county")]
    public string? County { get; set; }

    [JsonPropertyName("district")]
    public string? District { get; set; }

    [JsonPropertyName("stateOrRegion")]
    public string? StateOrRegion { get; set; }

    [JsonPropertyName("postalOrZipCode")]
    public string? PostalOrZipCode { get; set; }

    /// <summary>Two-letter country code, e.g. "ZA".</summary>
    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
}

public sealed class TaxRegistrationDetails
{
    /// <summary>"VAT" or "GST".</summary>
    [JsonPropertyName("taxRegistrationType")]
    public string TaxRegistrationType { get; set; } = "VAT";

    [JsonPropertyName("taxRegistrationNumber")]
    public string TaxRegistrationNumber { get; set; } = string.Empty;
}

public sealed class TaxDetails
{
    /// <summary>"VAT" for South Africa.</summary>
    [JsonPropertyName("taxType")]
    public string TaxType { get; set; } = TaxTypes.Vat;

    /// <summary>Rate as a percentage, e.g. 15 for 15%.</summary>
    [JsonPropertyName("taxRate")]
    public decimal? TaxRate { get; set; }

    [JsonPropertyName("taxAmount")]
    public Money TaxAmount { get; set; } = new();

    [JsonPropertyName("taxableAmount")]
    public Money? TaxableAmount { get; set; }
}

public sealed class Money
{
    [JsonPropertyName("currencyCode")]
    public string CurrencyCode { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }
}

public sealed class ItemQuantity
{
    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    /// <summary>"Cases" or "Eaches".</summary>
    [JsonPropertyName("unitOfMeasure")]
    public string UnitOfMeasure { get; set; } = UnitsOfMeasure.Eaches;

    /// <summary>Number of eaches per case. Only meaningful when UnitOfMeasure is "Cases".</summary>
    [JsonPropertyName("unitSize")]
    public int? UnitSize { get; set; }

    [JsonPropertyName("totalWeight")]
    public TotalWeight? TotalWeight { get; set; }
}

public sealed class TotalWeight
{
    [JsonPropertyName("unitOfMeasure")]
    public string UnitOfMeasure { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public string Amount { get; set; } = string.Empty;
}

public sealed class ChargeDetails
{
    /// <summary>See <see cref="ChargeTypes"/>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("chargeAmount")]
    public Money ChargeAmount { get; set; } = new();

    [JsonPropertyName("taxDetails")]
    public List<TaxDetails>? TaxDetails { get; set; }
}

public sealed class AllowanceDetails
{
    /// <summary>See <see cref="AllowanceTypes"/>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("allowanceAmount")]
    public Money AllowanceAmount { get; set; } = new();

    [JsonPropertyName("taxDetails")]
    public List<TaxDetails>? TaxDetails { get; set; }
}

public sealed class AdditionalDetails
{
    /// <summary>"SUR", "OCR" or "CartonCount".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("languageCode")]
    public string? LanguageCode { get; set; }
}

public sealed class PaymentTerms
{
    /// <summary>See <see cref="PaymentTermsTypes"/>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("discountPercent")]
    public decimal? DiscountPercent { get; set; }

    [JsonPropertyName("discountDueDays")]
    public double? DiscountDueDays { get; set; }

    [JsonPropertyName("netDueDays")]
    public double? NetDueDays { get; set; }
}

public sealed class CreditNoteDetails
{
    [JsonPropertyName("referenceInvoiceNumber")]
    public string? ReferenceInvoiceNumber { get; set; }

    [JsonPropertyName("debitNoteNumber")]
    public string? DebitNoteNumber { get; set; }

    [JsonPropertyName("returnsReferenceNumber")]
    public string? ReturnsReferenceNumber { get; set; }

    [JsonPropertyName("goodsReturnDate")]
    public DateTimeOffset? GoodsReturnDate { get; set; }

    [JsonPropertyName("rmaId")]
    public string? RmaId { get; set; }

    [JsonPropertyName("coopReferenceNumber")]
    public string? CoopReferenceNumber { get; set; }

    [JsonPropertyName("consignorsReferenceNumber")]
    public string? ConsignorsReferenceNumber { get; set; }
}

/// <summary>Response body of a successful submitInvoices call. Acceptance is not confirmation -
/// Amazon processes the document asynchronously and can still reject it later.</summary>
public sealed class TransactionResponse
{
    [JsonPropertyName("payload")]
    public TransactionId? Payload { get; set; }

    [JsonPropertyName("errors")]
    public List<SpApiError>? Errors { get; set; }
}

public sealed class TransactionId
{
    [JsonPropertyName("transactionId")]
    public string? Value { get; set; }
}

public sealed class SpApiError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}

public static class InvoiceTypes
{
    public const string Invoice = "Invoice";
    public const string CreditNote = "CreditNote";
}

public static class TaxTypes
{
    public const string Vat = "VAT";
    public const string DomesticVat = "DomesticVAT";
    public const string Gst = "GST";
}

public static class UnitsOfMeasure
{
    public const string Cases = "Cases";
    public const string Eaches = "Eaches";
}

public static class ChargeTypes
{
    public const string Freight = "Freight";
    public const string Packing = "Packing";
    public const string Duty = "Duty";
    public const string Service = "Service";
    public const string SmallOrder = "SmallOrder";
}

public static class AllowanceTypes
{
    public const string Discount = "Discount";
    public const string DiscountIncentive = "DiscountIncentive";
    public const string Defective = "Defective";
    public const string Promotional = "Promotional";
    public const string UnsaleableMerchandise = "UnsaleableMerchandise";
    public const string Special = "Special";
}

public static class PaymentTermsTypes
{
    public const string Basic = "Basic";
    public const string EndOfMonth = "EndOfMonth";
    public const string FixedDate = "FixedDate";
    public const string Proximo = "Proximo";
    public const string PaymentDueUponReceiptOfInvoice = "PaymentDueUponReceiptOfInvoice";
    public const string LetterOfCredit = "LetterofCredit";
}
