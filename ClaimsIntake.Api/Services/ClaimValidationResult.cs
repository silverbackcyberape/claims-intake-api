namespace ClaimsIntake.Api.Services;

public sealed record ClaimValidationResult(bool IsValid, IReadOnlyCollection<string> Errors);
