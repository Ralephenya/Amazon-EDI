namespace Jumbo.AmazonEdi.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = "AmazonEdi:Persistence";

    /// <summary>Connection string for the database holding the AmazonEdi schema.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Apply pending EF Core migrations at startup. Fine while Jumbo Hub runs as a single instance.
    /// Turn this off if it is ever scaled out - two instances migrating the same database at once is
    /// a real hazard - and run migrations as a deploy step instead. When off, startup fails fast if
    /// migrations are pending rather than running against a stale schema.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 30;
}
