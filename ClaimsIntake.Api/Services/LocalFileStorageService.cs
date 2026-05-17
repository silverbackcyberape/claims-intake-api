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
        var storagePath = options.Value.UploadStoragePath;
        var uploadRoot = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.Combine(environment.ContentRootPath, storagePath);

        Directory.CreateDirectory(uploadRoot);

        var extension = Path.GetExtension(file.FileName);
        var storedFileName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}";
        var storedFilePath = Path.Combine(uploadRoot, storedFileName);

        await using var stream = File.Create(storedFilePath);
        await file.CopyToAsync(stream, cancellationToken);

        logger.LogInformation("Stored uploaded claim document at {StoredFilePath}", storedFilePath);
        return storedFilePath;
    }
}
