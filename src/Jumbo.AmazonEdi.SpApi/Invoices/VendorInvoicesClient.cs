using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jumbo.AmazonEdi.Core.Abstractions;
using Jumbo.AmazonEdi.Core.Models;
using Jumbo.AmazonEdi.SpApi.Auth;
using Jumbo.AmazonEdi.SpApi.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.SpApi.Invoices;

/// <summary>POST /vendor/payments/v1/invoices. Returns a result rather than throwing: the caller
/// has to archive the request and response either way, including when the call failed.</summary>
public sealed class VendorInvoicesClient : IVendorInvoicesClient
{
    private const string Path = "/vendor/payments/v1/invoices";

    private readonly HttpClient _httpClient;
    private readonly ILwaTokenClient _tokenClient;
    private readonly SpApiOptions _options;
    private readonly ILogger<VendorInvoicesClient> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);

    private DateTimeOffset _nextRequestNotBefore = DateTimeOffset.MinValue;

    public VendorInvoicesClient(
        HttpClient httpClient,
        ILwaTokenClient tokenClient,
        IOptions<SpApiOptions> options,
        ILogger<VendorInvoicesClient> logger)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SubmitInvoicesResult> SubmitAsync(IReadOnlyList<Invoice> invoices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        if (invoices.Count == 0)
        {
            throw new ArgumentException("No invoices to submit.", nameof(invoices));
        }

        var body = new SubmitInvoicesRequest { Invoices = invoices.ToList() };
        var requestJson = AmazonJson.Serialize(body);

        SubmitInvoicesResult? lastResult = null;

        for (var attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            lastResult = await SendOnceAsync(requestJson, cancellationToken).ConfigureAwait(false);

            if (lastResult.Succeeded || !lastResult.IsRetryable)
            {
                return lastResult;
            }

            if (attempt < _options.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(
                    "submitInvoices attempt {Attempt}/{Max} failed with HTTP {Status}; retrying in {Delay}s.",
                    attempt, _options.MaxRetries, lastResult.StatusCode, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return lastResult!;
    }

    private async Task<SubmitInvoicesResult> SendOnceAsync(string requestJson, CancellationToken cancellationToken)
    {
        try
        {
            var accessToken = await _tokenClient.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            await WaitForRateLimitSlotAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + Path)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("x-amz-access-token", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new SubmitInvoicesResult
                {
                    Succeeded = false,
                    StatusCode = statusCode,
                    RequestJson = requestJson,
                    ResponseJson = responseJson,
                    ErrorMessage = DescribeErrors(responseJson) ?? $"HTTP {statusCode}.",
                    IsRetryable = IsRetryable(response.StatusCode),
                };
            }

            var transactionId = ReadTransactionId(responseJson);

            if (string.IsNullOrWhiteSpace(transactionId))
            {
                // A 2xx with no transaction id is not a success we can prove later. Treat it as a
                // failure needing a human rather than silently marking the invoice sent.
                return new SubmitInvoicesResult
                {
                    Succeeded = false,
                    StatusCode = statusCode,
                    RequestJson = requestJson,
                    ResponseJson = responseJson,
                    ErrorMessage = "Amazon returned success but no transactionId. Check Vendor Central before resending.",
                    IsRetryable = false,
                };
            }

            return new SubmitInvoicesResult
            {
                Succeeded = true,
                TransactionId = transactionId,
                StatusCode = statusCode,
                RequestJson = requestJson,
                ResponseJson = responseJson,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The call may or may not have reached Amazon. Retryable, but the caller must never
            // assume a failure here means nothing was submitted.
            return new SubmitInvoicesResult
            {
                Succeeded = false,
                StatusCode = 0,
                RequestJson = requestJson,
                ResponseJson = string.Empty,
                ErrorMessage = $"Transport failure calling submitInvoices: {ex.Message}",
                IsRetryable = true,
            };
        }
    }

    /// <summary>Simple spacing between calls so we stay under the 10 rps limit on submitInvoices.</summary>
    private async Task WaitForRateLimitSlotAsync(CancellationToken cancellationToken)
    {
        var minimumInterval = TimeSpan.FromSeconds(1d / Math.Max(1, _options.MaxRequestsPerSecond));

        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextRequestNotBefore)
            {
                await Task.Delay(_nextRequestNotBefore - now, cancellationToken).ConfigureAwait(false);
            }

            _nextRequestNotBefore = DateTimeOffset.UtcNow.Add(minimumInterval);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static string? ReadTransactionId(string responseJson)
    {
        try
        {
            var response = JsonSerializer.Deserialize<TransactionResponse>(responseJson, AmazonJson.SerializerOptions);
            return response?.Payload?.Value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? DescribeErrors(string responseJson)
    {
        try
        {
            var response = JsonSerializer.Deserialize<TransactionResponse>(responseJson, AmazonJson.SerializerOptions);
            if (response?.Errors is not { Count: > 0 })
            {
                return null;
            }

            return string.Join("; ", response.Errors.Select(error =>
                string.Join(" ", new[] { error.Code, error.Message, error.Details }
                    .Where(part => !string.IsNullOrWhiteSpace(part)))));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
