using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Jumbo.AmazonEdi.SpApi.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jumbo.AmazonEdi.SpApi.Auth;

public interface ILwaTokenClient
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>Exchanges the long-lived LWA refresh token for a short-lived access token and caches it.
/// SP-API no longer requires AWS SigV4 signing - this bearer token is the whole auth story.</summary>
public sealed class LwaTokenClient : ILwaTokenClient
{
    private readonly HttpClient _httpClient;
    private readonly SpApiOptions _options;
    private readonly ILogger<LwaTokenClient> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly TimeProvider _timeProvider;

    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public LwaTokenClient(
        HttpClient httpClient,
        IOptions<SpApiOptions> options,
        ILogger<LwaTokenClient> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var cached))
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited.
            if (TryGetCachedToken(out cached))
            {
                return cached;
            }

            var token = await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            _accessToken = token.AccessToken;
            _expiresAt = _timeProvider.GetUtcNow().AddSeconds(token.ExpiresIn);

            _logger.LogInformation("Obtained a new LWA access token, valid for {Seconds}s.", token.ExpiresIn);
            return token.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool TryGetCachedToken(out string token)
    {
        var skew = TimeSpan.FromSeconds(_options.Lwa.RefreshSkewSeconds);
        if (_accessToken is not null && _timeProvider.GetUtcNow() < _expiresAt - skew)
        {
            token = _accessToken;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private async Task<LwaTokenResponse> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var lwa = _options.Lwa;
        if (string.IsNullOrWhiteSpace(lwa.ClientId)
            || string.IsNullOrWhiteSpace(lwa.ClientSecret)
            || string.IsNullOrWhiteSpace(lwa.RefreshToken))
        {
            throw new InvalidOperationException(
                "LWA credentials are not configured. Set AmazonEdi:SpApi:Lwa ClientId, ClientSecret and RefreshToken " +
                "via environment variables or user-secrets.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, lwa.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = lwa.RefreshToken,
                ["client_id"] = lwa.ClientId,
                ["client_secret"] = lwa.ClientSecret,
            }),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Never log the body verbatim - a failed token exchange can echo credentials back.
            throw new InvalidOperationException(
                $"LWA token request failed with HTTP {(int)response.StatusCode}. " +
                $"Error code: {ExtractErrorCode(body)}.");
        }

        var token = await response.Content
            .ReadFromJsonAsync<LwaTokenResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return token ?? throw new InvalidOperationException("LWA token response was empty.");
    }

    private static string ExtractErrorCode(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString() ?? "unknown"
                : "unknown";
        }
        catch (System.Text.Json.JsonException)
        {
            return "unparseable";
        }
    }

    private sealed class LwaTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
