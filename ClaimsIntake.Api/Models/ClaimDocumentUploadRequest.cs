using System.ComponentModel;

namespace ClaimsIntake.Api.Models;

public sealed class ClaimDocumentUploadRequest
{
    public IFormFile File { get; set; } = default!;

    public string ClaimReference { get; set; } = string.Empty;

    [DefaultValue("{}")]
    public string? MetadataJson { get; set; }

    [DefaultValue("")]
    public string? CallbackUrl { get; set; }
}
