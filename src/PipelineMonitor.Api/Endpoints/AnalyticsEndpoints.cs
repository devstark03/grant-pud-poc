using System.Globalization;
using PipelineMonitor.Core.Services;

namespace PipelineMonitor.Api.Endpoints;

public static class AnalyticsEndpoints {
    public static void MapAnalyticsEndpoints(this WebApplication app) {
        app.MapGet("/api/analytics/summary", GetSummaryAsync).WithName("AnalyticsSummary");
        app.MapGet("/api/analytics/demand-over-time", GetDemandOverTimeAsync).WithName("DemandOverTime");
        app.MapGet("/api/analytics/demand-vs-temperature", GetDemandVsTemperatureAsync).WithName("DemandVsTemperature");
        app.MapGet("/api/analytics/weekend-vs-weekday", GetWeekendVsWeekdayAsync).WithName("WeekendVsWeekday");
        app.MapGet("/api/dq/summary", GetDqSummaryAsync).WithName("DqSummary");
        app.MapGet("/api/dq/recent", GetDqRecentAsync).WithName("DqRecent");
        app.MapGet("/api/dq/by-table", GetDqByTableAsync).WithName("DqByTable");
        app.MapGet("/api/dq/pass-rate-trend", GetDqPassRateTrendAsync).WithName("DqPassRateTrend");
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

    private static async Task<IResult> GetDqSummaryAsync(
    IDatabricksSqlClient client,
    ILogger<AnalyticsEndpointsLog> logger,
    int hours = 24,
    CancellationToken cancellationToken = default) {
        var sql = $@"
        SELECT
            COUNT(*) AS total_checks,
            SUM(CASE WHEN status = 'passed' THEN 1 ELSE 0 END) AS passed,
            SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END) AS failed,
            SUM(CASE WHEN status = 'warning' THEN 1 ELSE 0 END) AS warnings,
            COUNT(DISTINCT table_name) AS tables_checked,
            SUM(rows_checked) AS total_rows_checked,
            SUM(rows_failed) AS total_rows_failed
        FROM grantpud.observability.dq_check_results
        WHERE check_timestamp >= current_timestamp() - INTERVAL {Math.Abs(hours)} HOURS
    ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        if (result.Rows.Count == 0) {
            return Results.Ok(new { });
        }

        var row = result.Rows[0];
        var total = ParseInt(row[0]) ?? 0;
        var passed = ParseInt(row[1]) ?? 0;
        var failed = ParseInt(row[2]) ?? 0;
        var warnings = ParseInt(row[3]) ?? 0;
        var passRate = total > 0 ? Math.Round((double)passed / total * 100.0, 1) : (double?)null;

        return Results.Ok(new {
            windowHours = hours,
            totalChecks = total,
            passed,
            failed,
            warnings,
            passRate,
            tablesChecked = ParseInt(row[4]) ?? 0,
            totalRowsChecked = ParseLong(row[5]) ?? 0,
            totalRowsFailed = ParseLong(row[6]) ?? 0,
        });
    }

    private static async Task<IResult> GetDqRecentAsync(
        IDatabricksSqlClient client,
        ILogger<AnalyticsEndpointsLog> logger,
        int limit = 50,
        CancellationToken cancellationToken = default) {
        var safeLimit = Math.Clamp(limit, 1, 200);
        var sql = $@"
        SELECT
            check_timestamp,
            layer,
            table_name,
            check_category,
            check_name,
            severity,
            status,
            rows_checked,
            rows_failed,
            failure_rate_pct,
            details
        FROM grantpud.observability.dq_check_results
        ORDER BY check_timestamp DESC
        LIMIT {safeLimit}
    ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        var data = result.Rows.Select(r => new
        {
            checkTimestamp = r[0],
            layer = r[1],
            tableName = r[2],
            checkCategory = r[3],
            checkName = r[4],
            severity = r[5],
            status = r[6],
            rowsChecked = ParseLong(r[7]),
            rowsFailed = ParseLong(r[8]),
            failureRatePct = ParseDouble(r[9]),
            details = r[10],
        });

        return Results.Ok(data);
    }

    private static async Task<IResult> GetDqByTableAsync(
        IDatabricksSqlClient client,
        ILogger<AnalyticsEndpointsLog> logger,
        int hours = 168,
        CancellationToken cancellationToken = default) {
        var sql = $@"
        SELECT
            layer,
            table_name,
            COUNT(*) AS total_checks,
            SUM(CASE WHEN status = 'passed' THEN 1 ELSE 0 END) AS passed,
            SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END) AS failed,
            SUM(CASE WHEN status = 'warning' THEN 1 ELSE 0 END) AS warnings,
            MAX(check_timestamp) AS last_check
        FROM grantpud.observability.dq_check_results
        WHERE check_timestamp >= current_timestamp() - INTERVAL {Math.Abs(hours)} HOURS
        GROUP BY layer, table_name
        ORDER BY layer, table_name
    ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        var data = result.Rows.Select(r =>
        {
            var total = ParseInt(r[2]) ?? 0;
            var passed = ParseInt(r[3]) ?? 0;
            return new {
                layer = r[0],
                tableName = r[1],
                totalChecks = total,
                passed,
                failed = ParseInt(r[4]) ?? 0,
                warnings = ParseInt(r[5]) ?? 0,
                passRate = total > 0 ? Math.Round((double)passed / total * 100.0, 1) : (double?)null,
                lastCheck = r[6],
            };
        });

        return Results.Ok(data);
    }

    private static async Task<IResult> GetDqPassRateTrendAsync(
        IDatabricksSqlClient client,
        ILogger<AnalyticsEndpointsLog> logger,
        int days = 14,
        CancellationToken cancellationToken = default) {
        var sql = $@"
        SELECT
            check_date,
            COUNT(*) AS total_checks,
            SUM(CASE WHEN status = 'passed' THEN 1 ELSE 0 END) AS passed,
            SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END) AS failed,
            SUM(CASE WHEN status = 'warning' THEN 1 ELSE 0 END) AS warnings
        FROM grantpud.observability.dq_check_results
        WHERE check_timestamp >= current_timestamp() - INTERVAL {Math.Abs(days)} DAYS
        GROUP BY check_date
        ORDER BY check_date
    ";

        var result = await client.ExecuteAsync(sql, cancellationToken);
        var data = result.Rows.Select(r =>
        {
            var total = ParseInt(r[1]) ?? 0;
            var passed = ParseInt(r[2]) ?? 0;
            return new {
                date = r[0],
                totalChecks = total,
                passed,
                failed = ParseInt(r[3]) ?? 0,
                warnings = ParseInt(r[4]) ?? 0,
                passRate = total > 0 ? Math.Round((double)passed / total * 100.0, 1) : (double?)null,
            };
        });

        return Results.Ok(data);
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

    private static long? ParseLong(string? value) =>
    long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public partial class AnalyticsEndpointsLog { }