namespace Jumbo.AmazonEdi.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "AmazonEdi:Persistence";

    /// <summary>Connection string for the database holding the AmazonEdi schema.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public int CommandTimeoutSeconds { get; set; } = 30;
}
