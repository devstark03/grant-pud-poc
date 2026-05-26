# Architecture

This document describes the technical architecture of the Grant PUD PoC: what the system does, how the components interact, what design choices were made, and why. It complements the high-level overview in the root README with deeper detail on each layer.

## Overview

The system implements a medallion architecture on Azure for ingesting, processing, and analyzing public utility data. Two REST APIs flow through a bronze landing zone into curated silver Delta tables, then into a dimensional star schema in gold. Orchestration spans Azure Data Factory (ingestion) and Databricks Workflows (transformation), with Unity Catalog as the metadata and access control backbone.

The PoC is deliberately scoped to one balancing authority (BPA) and one weather station (Portland International Airport) to keep the data volume tractable while exercising every architectural pattern relevant to a production utility analytics platform.

## Components

| Component | Responsibility |
|-----------|---------------|
| Azure Data Factory | Bronze ingestion orchestration. Pulls from REST sources, lands raw JSON in ADLS Gen2 partitioned by date, then triggers the downstream Databricks job. |
| Azure Data Lake Storage Gen2 | All three medallion layers (bronze, silver, gold) plus a quarantine path for rejected rows. Hierarchical namespace enabled. |
| Azure Databricks | PySpark transformations from bronze to silver and silver to gold. Runs on serverless compute via Databricks Workflows. |
| Unity Catalog | Catalog, schema, table, and column metadata. External locations resolve abfss URIs through a managed-identity-backed credential. |
| Microsoft Purview | Cross-platform lineage and data catalog. Scans both ADLS Gen2 and Databricks. |
| Azure Key Vault | API keys for EIA and NOAA, plus the Databricks PAT used by ADF to invoke the Jobs API. |
| Azure SQL Database | Pipeline run metadata persistence for the .NET monitoring service. |
| Azure Event Grid | Event source for ADF pipeline run events (started, succeeded, failed). |
| .NET 8 Monitoring Service | Event Grid consumer + dashboard API. Persists run events to SQL and exposes a real-time pipeline health view. |
| GitHub | Source of truth for ADF (auto-committed), notebooks (via Databricks Repos), .NET code, and CI/CD workflows. |

## Data flow

The end-to-end flow for a single day of data:

1. **06:00 UTC trigger fires.** ADF Schedule trigger on `pl_bronze_ingest_daily` invokes the pipeline with `runDate` = yesterday in UTC.
2. **Key Vault lookups.** Three parallel Web activities fetch the EIA API key, NOAA token, and Databricks PAT via ADF's system-assigned managed identity.
3. **EIA ingestion.** Copy activity hits the EIA REST endpoint for one day of BPA data, lands the JSON response at `bronze/eia/BPAT/year=YYYY/month=MM/day=DD/eia_BPAT_YYYY-MM-DD.json`.
4. **NOAA ingestion.** Parallel Copy activity hits the NOAA REST endpoint for one day of KPDX observations, lands at `bronze/noaa/GHCND_USW00024229/year=YYYY/month=MM/day=DD/`.
5. **Databricks job invocation.** Web activity calls the Databricks Jobs API `run-now` endpoint with the configured job ID. ADF's responsibility ends here; the job runs asynchronously on Databricks-managed compute.
6. **Silver in parallel.** Two notebooks run concurrently: `bronze_to_silver_eia` and `bronze_to_silver_noaa`. Each reads from its bronze path with explicit schema enforcement, applies validation, deduplication, type conversion, and writes to Delta tables in `grantpud.silver`.
7. **Gold after silver completes.** `silver_to_gold` reads from both silver tables, builds the date dimension, manages SCD2 changes on the balancing authority dimension, joins silver data into the fact table, and writes everything to `grantpud.gold`.
8. **Events surface.** Throughout the flow, ADF emits pipeline run events to Event Grid. The .NET monitoring service consumes them, persists to Azure SQL, and updates the dashboard view in near real time.

## Medallion contract

Each layer has an explicit contract. Anything that violates the contract is a bug, not a feature.

**Bronze**

| Property | Value |
|----------|-------|
| Format | Raw JSON, byte-faithful to source response |
| Schema | None enforced. Source contract is the contract. |
| Partitioning | By date in the path: `year=YYYY/month=MM/day=DD/` |
| Mutability | Immutable. Re-ingesting a date overwrites that partition only. |
| Consumer | Silver notebooks only |

**Silver**

| Property | Value |
|----------|-------|
| Format | Delta Lake |
| Schema | Explicitly enforced via PySpark StructType definitions |
| Partitioning | By year and month, physical columns |
| Mutability | MERGE-based, idempotent on natural keys |
| Consumer | Gold notebooks and any ad hoc analytical queries |

**Gold**

