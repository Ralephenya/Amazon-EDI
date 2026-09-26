using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.SpApi.Auth;
using Jumbo.AmazonEdi.SpApi.Configuration;
using Jumbo.AmazonEdi.SpApi.Invoices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.SpApi;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAmazonSpApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SpApiOptions>(configuration.GetSection(SpApiOptions.SectionName));

        services.AddHttpClient<ILwaTokenClient, LwaTokenClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SpApiOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddHttpClient<IVendorInvoicesClient, VendorInvoicesClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<SpApiOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        return services;
    }
}
