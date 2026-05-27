using System.Net.Http.Headers;

namespace PipelineMonitor.Api.Services;

public class DatabricksAuthHandler : DelegatingHandler {
    private readonly DatabricksTokenProvider _tokenProvider;

    public DatabricksAuthHandler(DatabricksTokenProvider tokenProvider) {
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) {
        var token = await _tokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}