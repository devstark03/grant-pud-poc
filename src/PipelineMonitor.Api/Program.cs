using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PipelineMonitor.Api;
using PipelineMonitor.Api.Endpoints;
using PipelineMonitor.Core.Data;
using PipelineMonitor.Core.Services;

namespace PipelineMonitor.Api;

public static class Program {
    public static void Main(string[] args) {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDbContext<MonitoringDbContext>((serviceProvider, options) =>
        {
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

            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
            });

            options.AddInterceptors(new AadAuthenticationInterceptor(credential));
        });

        builder.Services.AddScoped<IEventProcessor, EventProcessor>();

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            });
        });

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseCors();

        app.MapNotifyEndpoints();
        app.MapEventGridEndpoints();
        app.MapDashboardEndpoints();

        app.Run();
    }
}