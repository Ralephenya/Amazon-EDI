using Microsoft.EntityFrameworkCore;
using Jumbo.AmazonEdi.Persistence;
using Xunit;

namespace Jumbo.AmazonEdi.Tests;

/// <summary>
/// A throwaway SQL Server database for the persistence tests.
///
/// These tests deliberately run against real SQL Server rather than SQLite or the EF InMemory
/// provider. InMemory does not enforce unique indexes, so it would hide a broken idempotency guard -
/// the one thing that stops us invoicing Amazon twice. SQLite enforces that, but cannot order or
/// aggregate a DateTimeOffset, which the invoice date is. Only the real provider exercises what
/// production actually does: the unique index, decimal(19,4) money, and DateTimeOffset ordering.
///
/// Set AMAZONEDI_TEST_SQL to a connection string to run them. CI sets it to a SQL Server service
/// container. When it is unset the tests skip rather than fail, so `dotnet test` still works on a
/// machine with no SQL Server.
/// </summary>
public sealed class SqlServerTestDatabase : IAsyncLifetime
{
    public const string ConnectionStringVariable = "AMAZONEDI_TEST_SQL";

    private readonly string _databaseName = $"AmazonEdiTests_{Guid.NewGuid():N}";

    public static string? BaseConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringVariable);

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(BaseConnectionString);

    public IDbContextFactory<AmazonEdiDbContext> ContextFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(BaseConnectionString)
        {
            InitialCatalog = _databaseName,
        };

        ContextFactory = new TestDbContextFactory(builder.ConnectionString);

        await using var context = await ContextFactory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var context = await ContextFactory.CreateDbContextAsync();
        await context.Database.EnsureDeletedAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AmazonEdiDbContext>
    {
        private readonly string _connectionString;

        public TestDbContextFactory(string connectionString) => _connectionString = connectionString;

        public AmazonEdiDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AmazonEdiDbContext>()
                .UseSqlServer(_connectionString)
                .Options);
    }
}

/// <summary>A fact that skips itself when no test SQL Server is configured, so the suite stays
/// runnable on a machine without one.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerTestDatabase.IsAvailable)
        {
            Skip = $"Set {SqlServerTestDatabase.ConnectionStringVariable} to run the persistence tests against SQL Server.";
        }
    }
}

/// <summary>The <see cref="SqlServerFactAttribute"/> equivalent for data-driven tests.</summary>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (!SqlServerTestDatabase.IsAvailable)
        {
            Skip = $"Set {SqlServerTestDatabase.ConnectionStringVariable} to run the persistence tests against SQL Server.";
        }
    }
}
