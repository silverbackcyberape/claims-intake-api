namespace ClaimsIntake.Api.Models;

public sealed class ClaimProcessingJob
{
    public Guid Id { get; set; }

    public string ClaimReference { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFilePath { get; set; } = string.Empty;

    public string Status { get; set; } = ClaimProcessingJobStatus.Created;

    public string? ExtractedJson { get; set; }

    public string? ValidationErrors { get; set; }

    public int? ClientApiStatusCode { get; set; }

    public string? ClientApiResponse { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }
}
