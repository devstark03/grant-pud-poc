# Databricks notebook source
# MAGIC %md
# MAGIC # Silver to Gold: Star Schema Build
# MAGIC
# MAGIC Joins silver_eia_load and silver_noaa_weather on hour_utc, builds dimension tables
# MAGIC (dim_date, dim_balancing_authority with SCD2, dim_weather_station), and writes
# MAGIC fact_hourly_load to the gold layer.

# COMMAND ----------

# TODO: implementation
