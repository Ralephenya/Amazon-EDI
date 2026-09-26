using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.Persistence;

/// <summary>Writes every request and response to disk, alongside the copy in the database.
/// Archiving must never break a run, so failures here are logged and swallowed - but they are
/// logged loudly, because a silent logging failure is one of the defects we are replacing.</summary>
public sealed class FilePayloadArchive : IPayloadArchive
{
    private readonly AmazonEdiOptions _options;
    private readonly ILogger<FilePayloadArchive> _logger;

    public FilePayloadArchive(IOptions<AmazonEdiOptions> options, ILogger<FilePayloadArchive> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task ArchiveAsync(string invoiceNumber, string kind, string content, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(_options.PayloadArchiveDirectory, DateTime.UtcNow.ToString("yyyyMMdd"));
            Directory.CreateDirectory(directory);

            var fileName = $"{Sanitize(invoiceNumber)}-{DateTime.UtcNow:HHmmssfff}-{Sanitize(kind)}.json";
            await File.WriteAllTextAsync(Path.Combine(directory, fileName), content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(
                ex,
                "Could not archive the {Kind} payload for invoice {InvoiceNumber} to {Directory}. " +
                "The database copy is still authoritative, but fix the archive path.",
                kind, invoiceNumber, _options.PayloadArchiveDirectory);
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
