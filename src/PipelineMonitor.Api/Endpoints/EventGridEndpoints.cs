using System.Text.Json;
using PipelineMonitor.Core.Events;
using PipelineMonitor.Core.Services;

namespace PipelineMonitor.Api.Endpoints;

public static class EventGridEndpoints {
    public static void MapEventGridEndpoints(this WebApplication app) {
        app.MapPost("/webhook/eventgrid", HandleEventGridAsync)
            .WithName("EventGridWebhook");
    }

    private static async Task<IResult> HandleEventGridAsync(
        HttpRequest request,
        IEventProcessor processor,
        ILogger logger,
        CancellationToken cancellationToken) {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(body)) {
            return Results.BadRequest("Empty request body.");
        }

        var jsonOptions = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
        };

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var envelopes = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : new[] { root }.AsEnumerable().GetEnumerator() as IEnumerable<JsonElement> ?? Enumerable.Empty<JsonElement>();

        var rawEvents = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : new[] { root };

        var validationResponses = new List<EventGridValidationResponse>();

        foreach (var element in rawEvents) {
            var eventTypeElement = element.GetProperty("eventType");
            var eventType = eventTypeElement.GetString();

            if (string.Equals(eventType, "Microsoft.EventGrid.SubscriptionValidationEvent", StringComparison.OrdinalIgnoreCase)) {
                var validationData = element.GetProperty("data").Deserialize<EventGridValidationEvent>(jsonOptions);
                if (validationData != null) {
                    validationResponses.Add(new EventGridValidationResponse {
                        ValidationResponse = validationData.ValidationCode,
                    });
                    logger.LogInformation("Handled Event Grid subscription validation handshake.");
                }
                continue;
            }

            var envelope = element.Deserialize<EventGridEnvelope>(jsonOptions);
            if (envelope == null) {
                logger.LogWarning("Failed to deserialize Event Grid envelope; skipping.");
                continue;
            }

            try {
                var result = await processor.ProcessAsync(envelope, cancellationToken);
                logger.LogInformation(
                    "Processed event {EventId} (type {EventType}, pipeline {Pipeline}): {Result}",
                    envelope.Id, envelope.EventType, envelope.Data.PipelineName, result);
            }
            catch (Exception ex) {
                logger.LogError(ex,
                    "Failed to process event {EventId} (type {EventType}).",
                    envelope.Id, envelope.EventType);
                throw;
            }
        }

        if (validationResponses.Count == 1) {
            return Results.Ok(validationResponses[0]);
        }

        return Results.Ok();
    }
}