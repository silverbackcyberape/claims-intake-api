namespace ClaimsIntake.Api.Services;

public sealed record ClientClaimsApiResult(bool IsSuccess, int? StatusCode, string? ResponseBody, string? ErrorMessage);
