using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaimsIntake.Api.Configuration;
using ClaimsIntake.Api.Data;
using ClaimsIntake.Api.Models;
using ClaimsIntake.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var startupOptions = builder.Configuration
    .GetSection(ClaimsIntakeOptions.SectionName)
    .Get<ClaimsIntakeOptions>() ?? new ClaimsIntakeOptions();

builder.Services.Configure<ClaimsIntakeOptions>(
    builder.Configuration.GetSection(ClaimsIntakeOptions.SectionName));

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = startupOptions.MaxUploadBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = startupOptions.MaxUploadBytes;
});

builder.Services.AddDbContext<ClaimsIntakeDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("ClaimsIntake")
        ?? "Data Source=claims-intake.db"));

builder.Services.AddHttpClient<IClientClaimsApiService, ClientClaimsApiService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(startupOptions.ClientApiTimeoutSeconds, 5, 120));
});
builder.Services.AddScoped<IFileStorageService, LocalFileStorageService>();
builder.Services.AddScoped<IClaimExtractionService, OpenAiClaimExtractionService>();
builder.Services.AddScoped<IClaimValidationService, ClaimValidationService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API key required in the X-API-Key header.",
        Name = "X-API-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "ApiKeyScheme"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                },
                In = ParameterLocation.Header
            },
            []
        }
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var startupLogger = scope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("ClaimsIntake.Startup");
    var dbContext = scope.ServiceProvider.GetRequiredService<ClaimsIntakeDbContext>();
    var configuredOptions = scope.ServiceProvider.GetRequiredService<IOptions<ClaimsIntakeOptions>>().Value;

    try
    {
        dbContext.Database.EnsureCreated();

        var uploadRoot = LocalFileStorageService.GetUploadRoot(
            configuredOptions.UploadStoragePath,
            app.Environment.ContentRootPath);
        Directory.CreateDirectory(uploadRoot);

        startupLogger.LogInformation(
            "ClaimsIntake startup checks completed. UploadRoot: {UploadRoot}",
            uploadRoot);
    }
    catch (Exception ex)
    {
        startupLogger.LogCritical(ex, "ClaimsIntake startup checks failed.");
        throw;
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    app.MapPost("/api/dev/client-claims", (
            JsonElement claimJson,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("ClaimsIntake.DevClient");
            logger.LogInformation("Development client API received extracted claim JSON: {ClaimJson}", claimJson);

            return Results.Accepted(value: new
            {
                status = "received",
                receivedAt = DateTimeOffset.UtcNow
            });
        })
        .WithName("DevelopmentClientClaimsApi")
        .WithOpenApi();
}

app.Use(async (context, next) =>
{
    const string correlationHeaderName = "X-Correlation-ID";
    var correlationId = context.Request.Headers.TryGetValue(correlationHeaderName, out var incomingCorrelationId) &&
                        !string.IsNullOrWhiteSpace(incomingCorrelationId)
        ? incomingCorrelationId.ToString()
        : Guid.NewGuid().ToString("N");

    context.TraceIdentifier = correlationId;
    context.Response.Headers[correlationHeaderName] = correlationId;

    var logger = context.RequestServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("ClaimsIntake.Requests");
    var stopwatch = Stopwatch.StartNew();

    using (logger.BeginScope(new Dictionary<string, object>
           {
               ["CorrelationId"] = correlationId
           }))
    {
        try
        {
            await next();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Unhandled exception for {Method} {Path}.",
                context.Request.Method,
                context.Request.Path.Value);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms.",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
});

app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new
    {
        status = "Healthy",
        checkedAt = DateTimeOffset.UtcNow
    }))
    .AllowAnonymous()
    .WithName("Health")
    .WithOpenApi();

var claims = app.MapGroup("/api/claims");

claims.MapPost("/documents", async (
        HttpRequest httpRequest,
        [FromForm] ClaimDocumentUploadRequest request,
        ClaimsIntakeDbContext dbContext,
        IWebHostEnvironment environment,
        IFileStorageService fileStorageService,
        IClaimExtractionService extractionService,
        IClaimValidationService validationService,
        IClientClaimsApiService clientClaimsApiService,
        IOptions<ClaimsIntakeOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        var logger = loggerFactory.CreateLogger("ClaimsIntake.Documents");
        var configuredOptions = options.Value;

        if (!IsAuthorized(httpRequest, configuredOptions.InboundApiKey))
        {
            return Results.Unauthorized();
        }

        if (request.File is null || request.File.Length == 0)
        {
            return Results.BadRequest(new { error = "A non-empty file is required." });
        }

        if (request.File.Length > configuredOptions.MaxUploadBytes)
        {
            return Results.Problem(
                title: "Uploaded file is too large.",
                detail: $"Maximum upload size is {configuredOptions.MaxUploadBytes} bytes.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (!IsAllowedUploadExtension(request.File.FileName))
        {
            return Results.BadRequest(new
            {
                error = "Unsupported file type. Upload a .jpg, .jpeg, .png, .webp, .gif, or .pdf file."
            });
        }

        if (string.IsNullOrWhiteSpace(request.ClaimReference))
        {
            return Results.BadRequest(new { error = "claimReference is required." });
        }

        var metadataJson = NormalizeOptionalFormValue(
            request.MetadataJson,
            treatSwaggerPlaceholderAsEmpty: environment.IsDevelopment());
        var callbackUrl = NormalizeOptionalFormValue(
            request.CallbackUrl,
            treatSwaggerPlaceholderAsEmpty: environment.IsDevelopment());

        if (!IsValidJsonOrEmpty(metadataJson))
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
            // TODO: Move processing to a durable background queue when uploads need async client acknowledgement.
            job.Status = ClaimProcessingJobStatus.Processing;
            job.StoredFilePath = await fileStorageService.SaveAsync(request.File, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            job.ExtractedJson = await extractionService.ExtractAsync(
                job.StoredFilePath,
                job.ClaimReference,
                metadataJson,
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
                callbackUrl,
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
            logger.LogError(
                ex,
                "Failed to process claim document for {ClaimReference}. JobId: {JobId}.",
                job.ClaimReference,
                job.Id);

            job.Status = ClaimProcessingJobStatus.Failed;
            job.ErrorMessage = GetSafeErrorMessage(ex);
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
            .ToListAsync(cancellationToken);

        return Results.Ok(jobs
            .OrderByDescending(job => job.CreatedAt)
            .Select(ToJobResponse));
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

static string? NormalizeOptionalFormValue(string? value, bool treatSwaggerPlaceholderAsEmpty)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    var trimmed = value.Trim();

    return treatSwaggerPlaceholderAsEmpty &&
           string.Equals(trimmed, "string", StringComparison.OrdinalIgnoreCase)
        ? null
        : trimmed;
}

static bool IsAllowedUploadExtension(string fileName)
{
    return Path.GetExtension(fileName).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".pdf";
}

static string GetSafeErrorMessage(Exception exception)
{
    return exception.Message
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
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
