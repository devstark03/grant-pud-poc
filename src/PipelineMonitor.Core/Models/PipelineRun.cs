namespace PipelineMonitor.Core.Models;

public class PipelineRun {
    public long Id { get; set; }
    public string PipelineRunId { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;
    public string DataFactoryName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int? DurationSeconds { get; set; }
    public string? ErrorMessage { get; set; }
    public string EventGridEventId { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
}