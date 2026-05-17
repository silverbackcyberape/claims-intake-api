namespace ClaimsIntake.Api.Models;

public sealed class ClaimDocumentUploadRequest
{
    public IFormFile File { get; set; } = default!;

    public string ClaimReference { get; set; } = string.Empty;

    public string? MetadataJson { get; set; }

    public string? CallbackUrl { get; set; }
}
