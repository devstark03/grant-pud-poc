# Databricks Notebooks

This directory contains PySpark notebooks for medallion layer transformations.

## Notebooks

- `bronze_to_silver_eia.py` - Cleans and types raw EIA API responses from bronze JSON into a silver Delta table
- `bronze_to_silver_noaa.py` - Cleans and types raw NOAA CDO API responses from bronze JSON into a silver Delta table
- `silver_to_gold.py` - Joins silver tables and builds the gold star schema (fact_hourly_load, dim_date, dim_balancing_authority, dim_weather_station)

## Format

Notebooks are committed as `.py` files using the Databricks notebook source format. Each file begins with `# Databricks notebook source` and uses `# COMMAND ----------` as cell separators. This format version-controls cleanly as plain text and deploys directly via the Databricks CLI.

## Deployment

Notebooks are deployed to the Databricks workspace using the workflow at `.github/workflows/deploy-notebooks.yml`, which uses the Databricks CLI to sync files to the target workspace path.
