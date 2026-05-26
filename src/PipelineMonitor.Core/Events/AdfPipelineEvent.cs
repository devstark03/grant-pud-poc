using System.Text.Json.Serialization;

namespace PipelineMonitor.Core.Events;

public class EventGridEnvelope {
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("eventTime")]
    public DateTime EventTime { get; set; }

    [JsonPropertyName("data")]
    public AdfPipelineEventData Data { get; set; } = new();
}

public class AdfPipelineEventData {
    [JsonPropertyName("pipelineName")]
    public string PipelineName { get; set; } = string.Empty;

    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("factoryName")]
    public string FactoryName { get; set; } = string.Empty;

    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public DateTime? EndTime { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class EventGridValidationEvent {
    [JsonPropertyName("validationCode")]
    public string ValidationCode { get; set; } = string.Empty;

    [JsonPropertyName("validationUrl")]
    public string? ValidationUrl { get; set; }
}

public class EventGridValidationResponse {
    [JsonPropertyName("validationResponse")]
    public string ValidationResponse { get; set; } = string.Empty;
}