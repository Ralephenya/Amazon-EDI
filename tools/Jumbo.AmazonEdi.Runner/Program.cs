using Jumbo.AmazonEdi.Jobs;
using Jumbo.AmazonEdi.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Development and dry-run host. Runs the same job Jumbo Hub schedules, once, and exits.
// Keep UseSandbox true until the dry run against real Omni data has been checked line by line.

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.Services.AddAmazonEdi(builder.Configuration);

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();

// Same startup check Jumbo Hub performs: apply pending migrations, or refuse to run.
await host.Services.EnsureAmazonEdiDatabaseAsync();

using var scope = host.Services.CreateScope();
var job = scope.ServiceProvider.GetRequiredService<AmazonInvoiceSubmissionJob>();

try
{
    var summary = await job.RunAsync(CancellationToken.None);

    Console.WriteLine(
        $"Ingested {summary.Ingested}, validated {summary.Validated}, held for review {summary.HeldForReview}, " +
        $"submitted {summary.Submitted}, failed {summary.Failed}.");

    return summary.Failed == 0 ? 0 : 1;
}
catch (Exception ex)
{
    logger.LogError(ex, "The Amazon invoice run failed.");
    return 2;
}

public partial class Program;
