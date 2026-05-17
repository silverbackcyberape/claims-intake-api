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
            ? "gpt-4.1-mini"
            : openAiOptions.OpenAiModel;

        logger.LogInformation(
            "Extracting claim data from {StoredFilePath} using OpenAI model {OpenAiModel}.",
            storedFilePath,
            model);

        try
        {
            var responsesClient = new ResponsesClient(openAiOptions.OpenAiApiKey);
            var inputParts = await BuildInputPartsAsync(
                openAiOptions.OpenAiApiKey,
                storedFilePath,
                claimReference,
                metadataJson,
                cancellationToken);

            var response = await responsesClient.CreateResponseAsync(
                model,
                [ResponseItem.CreateUserMessageItem(inputParts)],
                cancellationToken: cancellationToken);

            var outputText = response.Value.GetOutputText();
            var extractedJson = NormalizeStrictJson(outputText);

            logger.LogInformation("OpenAI extraction completed for claim {ClaimReference}.", claimReference);
            return extractedJson;
        }
        catch (ClientResultException ex)
        {
            logger.LogError(
                ex,
                "OpenAI API request failed while extracting claim {ClaimReference}. Status: {Status}",
                claimReference,
                ex.Status);
            throw new InvalidOperationException("OpenAI extraction failed. See logs for details.", ex);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "OpenAI returned non-JSON output for claim {ClaimReference}.", claimReference);
            throw new InvalidOperationException("OpenAI extraction did not return valid JSON.", ex);
        }
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
            var fileBytes = await File.ReadAllBytesAsync(storedFilePath, cancellationToken);
            return
            [
                ResponseContentPart.CreateInputTextPart(prompt),
                ResponseContentPart.CreateInputImagePart(BinaryData.FromBytes(fileBytes), contentType)
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
}
