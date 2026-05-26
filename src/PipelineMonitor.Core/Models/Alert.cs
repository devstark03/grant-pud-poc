namespace PipelineMonitor.Core.Models;

public class Alert {
    public long Id { get; set; }
    public string PipelineRunId { get; set; } = string.Empty;
    public string PipelineName { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
}