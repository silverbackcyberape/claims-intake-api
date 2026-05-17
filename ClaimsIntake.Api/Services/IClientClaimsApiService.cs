namespace ClaimsIntake.Api.Services;

public interface IClientClaimsApiService
{
    Task<ClientClaimsApiResult> PostClaimAsync(string extractedJson, string? callbackUrl, CancellationToken cancellationToken);
}
