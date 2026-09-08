using Jumbo.AmazonEdi.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAmazonEdiPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));

        // A factory rather than a scoped context: the repository is a singleton and Hangfire runs
        // jobs on background threads, where a shared scoped context would throw.
        services.AddDbContextFactory<AmazonEdiDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<PersistenceOptions>>().Value;

            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException("AmazonEdi:Persistence:ConnectionString is not configured.");
            }

            builder.UseSqlServer(options.ConnectionString, sqlServer =>
            {
                sqlServer.CommandTimeout(options.CommandTimeoutSeconds);
                sqlServer.MigrationsHistoryTable("__EFMigrationsHistory", AmazonEdiDbContext.SchemaName);
                sqlServer.EnableRetryOnFailure();
            });
        });

        services.AddSingleton<IAmazonInvoiceRepository, EfAmazonInvoiceRepository>();
        services.AddSingleton<DatabaseMigrator>();

        return services;
    }

    /// <summary>Call once during startup, before the Hangfire job is scheduled.</summary>
    public static Task EnsureAmazonEdiDatabaseAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<DatabaseMigrator>().EnsureUpToDateAsync(cancellationToken);
}
