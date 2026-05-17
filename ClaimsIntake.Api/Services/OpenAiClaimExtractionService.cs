using System.ClientModel;
using System.Text.Json;
using ClaimsIntake.Api.Configuration;
using Microsoft.Extensions.Options;
using OpenAI.Files;
using OpenAI.Responses;

#pragma warning disable OPENAI001

namespace ClaimsIntake.Api.Services;

public sealed class OpenAiClaimExtractionService(
    IOptions<ClaimsIntakeOptions> options,
    ILogger<OpenAiClaimExtractionService> logger) : IClaimExtractionService
{
    public async Task<string> ExtractAsync(
        string storedFilePath,
        string claimReference,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        var openAiOptions = options.Value;

        if (string.IsNullOrWhiteSpace(openAiOptions.OpenAiApiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        if (!File.Exists(storedFilePath))
        {
            throw new FileNotFoundException("Stored claim document was not found.", storedFilePath);
        }

        var model = string.IsNullOrWhiteSpace(openAiOptions.OpenAiModel)
            ? "gpt-4o-mini"
            : openAiOptions.OpenAiModel;

        logger.LogInformation(
            "Extracting claim data from {StoredFilePath} using OpenAI model {OpenAiModel}.",
            storedFilePath,
            model);

        return await ExecuteWithRetryAsync(
            async attemptCancellationToken =>
            {
                var responsesClient = new ResponsesClient(openAiOptions.OpenAiApiKey);
                var inputParts = await BuildInputPartsAsync(
                    openAiOptions.OpenAiApiKey,
                    storedFilePath,
                    claimReference,
                    metadataJson,
                    attemptCancellationToken);

                var response = await responsesClient.CreateResponseAsync(
                    model,
                    [ResponseItem.CreateUserMessageItem(inputParts)],
                    cancellationToken: attemptCancellationToken);

                var outputText = response.Value.GetOutputText();
                var extractedJson = NormalizeStrictJson(outputText);

                logger.LogInformation("OpenAI extraction completed for claim {ClaimReference}.", claimReference);
                return extractedJson;
            },
            claimReference,
            openAiOptions.OpenAiTimeoutSeconds,
            openAiOptions.OpenAiMaxRetries,
            cancellationToken);
    }

    private async Task<string> ExecuteWithRetryAsync(
        Func<CancellationToken, Task<string>> operation,
        string claimReference,
        int timeoutSeconds,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, maxRetries + 1);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 300));

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                return await operation(timeoutCts.Token);
            }
            catch (ClientResultException ex) when (ShouldRetryOpenAi(ex.Status) && attempt < attempts)
            {
                logger.LogWarning(
                    ex,
                    "OpenAI extraction attempt {Attempt}/{Attempts} failed for claim {ClaimReference}. Status: {Status}. Retrying.",
                    attempt,
                    attempts,
                    claimReference,
                    ex.Status);

                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
            catch (ClientResultException ex)
            {
                var message = $"OpenAI extraction failed. Status: {ex.Status}. {SanitizeOpenAiErrorMessage(ex.Message)}";
                logger.LogError(
                    ex,
                    "OpenAI API request failed while extracting claim {ClaimReference}. Status: {Status}.",
                    claimReference,
                    ex.Status);
                throw new InvalidOperationException(message, ex);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested && attempt < attempts)
            {
                logger.LogWarning(
                    ex,
                    "OpenAI extraction attempt {Attempt}/{Attempts} timed out for claim {ClaimReference}. Retrying.",
                    attempt,
                    attempts,
                    claimReference);

                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                logger.LogError(ex, "OpenAI extraction timed out for claim {ClaimReference}.", claimReference);
                throw new TimeoutException($"OpenAI extraction timed out after {timeout.TotalSeconds:N0} seconds.", ex);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "OpenAI returned non-JSON output for claim {ClaimReference}.", claimReference);
                throw new InvalidOperationException("OpenAI extraction did not return valid JSON.", ex);
            }
        }

        throw new InvalidOperationException("OpenAI extraction failed after retry attempts.");
    }

    private static async Task<IList<ResponseContentPart>> BuildInputPartsAsync(
        string apiKey,
        string storedFilePath,
        string claimReference,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        var contentType = GetContentType(storedFilePath);
        var prompt = BuildExtractionPrompt(claimReference, metadataJson);

        if (IsSupportedImageContentType(contentType))
        {
            var fileClient = new OpenAIFileClient(apiKey);
            await using var stream = File.OpenRead(storedFilePath);
            var file = await fileClient.UploadFileAsync(
                stream,
                Path.GetFileName(storedFilePath),
                FileUploadPurpose.Vision,
                cancellationToken);

            return
            [
                ResponseContentPart.CreateInputTextPart(prompt),
                ResponseContentPart.CreateInputImagePart(file.Value.Id)
            ];
        }

        if (contentType == "application/pdf")
        {
            // TODO: Add page-level PDF OCR preprocessing if extraction quality is poor for scanned PDFs.
            // TODO: Consider splitting large PDFs before upload when production file sizes are known.
            var fileClient = new OpenAIFileClient(apiKey);
            await using var stream = File.OpenRead(storedFilePath);
            var file = await fileClient.UploadFileAsync(
                stream,
                Path.GetFileName(storedFilePath),
                FileUploadPurpose.UserData,
                cancellationToken);

            return
            [
                ResponseContentPart.CreateInputTextPart(prompt),
                ResponseContentPart.CreateInputFilePart(file.Value.Id)
            ];
        }

        throw new NotSupportedException(
            $"Unsupported claim document type '{contentType}'. Upload an image or PDF file.");
    }

    private static string BuildExtractionPrompt(string claimReference, string? metadataJson)
    {
        return $$"""
            You are extracting structured data from an insurance claim form.

            Return STRICT JSON only. Do not include markdown, comments, prose, code fences, or explanations.

            The JSON must use this exact top-level shape:
            {
              "claimReference": "{{claimReference}}",
              "fields": {
                "claimantName": { "value": null, "confidence": 0.0 },
                "policyNumber": { "value": null, "confidence": 0.0 },
                "dateOfBirth": { "value": null, "confidence": 0.0 },
                "emailAddress": { "value": null, "confidence": 0.0 },
                "phoneNumber": { "value": null, "confidence": 0.0 },
                "incidentType": { "value": null, "confidence": 0.0 },
                "totalClaimAmount": { "value": null, "confidence": 0.0 },
                "currency": { "value": null, "confidence": 0.0 }
              },
              "lineItems": [
                {
                  "serviceDate": { "value": null, "confidence": 0.0 },
                  "providerName": { "value": null, "confidence": 0.0 },
                  "description": { "value": null, "confidence": 0.0 },
                  "amount": { "value": null, "confidence": 0.0 },
                  "paid": { "value": null, "confidence": 0.0 }
                }
              ],
              "missingRequiredFields": [],
              "requiresReview": false
            }

            Rules:
            - Use null for unknown values.
            - Confidence scores must be numbers between 0 and 1.
            - Required fields are claimantName, policyNumber, dateOfBirth, totalClaimAmount.
            - Add any missing required field names to missingRequiredFields.
            - Set requiresReview to true when any required field is missing or any required field confidence is below 0.75.
            - Preserve dates as ISO yyyy-MM-dd where possible.
            - Preserve amounts as numbers where possible.
            - Include only facts visible in the document.

            Claim reference: {{claimReference}}
            Metadata JSON supplied by caller: {{(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson)}}
            """;
    }

    private static string NormalizeStrictJson(string outputText)
    {
        var trimmed = outputText.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);

            if (firstNewLine >= 0 && lastFence > firstNewLine)
            {
                trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        using var document = JsonDocument.Parse(trimmed);
        return JsonSerializer.Serialize(document.RootElement, JsonOptions.Default);
    }

    private static string GetContentType(string storedFilePath)
    {
        return Path.GetExtension(storedFilePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    private static bool IsSupportedImageContentType(string contentType)
    {
        return contentType is "image/jpeg" or "image/png" or "image/webp" or "image/gif";
    }

    private static bool ShouldRetryOpenAi(int status)
    {
        return status is 408 or 429 or >= 500;
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        return TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt)));
    }

    private static string SanitizeOpenAiErrorMessage(string message)
    {
        return message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
