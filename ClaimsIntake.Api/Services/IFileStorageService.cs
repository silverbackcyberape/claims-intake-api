namespace ClaimsIntake.Api.Services;

public interface IFileStorageService
{
    Task<string> SaveAsync(IFormFile file, CancellationToken cancellationToken);
}
