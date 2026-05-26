using System.Data.Common;
using Azure.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PipelineMonitor.Api;

public class AadAuthenticationInterceptor : DbConnectionInterceptor {
    private static readonly string[] AzureSqlScopes = new[] { "https://database.windows.net/.default" };
    private readonly TokenCredential _credential;

    public AadAuthenticationInterceptor(TokenCredential credential) {
        _credential = credential;
    }

    public override InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result) {
        if (connection is SqlConnection sqlConnection) {
            var token = _credential.GetToken(
                new TokenRequestContext(AzureSqlScopes),
                CancellationToken.None);
            sqlConnection.AccessToken = token.Token;
        }
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default) {
        if (connection is SqlConnection sqlConnection) {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(AzureSqlScopes),
                cancellationToken);
            sqlConnection.AccessToken = token.Token;
        }
        return await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }
}