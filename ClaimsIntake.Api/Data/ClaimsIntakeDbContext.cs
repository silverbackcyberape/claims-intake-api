using ClaimsIntake.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaimsIntake.Api.Data;

public sealed class ClaimsIntakeDbContext(DbContextOptions<ClaimsIntakeDbContext> options) : DbContext(options)
{
    public DbSet<ClaimProcessingJob> ClaimProcessingJobs => Set<ClaimProcessingJob>();
}
