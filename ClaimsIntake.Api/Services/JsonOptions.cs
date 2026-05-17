using System.Text.Json;

namespace ClaimsIntake.Api.Services;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}
