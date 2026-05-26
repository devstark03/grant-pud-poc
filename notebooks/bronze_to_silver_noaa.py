# Databricks notebook source
# Databricks notebook source
# MAGIC %md
# MAGIC # Bronze to Silver: NOAA Weather Observations
# MAGIC
# MAGIC Reads NOAA daily weather observations from bronze, pivots from long to wide
# MAGIC format, applies schema and type conversions, and writes to silver as a Delta
# MAGIC table partitioned by year and month.

# COMMAND ----------

from pyspark.sql import functions as F
from pyspark.sql.types import (
    StructType, StructField, StringType, DoubleType, IntegerType, ArrayType
)
from delta.tables import DeltaTable

STORAGE_ACCOUNT = "grantpudpocstorage"
BRONZE_PATH = f"abfss://bronze@{STORAGE_ACCOUNT}.dfs.core.windows.net/noaa"
SILVER_TABLE = "grantpud.silver.weather_daily"

# COMMAND ----------

# MAGIC %md
# MAGIC ## Read bronze JSON with explicit schema
# MAGIC
# MAGIC Spark recursively reads all NOAA JSON files under the bronze path. The schema
# MAGIC is enforced explicitly to catch upstream changes rather than letting Spark
# MAGIC infer silently across runs.

# COMMAND ----------

noaa_schema = StructType([
    StructField("metadata", StructType([
        StructField("resultset", StructType([
            StructField("offset", IntegerType()),
            StructField("count", IntegerType()),
            StructField("limit", IntegerType()),
        ]))
    ])),
    StructField("results", ArrayType(StructType([
        StructField("date", StringType()),
        StructField("datatype", StringType()),
        StructField("station", StringType()),
        StructField("attributes", StringType()),
        StructField("value", DoubleType()),
    ])))
])

bronze_df = (
    spark.read
    .schema(noaa_schema)
    .option("recursiveFileLookup", "true")
    .json(BRONZE_PATH)
    .withColumn("_source_file", F.input_file_name())
)

# COMMAND ----------

# MAGIC %md
# MAGIC ## Explode the results array
# MAGIC
# MAGIC Each NOAA JSON contains a results array with one entry per datatype per day.
# MAGIC Exploding flattens it so each measurement is its own row.

# COMMAND ----------

exploded_df = (
    bronze_df
    .select(F.explode("results").alias("r"), "_source_file")
    .select(
        F.to_date(F.col("r.date")).alias("observation_date"),
        F.col("r.station").alias("station_id"),
        F.col("r.datatype").alias("datatype"),
        F.col("r.value").alias("value"),
        F.col("r.attributes").alias("attributes"),
        F.col("_source_file"),
    )
    .filter(F.col("observation_date").isNotNull())
    .filter(F.col("station_id").isNotNull())
)

# COMMAND ----------

# MAGIC %md
# MAGIC ## Pivot to wide format
# MAGIC
# MAGIC The long form (date, datatype, value) is reshaped so each row represents one
# MAGIC (date, station) combination with each datatype as its own column. Restricting
# MAGIC the pivot to a known list of datatypes prevents schema drift if NOAA adds new
# MAGIC measurement types upstream.

# COMMAND ----------

expected_datatypes = [
    "TMAX", "TMIN", "AWND", "PRCP",
    "SNOW", "SNWD", "WDF2", "WDF5", "WSF2", "WSF5",
]

pivoted_df = (
    exploded_df
    .groupBy("observation_date", "station_id")
    .pivot("datatype", expected_datatypes)
    .agg(F.first("value"))
)

column_renames = {
    "TMAX": "temp_max_c",
    "TMIN": "temp_min_c",
    "AWND": "wind_avg_ms",
    "PRCP": "precip_mm",
    "SNOW": "snowfall_mm",
    "SNWD": "snow_depth_mm",
    "WDF2": "wind_dir_2min_deg",
    "WDF5": "wind_dir_5sec_deg",
    "WSF2": "wind_speed_2min_ms",
    "WSF5": "wind_speed_5sec_ms",
}

for old_name, new_name in column_renames.items():
    pivoted_df = pivoted_df.withColumnRenamed(old_name, new_name)

# COMMAND ----------

# MAGIC %md
# MAGIC ## Add lineage and partition columns
# MAGIC
# MAGIC `_loaded_at` enables time-based debugging. `_source_file` enables tracing any
# MAGIC row back to its origin bronze file. `year` and `month` are physical partition
# MAGIC columns to speed up date-range queries downstream.

# COMMAND ----------

source_file_map = (
    exploded_df
    .groupBy("observation_date", "station_id")
    .agg(F.first("_source_file").alias("_source_file"))
)

silver_df = (
    pivoted_df
    .join(source_file_map, ["observation_date", "station_id"], "left")
    .withColumn("_loaded_at", F.current_timestamp())
    .withColumn("year", F.year("observation_date"))
    .withColumn("month", F.month("observation_date"))
)

# COMMAND ----------

# MAGIC %md
# MAGIC ## Data quality checks
# MAGIC
# MAGIC Validates assumptions about the data before writing. Fail fast if reality
# MAGIC diverges from expectations.

# COMMAND ----------

row_count = silver_df.count()
print(f"Rows ready to merge: {row_count}")

if row_count == 0:
    raise ValueError("No rows produced; check bronze path and date partitions.")

null_date_count = silver_df.filter(F.col("observation_date").isNull()).count()
if null_date_count > 0:
    raise ValueError(f"{null_date_count} rows have null observation_date.")

duplicate_count = (
    silver_df.groupBy("observation_date", "station_id")
    .count()
    .filter("count > 1")
    .count()
)
if duplicate_count > 0:
    raise ValueError(f"{duplicate_count} duplicate (date, station) keys detected.")

print("Data quality checks passed.")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Write to silver as Delta
# MAGIC
# MAGIC On first run, creates the table. On subsequent runs, merges by primary key so
# MAGIC reprocessing bronze for any date is idempotent.

# COMMAND ----------

if not spark.catalog.tableExists(SILVER_TABLE):
    (
        silver_df.write
        .format("delta")
        .partitionBy("year", "month")
        .saveAsTable(SILVER_TABLE)
    )
    print(f"Created table {SILVER_TABLE} with {row_count} rows.")
else:
    target = DeltaTable.forName(spark, SILVER_TABLE)
    (
        target.alias("t")
        .merge(
            silver_df.alias("s"),
            "t.observation_date = s.observation_date AND t.station_id = s.station_id"
        )
        .whenMatchedUpdateAll()
        .whenNotMatchedInsertAll()
        .execute()
    )
    print(f"Merged {row_count} rows into {SILVER_TABLE}.")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Verification

# COMMAND ----------

result = spark.table(SILVER_TABLE)
print(f"Total rows in silver table: {result.count()}")
print(f"Distinct stations: {result.select('station_id').distinct().count()}")

date_range = result.agg(
    F.min("observation_date").alias("min_date"),
    F.max("observation_date").alias("max_date"),
).collect()[0]
print(f"Date range: {date_range['min_date']} to {date_range['max_date']}")

display(result.orderBy("observation_date").limit(10))