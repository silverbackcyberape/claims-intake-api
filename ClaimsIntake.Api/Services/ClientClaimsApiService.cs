using System.Net.Http.Headers;
using System.Text;
using ClaimsIntake.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ClaimsIntake.Api.Services;

public sealed class ClientClaimsApiService(
    HttpClient httpClient,
    IOptions<ClaimsIntakeOptions> options,
    ILogger<ClientClaimsApiService> logger) : IClientClaimsApiService
{
    public async Task<ClientClaimsApiResult> PostClaimAsync(
        string extractedJson,
        string? callbackUrl,
        CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(callbackUrl)
            ? options.Value.ClientApiEndpoint
            : callbackUrl;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ClientClaimsApiResult(false, null, null, "Client API endpoint is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(extractedJson, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(options.Value.ClientApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ClientApiKey);
            request.Headers.Add("X-API-Key", options.Value.ClientApiKey);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogInformation(
                "Posted extracted claim JSON to client API. StatusCode: {StatusCode}",
                (int)response.StatusCode);

            return new ClientClaimsApiResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                responseBody,
                response.IsSuccessStatusCode ? null : "Client API returned a non-success status code.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "Failed to post extracted claim JSON to client API.");
            return new ClientClaimsApiResult(false, null, null, ex.Message);
        }
    }
}
