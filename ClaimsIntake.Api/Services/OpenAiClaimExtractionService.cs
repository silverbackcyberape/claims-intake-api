using System.Text.Json;
using ClaimsIntake.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ClaimsIntake.Api.Services;

public sealed class OpenAiClaimExtractionService(
    IOptions<ClaimsIntakeOptions> options,
    ILogger<OpenAiClaimExtractionService> logger) : IClaimExtractionService
{
    public Task<string> ExtractAsync(
        string storedFilePath,
        string claimReference,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Mock extracting claim data for {ClaimReference} using configured model {OpenAiModel}",
            claimReference,
            options.Value.OpenAiModel);

        var extracted = new
        {
            claimReference,
            sourceDocument = Path.GetFileName(storedFilePath),
            extractedAt = DateTimeOffset.UtcNow,
            claimant = new
            {
                name = "Mock Claimant",
                policyNumber = "POL-MOCK-001"
            },
            incident = new
            {
                date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                type = "Property Damage",
                description = "Mocked extraction result for MVP workflow testing."
            },
            metadata = TryParseMetadata(metadataJson)
        };

        return Task.FromResult(JsonSerializer.Serialize(extracted, JsonOptions.Default));
    }

    private static JsonElement? TryParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
