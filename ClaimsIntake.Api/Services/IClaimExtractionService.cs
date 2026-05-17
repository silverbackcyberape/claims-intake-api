namespace ClaimsIntake.Api.Services;

public interface IClaimExtractionService
{
    Task<string> ExtractAsync(string storedFilePath, string claimReference, string? metadataJson, CancellationToken cancellationToken);
}
