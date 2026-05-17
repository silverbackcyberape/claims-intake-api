namespace ClaimsIntake.Api.Configuration;

public sealed class ClaimsIntakeOptions
{
    public const string SectionName = "ClaimsIntake";

    public string InboundApiKey { get; set; } = string.Empty;

    public string UploadStoragePath { get; set; } = "uploads";

    public string ClientApiEndpoint { get; set; } = string.Empty;

    public string ClientApiKey { get; set; } = string.Empty;

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

    public int OpenAiTimeoutSeconds { get; set; } = 90;

    public int OpenAiMaxRetries { get; set; } = 2;

    public int ClientApiTimeoutSeconds { get; set; } = 30;
}
