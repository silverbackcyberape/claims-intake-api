namespace ClaimsIntake.Api.Configuration;

public sealed class ClaimsIntakeOptions
{
    public const string SectionName = "ClaimsIntake";

    public string InboundApiKey { get; set; } = string.Empty;

    public string UploadStoragePath { get; set; } = "uploads";

    public string ClientApiEndpoint { get; set; } = string.Empty;

    public string ClientApiKey { get; set; } = string.Empty;

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
}
