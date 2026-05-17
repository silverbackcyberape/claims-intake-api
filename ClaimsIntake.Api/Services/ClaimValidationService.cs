using System.Text.Json;

namespace ClaimsIntake.Api.Services;

public sealed class ClaimValidationService : IClaimValidationService
{
    public ClaimValidationResult Validate(string extractedJson)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(extractedJson))
        {
            return new ClaimValidationResult(false, ["Extracted JSON is empty."]);
        }

        try
        {
            using var document = JsonDocument.Parse(extractedJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Extracted JSON must be an object.");
            }

            RequireString(root, "claimReference", errors);
            RequireObject(root, "fields", errors);
            RequireArray(root, "missingRequiredFields", errors);
            RequireBoolean(root, "requiresReview", errors);
        }
        catch (JsonException ex)
        {
            errors.Add($"Extracted JSON is invalid: {ex.Message}");
        }

        return new ClaimValidationResult(errors.Count == 0, errors);
    }

    private static void RequireString(JsonElement root, string propertyName, List<string> errors)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            errors.Add($"{propertyName} is required.");
        }
    }

    private static void RequireObject(JsonElement root, string propertyName, List<string> errors)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{propertyName} object is required.");
        }
    }

    private static void RequireArray(JsonElement root, string propertyName, List<string> errors)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{propertyName} array is required.");
        }
    }

    private static void RequireBoolean(JsonElement root, string propertyName, List<string> errors)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors.Add($"{propertyName} boolean is required.");
        }
    }
}
