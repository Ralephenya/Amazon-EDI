namespace Jumbo.AmazonEdi.Omni;

public sealed class OmniOptions
{
    public const string SectionName = "AmazonEdi:Omni";

    /// <summary>
    /// Connection to the SQL Server that hosts the JOLLYJUMBO linked server - not to Omni itself.
    /// Omni is reached through OPENQUERY inside the stored procedure. Use an account that can execute
    /// the procedure and nothing else.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// The procedure that returns invoices ready to send to Amazon. Two result sets: headers, then
    /// lines. See docs/omni-stored-procedure.md for the full contract.
    /// </summary>
    public string StoredProcedureName { get; set; } = "dbo.usp_AmazonEdi_GetInvoicesToSubmit";

    /// <summary>OPENQUERY against a linked server is not always quick.</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;
}
