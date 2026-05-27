namespace PipelineMonitor.Core.Services;

public interface IDatabricksSqlClient {
    Task<DatabricksQueryResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default);
}

public record DatabricksQueryResult(
    IReadOnlyList<string> ColumnNames,
    IReadOnlyList<IReadOnlyList<string?>> Rows);