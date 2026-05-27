using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PipelineMonitor.Core.Services;

public class DatabricksSqlClient : IDatabricksSqlClient {
    private readonly HttpClient _http;
    private readonly DatabricksConfig _config;
    private readonly ILogger<DatabricksSqlClient> _logger;

    private const int PollIntervalMs = 500;
    private const int MaxPollAttempts = 60;

    public DatabricksSqlClient(
        HttpClient http,
        DatabricksConfig config,
        ILogger<DatabricksSqlClient> logger) {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<DatabricksQueryResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default) {
        var submitRequest = new SubmitRequest {
            Statement = sql,
            WarehouseId = _config.WarehouseId,
            WaitTimeout = "30s",
            Format = "JSON_ARRAY",
            Disposition = "INLINE",
        };

        using var submitResponse = await _http.PostAsJsonAsync(
            "/api/2.0/sql/statements",
            submitRequest,
            cancellationToken);

        if (!submitResponse.IsSuccessStatusCode) {
            var errorBody = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Databricks SQL submit failed: {submitResponse.StatusCode}. {errorBody}");
        }

        var result = await submitResponse.Content.ReadFromJsonAsync<StatementResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Databricks returned empty submit response.");

        for (var i = 0; i < MaxPollAttempts; i++) {
            if (result.Status?.State is "SUCCEEDED" or "FAILED" or "CANCELED" or "CLOSED") {
                break;
            }

            await Task.Delay(PollIntervalMs, cancellationToken);

            using var pollResponse = await _http.GetAsync(
                $"/api/2.0/sql/statements/{result.StatementId}",
                cancellationToken);

            if (!pollResponse.IsSuccessStatusCode) {
                throw new InvalidOperationException(
                    $"Databricks SQL poll failed: {pollResponse.StatusCode}");
            }

            result = await pollResponse.Content.ReadFromJsonAsync<StatementResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Databricks returned empty poll response.");
        }

        if (result.Status?.State != "SUCCEEDED") {
            var errorMessage = result.Status?.Error?.Message ?? "Unknown error";
            throw new InvalidOperationException(
                $"Databricks SQL did not succeed: {result.Status?.State}. {errorMessage}");
        }

        var columns = result.Manifest?.Schema?.Columns?
            .Select(c => c.Name)
            .ToList() ?? new List<string>();

        var rows = new List<IReadOnlyList<string?>>();
        if (result.Result?.DataArray != null) {
            foreach (var rowElement in result.Result.DataArray) {
                var row = new List<string?>();
                foreach (var cell in rowElement) {
                    row.Add(cell.ValueKind == JsonValueKind.Null ? null : cell.ToString());
                }
                rows.Add(row);
            }
        }

        return new DatabricksQueryResult(columns, rows);
    }

    private class SubmitRequest {
        [JsonPropertyName("statement")]
        public string Statement { get; set; } = string.Empty;

        [JsonPropertyName("warehouse_id")]
        public string WarehouseId { get; set; } = string.Empty;

        [JsonPropertyName("wait_timeout")]
        public string WaitTimeout { get; set; } = "30s";

        [JsonPropertyName("format")]
        public string Format { get; set; } = "JSON_ARRAY";

        [JsonPropertyName("disposition")]
        public string Disposition { get; set; } = "INLINE";
    }

    private class StatementResponse {
        [JsonPropertyName("statement_id")]
        public string StatementId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public StatementStatus? Status { get; set; }

        [JsonPropertyName("manifest")]
        public StatementManifest? Manifest { get; set; }

        [JsonPropertyName("result")]
        public StatementResult? Result { get; set; }
    }

    private class StatementStatus {
        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("error")]
        public StatementError? Error { get; set; }
    }

    private class StatementError {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private class StatementManifest {
        [JsonPropertyName("schema")]
        public ManifestSchema? Schema { get; set; }
    }

    private class ManifestSchema {
        [JsonPropertyName("columns")]
        public List<SchemaColumn>? Columns { get; set; }
    }

    private class SchemaColumn {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type_text")]
        public string TypeText { get; set; } = string.Empty;
    }

    private class StatementResult {
        [JsonPropertyName("data_array")]
        public List<List<JsonElement>>? DataArray { get; set; }
    }
}

public class DatabricksConfig {
    public string WorkspaceUrl { get; set; } = string.Empty;
    public string WarehouseId { get; set; } = string.Empty;
}