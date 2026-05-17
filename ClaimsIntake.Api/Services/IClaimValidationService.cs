namespace ClaimsIntake.Api.Services;

public interface IClaimValidationService
{
    ClaimValidationResult Validate(string extractedJson);
}
