using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Jumbo.AmazonEdi.Persistence;

/// <summary>Used only by the EF Core tooling (dotnet ef migrations add / script). The connection
/// string here is never used to talk to a real database - migrations are generated from the model,
/// not from the schema - so a placeholder is correct and deliberate.</summary>
public sealed class AmazonEdiDbContextFactory : IDesignTimeDbContextFactory<AmazonEdiDbContext>
{
    public AmazonEdiDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AmazonEdiDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=JumboAmazonEdi_DesignTime;Trusted_Connection=True")
            .Options;

        return new AmazonEdiDbContext(options);
    }
}