| Property | Value |
|----------|-------|
| Format | Delta Lake |
| Schema | Kimball-style dimensional model |
| Partitioning | Fact table by `date_key` |
| Mutability | Dimensions are SCD-aware; facts MERGE on grain |
| Consumer | BI tools, dashboards, downstream applications |

## Dimensional model

The gold layer follows classical Kimball dimensional modeling.

`fact_daily_load_weather` has the grain "one row per day per balancing authority." Measures cover demand, generation, interchange, forecast, temperature, precipitation, and wind. Foreign keys reference three dimensions.

`dim_balancing_authority` is a Type 2 slowly-changing dimension. When an attribute changes upstream, the current row is closed (its `effective_to` set to the change timestamp and `is_current` set to false) and a new row is inserted with the updated attributes. Surrogate keys ensure the fact table references the version of the BA active at the time of the measurement. The implementation uses Delta `MERGE INTO` with explicit change detection on tracked attributes.

`dim_date` is built deterministically from the date range present in the source data. Reruns produce identical output. Standard calendar attributes (year, quarter, month, day_of_week, season, weekend flag) plus a surrogate `date_key` formatted as `yyyymmdd` for human-readable joins.

`dim_weather_station` is Type 1. Station metadata changes overwrite in place, since historical metadata for a fixed station has limited analytical value compared to electricity-side history.

## Security model

All Azure-to-Azure authentication uses managed identities and RBAC. No connection strings, account keys, or service principal secrets appear in pipeline or notebook code.

| Authenticating principal | Authenticates to | Role |
|--------------------------|------------------|------|
| ADF system MI | ADLS Gen2 | Storage Blob Data Contributor |
| ADF system MI | Key Vault | Key Vault Secrets User |
| Databricks Access Connector MI | ADLS Gen2 | Storage Blob Data Contributor |
| App Service system MI | Key Vault | Key Vault Secrets User |
| App Service system MI | Azure SQL | db_datareader, db_datawriter (via Entra) |

Databricks workspace access to ADLS Gen2 routes through Unity Catalog. A dedicated Azure Databricks Access Connector resource holds a system-assigned managed identity, which is registered as a storage credential in Unity Catalog. External locations point at the bronze, silver, and gold container roots and reference that credential. All notebook and SQL access to storage flows through Unity Catalog's permission model rather than direct cluster-level Spark configs.

The EIA API echoes the caller's API key in every response payload as a documented part of its response contract. There is no parameter to suppress this echo. The key is a free, read-only credential against public data with no associated authorization beyond rate limiting. The bronze layer retains the response faithfully, including this echo, as is appropriate for a layer whose contract is source fidelity. The silver bronze-to-silver transformation explicitly drops the `request` object, ensuring the credential does not propagate to consumer-facing layers. Access to bronze is restricted to the relevant managed identities and workspace administrators via Azure RBAC.

For sources with material credential risk such as PHI feeds under HIPAA, the appropriate pattern would be a network-boundary scrub via an Azure Function that calls the source, parses the response, removes sensitive fields, and writes the cleaned payload to bronze. The PoC architecture accommodates this addition cleanly without restructuring the existing layers.

## Data quality

Each silver notebook runs explicit validation rules before writing. Failures raise exceptions that propagate through Databricks Workflows, surfacing as job-level failures with diagnostic context.

Validation categories:

- **Schema enforcement.** Bronze JSON is read with an explicit StructType, so any upstream shape change surfaces immediately rather than silently producing nulls.
- **Null checks.** Primary key columns are asserted non-null before write.
- **Duplicate detection.** Natural key combinations are grouped and counted; any duplicates raise an exception.
- **Domain checks.** Where applicable (unit consistency, expected enum values), distinct values are compared against an expected set.
- **Row count assertions.** Empty result sets raise rather than silently writing a zero-row table.

These map to the JD's specified data validation rules, schema checks, and data quality expectations.

## Monitoring

The pipeline supports both scheduled and event-driven monitoring patterns.

**Scheduled monitoring.** ADF's Monitor view and Databricks Workflows UI each provide run history, duration metrics, alerting hooks, and per-step failure diagnostics. The trigger fires daily; missed or delayed runs are visible in the Monitor view's calendar.

**Event-driven monitoring.** ADF pipeline events (started, succeeded, failed) are published to Azure Event Grid. The .NET monitoring service subscribes to that topic, persists events to a `PipelineRuns` table in Azure SQL, and exposes a dashboard surfacing recent run history, failure rates, average duration, and active alerts. Because the service consumes events rather than polling, failures are visible within seconds of occurrence.

Detailed failure modes and recovery procedures live in `docs/runbook.md`.

## Catalog and lineage

