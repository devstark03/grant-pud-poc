using Microsoft.EntityFrameworkCore;
using PipelineMonitor.Core.Data;
using PipelineMonitor.Core.Events;
using PipelineMonitor.Core.Models;

namespace PipelineMonitor.Core.Services;

public interface IEventProcessor {
    Task<EventProcessingResult> ProcessAsync(
        EventGridEnvelope envelope,
        CancellationToken cancellationToken = default);
}

public enum EventProcessingResult {
    Persisted,
    Duplicate,
    Ignored,
}

public class EventProcessor : IEventProcessor {
    private readonly MonitoringDbContext _db;

    private static readonly HashSet<string> SupportedEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.DataFactory.PipelineRunStarted",
        "Microsoft.DataFactory.PipelineRunSucceeded",
        "Microsoft.DataFactory.PipelineRunFailed",
        "Microsoft.DataFactory.PipelineRunCancelled",
    };

    public EventProcessor(MonitoringDbContext db) {
        _db = db;
    }

    public async Task<EventProcessingResult> ProcessAsync(
        EventGridEnvelope envelope,
        CancellationToken cancellationToken = default) {
        if (!SupportedEventTypes.Contains(envelope.EventType)) {
            return EventProcessingResult.Ignored;
        }

        var existing = await _db.PipelineRuns
            .AnyAsync(r => r.EventGridEventId == envelope.Id, cancellationToken);
        if (existing) {
            return EventProcessingResult.Duplicate;
        }

        var data = envelope.Data;
        var run = new PipelineRun {
            EventGridEventId = envelope.Id,
            PipelineRunId = data.RunId,
            PipelineName = data.PipelineName,
            DataFactoryName = data.FactoryName,
            Status = NormalizeStatus(data.Status, envelope.EventType),
            StartTime = data.StartTime,
            EndTime = data.EndTime,
            DurationSeconds = data.EndTime.HasValue
                ? (int)(data.EndTime.Value - data.StartTime).TotalSeconds
                : null,
            ErrorMessage = data.Message,
            ReceivedAt = DateTime.UtcNow,
        };

        _db.PipelineRuns.Add(run);

        if (run.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase)) {
            _db.Alerts.Add(new Alert {
                PipelineRunId = run.PipelineRunId,
                PipelineName = run.PipelineName,
                Severity = "SEV2",
                Message = $"Pipeline {run.PipelineName} failed at {run.EndTime ?? DateTime.UtcNow:O}. " +
                          $"{run.ErrorMessage ?? "No error message provided."}",
                CreatedAt = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return EventProcessingResult.Persisted;
    }

    private static string NormalizeStatus(string? rawStatus, string eventType) {
        if (!string.IsNullOrWhiteSpace(rawStatus)) {
            return rawStatus;
        }

        return eventType switch {
            "Microsoft.DataFactory.PipelineRunStarted" => "Queued",
            "Microsoft.DataFactory.PipelineRunSucceeded" => "Succeeded",
            "Microsoft.DataFactory.PipelineRunFailed" => "Failed",
            "Microsoft.DataFactory.PipelineRunCancelled" => "Cancelled",
            _ => "Unknown",
        };
    }
}