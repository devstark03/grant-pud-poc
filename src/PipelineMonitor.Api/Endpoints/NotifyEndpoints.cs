using PipelineMonitor.Core.Data;
using PipelineMonitor.Core.Models;

namespace PipelineMonitor.Api.Endpoints;

public static class NotifyEndpoints {
    public static void MapNotifyEndpoints(this WebApplication app) {
        app.MapPost("/api/runs/notify", HandleNotifyAsync)
            .WithName("NotifyPipelineRun");
    }

    public record RunNotification(
        string PipelineRunId,
        string PipelineName,
        string DataFactoryName,
        string Status,
        DateTime StartTime,
        DateTime? EndTime,
        string? ErrorMessage,
        string NotificationId);

    private static async Task<IResult> HandleNotifyAsync(
        RunNotification notification,
        MonitoringDbContext db,
        ILogger<NotifyEndpointsLog> logger,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(notification.NotificationId)) {
            return Results.BadRequest(new { error = "NotificationId is required for idempotency." });
        }

        var existing = db.PipelineRuns
            .Where(r => r.EventGridEventId == notification.NotificationId)
            .Select(r => new { r.Id, r.Status })
            .FirstOrDefault();

        if (existing != null) {
            logger.LogInformation(
                "Duplicate notification {NotificationId} ignored.",
                notification.NotificationId);
            return Results.Ok(new { result = "duplicate" });
        }

        var durationSeconds = notification.EndTime.HasValue
            ? (int?)(notification.EndTime.Value - notification.StartTime).TotalSeconds
            : null;

        var run = new PipelineRun {
            EventGridEventId = notification.NotificationId,
            PipelineRunId = notification.PipelineRunId,
            PipelineName = notification.PipelineName,
            DataFactoryName = notification.DataFactoryName,
            Status = notification.Status,
            StartTime = notification.StartTime,
            EndTime = notification.EndTime,
            DurationSeconds = durationSeconds,
            ErrorMessage = notification.ErrorMessage,
            ReceivedAt = DateTime.UtcNow,
        };

        db.PipelineRuns.Add(run);

        if (notification.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) {
            db.Alerts.Add(new Alert {
                PipelineRunId = notification.PipelineRunId,
                PipelineName = notification.PipelineName,
                Severity = "SEV2",
                Message = $"Pipeline {notification.PipelineName} failed. {notification.ErrorMessage ?? "No error message provided."}",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Recorded notification {NotificationId} for pipeline {Pipeline} status {Status}.",
            notification.NotificationId, notification.PipelineName, notification.Status);

        return Results.Ok(new { result = "persisted" });
    }
}

public partial class NotifyEndpointsLog { }