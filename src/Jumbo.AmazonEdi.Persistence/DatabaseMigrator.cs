using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Persistence;

/// <summary>Applies pending EF Core migrations at startup, or refuses to start when it is not
/// allowed to. Starting up against a schema that is missing a column fails later, deeper, and with a
/// far worse error than failing here.</summary>
public sealed class DatabaseMigrator
{
    private readonly IDbContextFactory<AmazonEdiDbContext> _contextFactory;
    private readonly PersistenceOptions _options;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(
        IDbContextFactory<AmazonEdiDbContext> contextFactory,
        IOptions<PersistenceOptions> options,
        ILogger<DatabaseMigrator> logger)
    {
        _contextFactory = contextFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureUpToDateAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToList();

        if (pending.Count == 0)
        {
            _logger.LogDebug("The AmazonEdi schema is up to date.");
            return;
        }

        if (!_options.AutoMigrate)
        {
            throw new InvalidOperationException(
                $"The AmazonEdi database has {pending.Count} pending migration(s) and AutoMigrate is off: " +
                $"{string.Join(", ", pending)}. Apply them as a deploy step " +
                "(dotnet ef database update) before starting.");
        }

        _logger.LogInformation(
            "Applying {Count} pending AmazonEdi migration(s): {Migrations}",
            pending.Count, string.Join(", ", pending));

        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("The AmazonEdi schema is up to date.");
    }
}
