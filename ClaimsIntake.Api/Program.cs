using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaimsIntake.Api.Configuration;
using ClaimsIntake.Api.Data;
using ClaimsIntake.Api.Models;
using ClaimsIntake.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ClaimsIntakeOptions>(
    builder.Configuration.GetSection(ClaimsIntakeOptions.SectionName));

builder.Services.AddDbContext<ClaimsIntakeDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ClaimsIntake")
        ?? "Data Source=claims-intake.db"));

builder.Services.AddHttpClient<IClientClaimsApiService, ClientClaimsApiService>();
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.AddScoped<IClaimExtractionService, OpenAiClaimExtractionService>();
builder.Services.AddScoped<IClaimValidationService, ClaimValidationService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ClaimsIntakeDbContext>();
    dbContext.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var claims = app.MapGroup("/api/claims");

claims.MapPost("/documents", async (
        HttpRequest httpRequest,
        [FromForm] ClaimDocumentUploadRequest request,
        ClaimsIntakeDbContext dbContext,
        IFileStorageService fileStorageService,
        IClaimExtractionService extractionService,
        IClaimValidationService validationService,
        IClientClaimsApiService clientClaimsApiService,
        IOptions<ClaimsIntakeOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("ClaimsIntake.Documents");

        if (!IsAuthorized(httpRequest, options.Value.InboundApiKey))
        {
            return Results.Unauthorized();
        }

        if (request.File is null || request.File.Length == 0)
        {
            return Results.BadRequest(new { error = "A non-empty file is required." });
        }

        if (string.IsNullOrWhiteSpace(request.ClaimReference))
        {
            return Results.BadRequest(new { error = "claimReference is required." });
        }

        if (!IsValidJsonOrEmpty(request.MetadataJson))
        {
            return Results.BadRequest(new { error = "metadataJson must be valid JSON when supplied." });
        }

        var job = new ClaimProcessingJob
        {
            Id = Guid.NewGuid(),
            ClaimReference = request.ClaimReference.Trim(),
            OriginalFileName = Path.GetFileName(request.File.FileName),
            Status = ClaimProcessingJobStatus.Created,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.ClaimProcessingJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            job.Status = ClaimProcessingJobStatus.Processing;
            job.StoredFilePath = await fileStorageService.SaveAsync(request.File, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            job.ExtractedJson = await extractionService.ExtractAsync(
                job.StoredFilePath,
                job.ClaimReference,
                request.MetadataJson,
                cancellationToken);

            var validationResult = validationService.Validate(job.ExtractedJson);
            if (!validationResult.IsValid)
            {
                job.Status = ClaimProcessingJobStatus.ValidationFailed;
                job.ValidationErrors = JsonSerializer.Serialize(validationResult.Errors, ClaimsIntake.Api.Services.JsonOptions.Default);
                job.ProcessedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);

                return Results.UnprocessableEntity(ToJobResponse(job));
            }

            var clientApiResult = await clientClaimsApiService.PostClaimAsync(
                job.ExtractedJson,
                request.CallbackUrl,
                cancellationToken);

            job.ClientApiStatusCode = clientApiResult.StatusCode;
            job.ClientApiResponse = clientApiResult.ResponseBody;

            if (clientApiResult.IsSuccess)
            {
                job.Status = ClaimProcessingJobStatus.Completed;
            }
            else
            {
                job.Status = ClaimProcessingJobStatus.ClientApiFailed;
                job.ErrorMessage = clientApiResult.ErrorMessage;
            }

            job.ProcessedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);

            return clientApiResult.IsSuccess
                ? Results.Created($"/api/claims/jobs/{job.Id}", ToJobResponse(job))
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process claim document for {ClaimReference}.", job.ClaimReference);

            job.Status = ClaimProcessingJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.ProcessedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);

            return Results.Problem(
                title: "Claim document processing failed.",
                detail: "The claim document was stored or processed unsuccessfully. Check job details for more information.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    })
    .DisableAntiforgery()
    .WithName("UploadClaimDocument")
    .WithOpenApi();

claims.MapGet("/jobs", async (
        HttpRequest httpRequest,
        ClaimsIntakeDbContext dbContext,
        IOptions<ClaimsIntakeOptions> options,
        CancellationToken cancellationToken) =>
    {
        if (!IsAuthorized(httpRequest, options.Value.InboundApiKey))
        {
            return Results.Unauthorized();
        }

        var jobs = await dbContext.ClaimProcessingJobs
            .OrderByDescending(job => job.CreatedAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(jobs.Select(ToJobResponse));
    })
    .WithName("GetClaimProcessingJobs")
    .WithOpenApi();

claims.MapGet("/jobs/{id:guid}", async (
        Guid id,
        HttpRequest httpRequest,
        ClaimsIntakeDbContext dbContext,
        IOptions<ClaimsIntakeOptions> options,
        CancellationToken cancellationToken) =>
    {
        if (!IsAuthorized(httpRequest, options.Value.InboundApiKey))
        {
            return Results.Unauthorized();
        }

        var job = await dbContext.ClaimProcessingJobs.FindAsync([id], cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(ToJobResponse(job));
    })
    .WithName("GetClaimProcessingJob")
    .WithOpenApi();

app.Run();

static bool IsAuthorized(HttpRequest request, string configuredApiKey)
{
    if (string.IsNullOrWhiteSpace(configuredApiKey))
    {
        return false;
    }

    if (!request.Headers.TryGetValue("X-API-Key", out var providedApiKey))
    {
        return false;
    }

    var providedApiKeyBytes = Encoding.UTF8.GetBytes(providedApiKey.ToString());
    var configuredApiKeyBytes = Encoding.UTF8.GetBytes(configuredApiKey);

    return providedApiKeyBytes.Length == configuredApiKeyBytes.Length &&
           CryptographicOperations.FixedTimeEquals(providedApiKeyBytes, configuredApiKeyBytes);
}

static bool IsValidJsonOrEmpty(string? json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        return true;
    }

    try
    {
        using var _ = JsonDocument.Parse(json);
        return true;
    }
    catch (JsonException)
    {
        return false;
    }
}

static object ToJobResponse(ClaimProcessingJob job) => new
{
    job.Id,
    job.ClaimReference,
    job.OriginalFileName,
    job.StoredFilePath,
    job.Status,
    job.ExtractedJson,
    job.ValidationErrors,
    job.ClientApiStatusCode,
    job.ClientApiResponse,
    job.ErrorMessage,
    job.CreatedAt,
    job.ProcessedAt
};
