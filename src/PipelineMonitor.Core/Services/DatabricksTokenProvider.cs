using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace PipelineMonitor.Api.Services;

public class DatabricksTokenProvider {
    private readonly SecretClient _secretClient;
    private string? _cachedToken;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private const string SecretName = "databricks-pat";

    public DatabricksTokenProvider(SecretClient secretClient) {
        _secretClient = secretClient;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default) {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _cacheExpiresAt) {
            return _cachedToken;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _cacheExpiresAt) {
                return _cachedToken;
            }

            var response = await _secretClient.GetSecretAsync(SecretName, cancellationToken: cancellationToken);
            _cachedToken = response.Value.Value;
            _cacheExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            return _cachedToken;
        }
        finally {
            _semaphore.Release();
        }
    }
}