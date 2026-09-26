using Jumbo.AmazonEdi.Core.Models;

namespace Jumbo.AmazonEdi.Core.Configuration;

public sealed class AmazonEdiOptions
{
    public const string SectionName = "AmazonEdi";

    /// <summary>ParallelTest until Amazon has validated at least three invoice files; Live after.</summary>
    public OperatingMode Mode { get; set; } = OperatingMode.ParallelTest;

    /// <summary>When true, nothing is sent until a human approves it in Jumbo Hub.</summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>Omni customer/debtor accounts that represent Amazon.</summary>
    public List<string> AmazonCustomerAccountCodes { get; set; } = new();

    /// <summary>ISO currency we expect on an Amazon invoice. A mismatch is a hard validation failure.</summary>
    public string ExpectedCurrencyCode { get; set; } = "ZAR";

    /// <summary>VAT rates (percent) we accept without complaint. Anything else is held for review.</summary>
    public List<decimal> AllowedTaxRates { get; set; } = new() { 0m, 15m };

    /// <summary>Cases or Eaches, per the Vendor Central agreement. Verify before going live.</summary>
    public string UnitOfMeasure { get; set; } = UnitsOfMeasure.Eaches;

    /// <summary>Us. Amazon pays this party.</summary>
    public PartyOptions RemitToParty { get; set; } = new();

    /// <summary>Amazon's bill-to party, taken verbatim from the EDI Resources page in Vendor Central.
    /// Amazon fails the call outright if this is incomplete or does not match.</summary>
    public PartyOptions BillToParty { get; set; } = new();

    /// <summary>Ship-from party per Omni warehouse code. Optional.</summary>
    public Dictionary<string, PartyOptions> ShipFromPartiesByWarehouse { get; set; } = new();

    /// <summary>Directory that receives a verbatim copy of every request and response.</summary>
    public string PayloadArchiveDirectory { get; set; } = @"C:\JBIntegration\AmazonEdi\Payloads";

    public PollOptions Poll { get; set; } = new();

    /// <summary>Give up automatic retries after this many attempts and hand the invoice to a human.</summary>
    public int MaxAttempts { get; set; } = 5;
}

public enum OperatingMode
{
    /// <summary>Submit via the API and still key the invoice into Vendor Central by hand.</summary>
    ParallelTest,
    Live,
}

public sealed class PartyOptions
{
    /// <summary>Amazon's identifier for the party - our vendor code, or Amazon's bill-to code.</summary>
    public string PartyId { get; set; } = string.Empty;

    public AddressOptions? Address { get; set; }

    /// <summary>VAT number. Required on remit-to for South Africa.</summary>
    public string? VatNumber { get; set; }
}

public sealed class AddressOptions
{
    public string Name { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? AddressLine3 { get; set; }
    public string? City { get; set; }
    public string? StateOrRegion { get; set; }
    public string? PostalOrZipCode { get; set; }
    public string CountryCode { get; set; } = "ZA";
    public string? Phone { get; set; }
}

public sealed class PollOptions
{
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>How far back to look for invoices we have not seen. Bounds a cold start and covers
    /// a backdated capture without rescanning all of history.</summary>
    public int LookbackDays { get; set; } = 7;

    /// <summary>Upper bound on invoices picked up in one run.</summary>
    public int BatchSize { get; set; } = 100;
}
