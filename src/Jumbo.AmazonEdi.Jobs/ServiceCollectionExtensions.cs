using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Configuration;
using Jumbo.AmazonEdi.Core.Invoicing;
using Jumbo.AmazonEdi.Omni;
using Jumbo.AmazonEdi.Persistence;
using Jumbo.AmazonEdi.SpApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Jobs;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the whole integration. In Jumbo Hub this plus the Hangfire recurring job is all the
    /// wiring needed:
    ///
    ///   services.AddAmazonEdi(configuration);
    ///   // after the host is built, before scheduling:
    ///   await app.Services.EnsureAmazonEdiDatabaseAsync();
    ///   RecurringJob.AddOrUpdate&lt;AmazonInvoiceSubmissionJob&gt;(
    ///       "amazon-invoices", job => job.RunAsync(CancellationToken.None), "*/15 * * * *");
    /// </summary>
    public static IServiceCollection AddAmazonEdi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AmazonEdiOptions>(configuration.GetSection(AmazonEdiOptions.SectionName));
        services.Configure<OmniOptions>(configuration.GetSection(OmniOptions.SectionName));

        services.AddAmazonSpApi(configuration);
        services.AddAmazonEdiPersistence(configuration);

        services.AddSingleton<IOmniInvoiceSource, OmniInvoiceSource>();
        services.AddSingleton<IPayloadArchive, FilePayloadArchive>();

        services.AddSingleton(provider =>
            new InvoiceBuilder(provider.GetRequiredService<IOptions<AmazonEdiOptions>>().Value));
        services.AddSingleton(provider =>
            new InvoiceValidator(provider.GetRequiredService<IOptions<AmazonEdiOptions>>().Value));

        services.AddScoped<AmazonInvoiceSubmissionJob>();

        return services;
    }
}
