using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PipelineMonitor.Api;
using PipelineMonitor.Api.Endpoints;
using PipelineMonitor.Api.Services;
using PipelineMonitor.Core.Data;
using PipelineMonitor.Core.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MonitoringDbContext>((serviceProvider, options) => {
    var config = serviceProvider.GetRequiredService<IConfiguration>();
    var serverName = config["Database:ServerName"]
        ?? throw new InvalidOperationException("Database:ServerName not configured.");
    var databaseName = config["Database:DatabaseName"]
        ?? throw new InvalidOperationException("Database:DatabaseName not configured.");

    var connectionString = new SqlConnectionStringBuilder {
        DataSource = serverName,
        InitialCatalog = databaseName,
        Encrypt = true,
        TrustServerCertificate = false,
        ConnectTimeout = 30,
    }.ConnectionString;

    var credential = new DefaultAzureCredential();

    options.UseSqlServer(connectionString, sqlOptions => {
        sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorNumbersToAdd: null);
    });

    options.AddInterceptors(new AadAuthenticationInterceptor(credential));
});

builder.Services.AddScoped<IEventProcessor, EventProcessor>();

// Key Vault for Databricks PAT
var keyVaultUri = builder.Configuration["KeyVault:Uri"]
    ?? throw new InvalidOperationException("KeyVault:Uri not configured.");
builder.Services.AddSingleton(new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential()));
builder.Services.AddSingleton<DatabricksTokenProvider>();
builder.Services.AddTransient<DatabricksAuthHandler>();

// Databricks SQL client config
var databricksConfig = new DatabricksConfig {
    WorkspaceUrl = builder.Configuration["Databricks:WorkspaceUrl"]
        ?? throw new InvalidOperationException("Databricks:WorkspaceUrl not configured."),
    WarehouseId = builder.Configuration["Databricks:WarehouseId"]
        ?? throw new InvalidOperationException("Databricks:WarehouseId not configured."),
};
builder.Services.AddSingleton(databricksConfig);

// HttpClient with auth handler
builder.Services.AddHttpClient<IDatabricksSqlClient, DatabricksSqlClient>(client => {
    client.BaseAddress = new Uri(databricksConfig.WorkspaceUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
})
.AddHttpMessageHandler<DatabricksAuthHandler>();

builder.Services.AddCors(options => {
    options.AddDefaultPolicy(policy => {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();

app.MapDashboardEndpoints();
app.MapNotifyEndpoints();
app.MapAnalyticsEndpoints();

app.Run();

public partial class Program { }