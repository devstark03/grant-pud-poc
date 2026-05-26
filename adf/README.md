# Azure Data Factory

This directory contains ADF resource definitions exported as JSON from the ADF instance.

## Contents

- `linkedServices/` - Connections to ADLS Gen2, Key Vault, EIA API, and NOAA API
- `datasets/` - Dataset definitions for bronze layer landing zones
- `pipelines/` - Pipeline definitions (pl_bronze_ingest_daily, pl_bronze_backfill)
- `triggers/` - Scheduled and event-based triggers

## Deployment

The GitHub Actions workflow at `.github/workflows/deploy-adf.yml` deploys these resources via ARM templates using the Azure CLI. See that workflow for configuration details.
