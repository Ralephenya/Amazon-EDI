namespace Jumbo.AmazonEdi.SpApi.Configuration;

public sealed class SpApiOptions
{
    public const string SectionName = "AmazonEdi:SpApi";

    /// <summary>Production endpoint. South Africa is served by the EU region - confirm against our
    /// own Vendor Central account before the first live call.</summary>
    public string Endpoint { get; set; } = "https://sellingpartnerapi-eu.amazon.com";

    public string SandboxEndpoint { get; set; } = "https://sandbox.sellingpartnerapi-eu.amazon.com";

    /// <summary>Keep this true until the sandbox round-trip and the dry run are both clean.</summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>South Africa (amazon.co.za). Verify in Vendor Central.</summary>
    public string MarketplaceId { get; set; } = "AE08WJ6YKNBMC";

    public LwaOptions Lwa { get; set; } = new();

    /// <summary>submitInvoices allows 10 requests per second. We stay well under it.</summary>
    public int MaxRequestsPerSecond { get; set; } = 5;

    public int MaxRetries { get; set; } = 3;

    public int TimeoutSeconds { get; set; } = 60;

    public string BaseUrl => UseSandbox ? SandboxEndpoint : Endpoint;
}

public sealed class LwaOptions
{
    public string TokenEndpoint { get; set; } = "https://api.amazon.com/auth/o2/token";

    /// <summary>Secret. Supply via environment or user-secrets, never appsettings in the repo.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Secret.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Secret. Obtained by self-authorizing the private vendor application in Vendor Central.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Refresh this far before the token actually expires.</summary>
    public int RefreshSkewSeconds { get; set; } = 300;
}
