# Data Dictionary

## Bronze Layer

Raw JSON responses stored in ADLS Gen2, partitioned by `source/yyyy/MM/dd/`. No schema enforcement at this layer.

### EIA Raw Response

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| period | string | ISO timestamp of the observation hour | EIA API | No |
| respondent | string | Balancing authority code (e.g., BPAT) | EIA API | No |
| type-name | string | Data series type (demand, generation, interchange) | EIA API | No |
| value | number | Reported value in MWh | EIA API | Yes |
| value-units | string | Unit of measure | EIA API | No |

### NOAA Raw Response

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| DATE | string | ISO timestamp of the observation | NOAA CDO API | No |
| STATION | string | Weather station identifier (e.g., GHCND:USW00024229) | NOAA CDO API | No |
| HourlyDryBulbTemperature | string | Air temperature in Fahrenheit | NOAA CDO API | Yes |
| HourlyRelativeHumidity | string | Relative humidity percentage | NOAA CDO API | Yes |
| HourlyWindSpeed | string | Wind speed in mph | NOAA CDO API | Yes |

## Silver Layer

Cleaned, typed, and deduplicated Delta tables.

### silver_eia_load

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| hour_utc | timestamp | Observation hour in UTC | bronze EIA | No |
| balancing_authority | string | BA code (BPAT) | bronze EIA | No |
| series_type | string | demand, generation, or interchange | bronze EIA | No |
| value_mwh | double | Reported value in MWh | bronze EIA | Yes |
| ingested_at | timestamp | Bronze ingestion timestamp | pipeline metadata | No |

### silver_noaa_weather

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| hour_utc | timestamp | Observation hour in UTC | bronze NOAA | No |
| station_id | string | GHCND station identifier | bronze NOAA | No |
| temperature_f | double | Dry bulb temperature in Fahrenheit | bronze NOAA | Yes |
| temperature_c | double | Converted temperature in Celsius | derived | Yes |
| humidity_pct | double | Relative humidity percentage | bronze NOAA | Yes |
| wind_speed_mph | double | Wind speed in mph | bronze NOAA | Yes |
| ingested_at | timestamp | Bronze ingestion timestamp | pipeline metadata | No |

## Gold Layer

Star schema optimized for analytical queries.

### fact_hourly_load

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| hour_utc | timestamp | Observation hour in UTC | silver_eia_load | No |
| date_key | int | Foreign key to dim_date | derived | No |
| balancing_authority_key | int | Foreign key to dim_balancing_authority | derived | No |
| weather_station_key | int | Foreign key to dim_weather_station | derived | No |
| demand_mwh | double | Hourly demand in MWh | silver_eia_load | Yes |
| generation_mwh | double | Hourly generation in MWh | silver_eia_load | Yes |
| interchange_mwh | double | Hourly interchange in MWh | silver_eia_load | Yes |
| temperature_c | double | Corresponding hourly temperature in Celsius | silver_noaa_weather | Yes |

### dim_date

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| date_key | int | Surrogate key (yyyyMMdd format) | derived | No |
| full_date | date | Calendar date | derived | No |
| year | int | Calendar year | derived | No |
| month | int | Calendar month | derived | No |
| day | int | Day of month | derived | No |
| day_of_week | string | Day name (Monday, Tuesday, etc.) | derived | No |
| is_weekend | boolean | True if Saturday or Sunday | derived | No |

### dim_balancing_authority (SCD2)

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| balancing_authority_key | int | Surrogate key | derived | No |
| balancing_authority_code | string | BA code (e.g., BPAT) | EIA API | No |
| balancing_authority_name | string | Full name of the balancing authority | EIA API | No |
| region | string | Operating region | EIA API | Yes |
| effective_from | date | SCD2 row validity start | derived | No |
| effective_to | date | SCD2 row validity end (9999-12-31 for current) | derived | No |
| is_current | boolean | True if this is the active record | derived | No |

### dim_weather_station

| Field Name | Data Type | Description | Source | Nullable |
|------------|-----------|-------------|--------|----------|
| weather_station_key | int | Surrogate key | derived | No |
| station_id | string | GHCND station identifier | NOAA CDO API | No |
| station_name | string | Station name (e.g., Portland International Airport) | NOAA CDO API | No |
| latitude | double | Station latitude | NOAA CDO API | No |
| longitude | double | Station longitude | NOAA CDO API | No |
| elevation_m | double | Station elevation in meters | NOAA CDO API | Yes |
