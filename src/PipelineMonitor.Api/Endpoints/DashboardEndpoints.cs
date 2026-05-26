using Microsoft.EntityFrameworkCore;
using PipelineMonitor.Core.Data;

namespace PipelineMonitor.Api.Endpoints;

public static class DashboardEndpoints {
    public static void MapDashboardEndpoints(this WebApplication app) {
        app.MapGet("/api/runs", GetRecentRunsAsync)
            .WithName("GetRecentRuns");

        app.MapGet("/api/runs/summary", GetRunsSummaryAsync)
            .WithName("GetRunsSummary");

        app.MapGet("/api/alerts/active", GetActiveAlertsAsync)
            .WithName("GetActiveAlerts");
    }

    private static async Task<IResult> GetRecentRunsAsync(
        MonitoringDbContext db,
        int hours = 24,
        int limit = 100,
        CancellationToken cancellationToken = default) {
        var since = DateTime.UtcNow.AddHours(-Math.Abs(hours));
        var safeLimit = Math.Clamp(limit, 1, 500);

        var runs = await db.PipelineRuns
            .Where(r => r.StartTime >= since)
            .OrderByDescending(r => r.StartTime)
            .Take(safeLimit)
            .Select(r => new {
                r.PipelineRunId,
                r.PipelineName,
                r.DataFactoryName,
                r.Status,
                r.StartTime,
                r.EndTime,
                r.DurationSeconds,
                r.ErrorMessage,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(runs);
    }

    private static async Task<IResult> GetRunsSummaryAsync(
        MonitoringDbContext db,
        int hours = 24,
        CancellationToken cancellationToken = default) {
        var since = DateTime.UtcNow.AddHours(-Math.Abs(hours));

        var runs = await db.PipelineRuns
            .Where(r => r.StartTime >= since)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var totalCount = runs.Sum(r => r.Count);
        var failedCount = runs.FirstOrDefault(r => r.Status == "Failed")?.Count ?? 0;
        var succeededCount = runs.FirstOrDefault(r => r.Status == "Succeeded")?.Count ?? 0;

        var avgDuration = await db.PipelineRuns
            .Where(r => r.StartTime >= since && r.DurationSeconds.HasValue)
            .AverageAsync(r => (double?)r.DurationSeconds, cancellationToken);

        return Results.Ok(new {
            WindowHours = hours,
            TotalRuns = totalCount,
            SucceededRuns = succeededCount,
            FailedRuns = failedCount,
            SuccessRate = totalCount > 0
                ? Math.Round((double)succeededCount / totalCount * 100.0, 1)
                : (double?)null,
            AverageDurationSeconds = avgDuration.HasValue
                ? Math.Round(avgDuration.Value, 1)
                : (double?)null,
            ByStatus = runs,
        });
    }

    private static async Task<IResult> GetActiveAlertsAsync(
        MonitoringDbContext db,
        CancellationToken cancellationToken = default) {
        var alerts = await db.Alerts
            .Where(a => a.AcknowledgedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new {
                a.Id,
                a.PipelineRunId,
                a.PipelineName,
                a.Severity,
                a.Message,
                a.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(alerts);
    }
}