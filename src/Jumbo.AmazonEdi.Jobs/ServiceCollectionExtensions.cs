using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Configuration;
using Jumbo.AmazonEdi.Core.Invoicing;
using Jumbo.AmazonEdi.Omni;
using Jumbo.AmazonEdi.Persistence;
using Jumbo.AmazonEdi.Persistence.Sql;
using Jumbo.AmazonEdi.SpApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Jobs;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the whole integration. In Jumbo Hub this is the only wiring needed,
    /// alongside the Hangfire RecurringJob registration for AmazonInvoiceSubmissionJob.</summary>
    public static IServiceCollection AddAmazonEdi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AmazonEdiOptions>(configuration.GetSection(AmazonEdiOptions.SectionName));
        services.Configure<OmniOptions>(configuration.GetSection(OmniOptions.SectionName));
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));

        services.AddAmazonSpApi(configuration);

        services.AddSingleton<IOmniInvoiceSource, SqlOmniInvoiceSource>();
        services.AddSingleton<IAmazonInvoiceRepository, SqlAmazonInvoiceRepository>();
        services.AddSingleton<IPayloadArchive, FilePayloadArchive>();

        services.AddSingleton(provider =>
            new InvoiceBuilder(provider.GetRequiredService<IOptions<AmazonEdiOptions>>().Value));
        services.AddSingleton(provider =>
            new InvoiceValidator(provider.GetRequiredService<IOptions<AmazonEdiOptions>>().Value));

        services.AddScoped<AmazonInvoiceSubmissionJob>();

        return services;
    }
}
