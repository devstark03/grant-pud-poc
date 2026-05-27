using System.Globalization;
using PipelineMonitor.Core.Services;

namespace PipelineMonitor.Api.Endpoints;

public static class AnalyticsEndpoints {
    public static void MapAnalyticsEndpoints(this WebApplication app) {
        app.MapGet("/api/analytics/summary", GetSummaryAsync).WithName("AnalyticsSummary");
        app.MapGet("/api/analytics/demand-over-time", GetDemandOverTimeAsync).WithName("DemandOverTime");
        app.MapGet("/api/analytics/demand-vs-temperature", GetDemandVsTemperatureAsync).WithName("DemandVsTemperature");
        app.MapGet("/api/analytics/weekend-vs-weekday", GetWeekendVsWeekdayAsync).WithName("WeekendVsWeekday");
    }

    private static async Task<IResult> GetSummaryAsync(
        IDatabricksSqlClient client,
        ILogger<AnalyticsEndpointsLog> logger,
        CancellationToken cancellationToken) {
        const string sql = @"
            SELECT
                COUNT(*) AS total_days,
                ROUND(AVG(demand_total_mwh), 1) AS avg_daily_demand,
                ROUND(MAX(demand_peak_mwh), 1) AS max_peak_demand,
                ROUND(AVG(temperature_max_c), 1) AS avg_max_temp,
                MIN(d.full_date) AS min_date,
                MAX(d.full_date) AS max_date
            FROM grantpud.gold.fact_daily_load_weather f
            JOIN grantpud.gold.dim_date d ON f.date_key = d.date_key
        ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        if (result.Rows.Count == 0) {
            return Results.Ok(new { });
        }

        var row = result.Rows[0];
        return Results.Ok(new {
            totalDays = ParseInt(row[0]),
            avgDailyDemandMwh = ParseDouble(row[1]),
            maxPeakDemandMwh = ParseDouble(row[2]),
            avgMaxTempC = ParseDouble(row[3]),
            minDate = row[4],
            maxDate = row[5],
        });
    }

    private static async Task<IResult> GetDemandOverTimeAsync(
        IDatabricksSqlClient client,
        ILogger<AnalyticsEndpointsLog> logger,
        CancellationToken cancellationToken) {
        const string sql = @"
            SELECT
                d.full_date,
                f.demand_total_mwh,
                f.demand_peak_mwh,
                f.demand_forecast_total_mwh
            FROM grantpud.gold.fact_daily_load_weather f
            JOIN grantpud.gold.dim_date d ON f.date_key = d.date_key
            ORDER BY d.full_date
        ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        var data = result.Rows.Select(r => new {
            date = r[0],
            demandTotalMwh = ParseDouble(r[1]),
            demandPeakMwh = ParseDouble(r[2]),
            demandForecastMwh = ParseDouble(r[3]),
        });

        return Results.Ok(data);
    }

    private static async Task<IResult> GetDemandVsTemperatureAsync(
        IDatabricksSqlClient client,
        ILogger<AnalyticsEndpointsLog> logger,
        CancellationToken cancellationToken) {
        const string sql = @"
            SELECT
                f.temperature_max_c,
                f.demand_peak_mwh,
                d.full_date
            FROM grantpud.gold.fact_daily_load_weather f
            JOIN grantpud.gold.dim_date d ON f.date_key = d.date_key
            WHERE f.temperature_max_c IS NOT NULL
              AND f.demand_peak_mwh IS NOT NULL
        ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        var data = result.Rows.Select(r => new {
            temperatureMaxC = ParseDouble(r[0]),
            demandPeakMwh = ParseDouble(r[1]),
            date = r[2],
        });

        return Results.Ok(data);
    }

    private static async Task<IResult> GetWeekendVsWeekdayAsync(
        IDatabricksSqlClient client,
        ILogger<AnalyticsEndpointsLog> logger,
        CancellationToken cancellationToken) {
        const string sql = @"
            SELECT
                CASE WHEN d.is_weekend THEN 'Weekend' ELSE 'Weekday' END AS day_type,
                ROUND(AVG(f.demand_total_mwh), 1) AS avg_demand,
                ROUND(AVG(f.demand_peak_mwh), 1) AS avg_peak,
                COUNT(*) AS day_count
            FROM grantpud.gold.fact_daily_load_weather f
            JOIN grantpud.gold.dim_date d ON f.date_key = d.date_key
            GROUP BY d.is_weekend
            ORDER BY day_type
        ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        var data = result.Rows.Select(r => new {
            dayType = r[0],
            avgDemand = ParseDouble(r[1]),
            avgPeak = ParseDouble(r[2]),
            dayCount = ParseInt(r[3]),
        });

        return Results.Ok(data);
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public partial class AnalyticsEndpointsLog { }