namespace ClaimsIntake.Api.Models;

public static class ClaimProcessingJobStatus
{
    public const string Created = "Created";
    public const string Processing = "Processing";
    public const string ValidationFailed = "ValidationFailed";
    public const string ClientApiFailed = "ClientApiFailed";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