Unity Catalog is the catalog and access control backbone for the data lake. The `grantpud` catalog contains `silver` and `gold` schemas, with tables defined by the bronze-to-silver and silver-to-gold notebooks. Columns inherit names and types from the PySpark schema definitions; future iterations would add column-level comments and tags.

Microsoft Purview provides cross-platform lineage by scanning both the ADLS Gen2 storage account and the Databricks workspace. The lineage graph for `fact_daily_load_weather` shows the full upstream chain: external EIA and NOAA APIs through bronze JSON files through silver Delta tables into the gold star schema. This satisfies the JD's "maintain dataset documentation, lineage notes, and catalog entries" requirement and provides discoverable metadata for analytics consumers.

## CI/CD

ADF resources are version-controlled via the native Azure Data Factory Git integration. The `main` branch is the collaboration branch where saves land as JSON under `adf/`. The `adf_publish` branch is auto-generated on publish and contains the consolidated ARM template suitable for deployment.

Notebooks live in `notebooks/` as source-format `.py` files, edited via Databricks Repos and pushed back to GitHub through Databricks' Git UI or via `git` locally.

The .NET monitoring service follows standard .NET solution conventions with three projects (Worker, Api, Tests). The xUnit test project covers event parsing and controller logic and runs on every push via GitHub Actions.

Branching follows GitHub Flow: feature branches off `main`, PRs trigger validation, merges to `main` trigger deployment. The three GitHub Actions workflows handle ADF (ARM template deployment to the target factory), notebooks (Databricks CLI sync to the target workspace path), and .NET (build, test, App Service deploy).

## Local setup

Reproducing this project from scratch requires:

| Requirement | Notes |
|-------------|-------|
| Azure subscription | Free tier sufficient; PoC fits well under the $200 credit |
| Azure CLI | `az login`, set default subscription |
| .NET 8 SDK | For the monitoring service |
| Databricks CLI | For notebook deployment via GitHub Actions |
| GitHub account | For the repo and Actions workflows |
| EIA API key | Free, register at eia.gov/opendata |
| NOAA token | Free, register at ncdc.noaa.gov/cdo-web/token |

Provisioning order, given service dependencies:

1. Resource group
2. Key Vault (other services need somewhere to store secrets)
3. Storage account with `bronze`, `silver`, `gold`, `quarantine` containers
4. Azure SQL Server and Database (with Entra ID admin set to your account)
5. Data Factory (enable system-assigned MI on creation)
6. Databricks workspace (Premium tier)
7. Access Connector for Azure Databricks
8. App Service Plan and App Service (.NET 8 runtime, system-assigned MI enabled)
9. Purview account
10. RBAC role assignments wiring each MI to the services it needs

After provisioning, the in-Databricks setup (catalogs, external locations, secret scopes, repo connection) takes another 30 minutes.

## Design decisions and tradeoffs

A few choices worth calling out, with the reasoning:

**Why Databricks Jobs instead of ADF directly orchestrating notebooks.** ADF has native Databricks Notebook activities, but they require a cluster definition in the linked service and don't compose well with serverless compute. Defining the transformations as a Databricks Job and invoking it from ADF via the Jobs API keeps each platform responsible for what it does best: ADF for cross-system orchestration, Databricks for compute scheduling and dependency management within the data engineering DAG. This also matches how mature teams typically organize the split in production.

**Why Unity Catalog external locations instead of cluster-level Spark configs.** Cluster Spark configs are workspace-local and require the same credential to be re-declared per cluster. Unity Catalog external locations centralize the credential, make access auditable, and unlock fine-grained permissions later if needed. This is also the path Databricks is steering all new deployments toward.

**Why SCD2 on the balancing authority dimension when there's only one BA.** The dimension is *modeled* for SCD2 even though the source has one static row, because the engineering pattern is what's being demonstrated. Adding more BAs later, or handling rare attribute changes (region reassignments, ownership transfers), requires zero schema changes.

**Why fire-and-forget on the Databricks job invocation.** ADF could poll the Jobs API for completion, but that couples ADF's pipeline status to Databricks's execution. Treating the two systems as independent (ADF's job is done once bronze lands and the downstream job is requested) keeps each monitoring layer focused on its own concerns. Databricks Workflows has its own retry, alerting, and dependency-management for the transformation DAG; ADF doesn't need to duplicate it.

**Why bronze contains the EIA API key.** Bronze is the raw layer. Its contract is faithfulness to the source. Modifying responses before bronze breaks that contract and means there's no longer a true "raw" layer in the architecture. The credential is non-sensitive (free, read-only, public data) so the operational cost of accepting it in bronze is near zero. For sources where the cost would be material, a separate ingestion-side scrub pattern is described in the security model section above.