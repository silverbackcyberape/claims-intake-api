using ClaimsIntake.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ClaimsIntake.Api.Services;

public sealed class LocalFileStorageService(
    IOptions<ClaimsIntakeOptions> options,
    IWebHostEnvironment environment,
    ILogger<LocalFileStorageService> logger) : IFileStorageService
{
    public async Task<string> SaveAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var uploadRoot = GetUploadRoot(options.Value.UploadStoragePath, environment.ContentRootPath);
        Directory.CreateDirectory(uploadRoot);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var storedFileName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}";
        var storedFilePath = Path.Combine(uploadRoot, storedFileName);

        await using var stream = File.Create(storedFilePath);
        await file.CopyToAsync(stream, cancellationToken);

        logger.LogInformation("Stored uploaded claim document at {StoredFilePath}", storedFilePath);
        return storedFilePath;
    }

    public static string GetUploadRoot(string storagePath, string contentRootPath)
    {
        var path = string.IsNullOrWhiteSpace(storagePath) ? "uploads" : storagePath;
        var uploadRoot = Path.IsPathRooted(path)
            ? path
            : Path.Combine(contentRootPath, path);

        return Path.GetFullPath(uploadRoot);
    }
}
