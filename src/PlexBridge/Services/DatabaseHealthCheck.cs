using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PlexBridge.Data;

namespace PlexBridge.Services;

public sealed class DatabaseHealthCheck(IDbContextFactory<ApplicationDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("database unavailable");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("database unavailable");
        }
    }
}
