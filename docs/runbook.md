# Runbook

Operational procedures for diagnosing and recovering from failures in the Grant PUD PoC data pipeline. This document is tactical: each entry maps to a specific failure mode with symptoms, diagnostic steps, recovery procedures, verification, and rollback. Strategic incident response (communication protocols, customer notification, postmortem facilitation) belongs in a separate playbook and is not in scope here.

## Document metadata

| Field | Value |
|-------|-------|
| Owner | Data engineering team |
| Last reviewed | 2026-05-26 |
| Review cadence | Quarterly, or after any incident that reveals a runbook gap |
| Audience | On-call data engineer with Reader access to the Azure subscription and Editor access to ADF and Databricks |

## Severity scale

| Level | Definition | Time-to-respond |
|-------|------------|-----------------|
| SEV1 | Production data unavailable to consumers; gold layer stale by >24 hours; security incident | Immediate, 24/7 |
| SEV2 | Pipeline failure with degraded but partial data; recoverable within hours | During business hours, same-day |
| SEV3 | Single-component failure with no consumer impact; transient errors that self-resolved | Next business day |

Default escalation: if no measurable progress after 30 minutes on a SEV1 or 2 hours on a SEV2, escalate per the escalation table at the end of this document. Any incident involving customer data exposure or unauthorized access escalates to SEV1 regardless of operational impact.

## How to use this document

1. Identify the symptom from your alert, monitoring dashboard, or report.
2. Use the 5-minute triage checklist below to confirm the failure category.
3. Jump to the relevant section. Each entry follows the same structure: symptoms, severity, diagnose, recover, verify, rollback.
4. If the runbook does not resolve the issue, escalate per the table at the end.
5. After resolution, file a brief incident note in `docs/incidents/` if criteria in the Incident documentation section are met. If the runbook had a gap, open a PR to update this document.

## Keywords and error messages

Searchable terms operators are likely to encounter, mapped to the relevant section.

| Keyword or message fragment | Section |
|-----------------------------|---------|
| `AbfsRestOperationException` | Storage access issues |
| `AnalysisException`, schema mismatch | Schema drift in a source |
| `Provided access token does not have required scopes` | Databricks job invocation fails |
| HTTP 401, 403 on Key Vault calls | Pipeline failures: `pl_bronze_ingest_daily` |
| HTTP 401, 403 on EIA, NOAA, or Databricks | API failures, Databricks invocation |
| HTTP 429 | API failures: rate limiting |
| `Path does not exist` in silver notebook | Silver notebook fails |
| Null surrogate key in fact table | Gold notebook fails |
| `value_units` mismatch | Silver: data quality |
| Duplicate (period, respondent, type) | Silver: data quality |
| `is_current = true` for the same `ba_code` more than once | Gold notebook fails: SCD2 conflict |

## 5-minute triage checklist

Run through these checks before diving into a specific runbook section. They isolate the failure category in under five minutes.

- [ ] Open ADF Studio → Monitor → Pipeline runs. Did the most recent scheduled run of `pl_bronze_ingest_daily` succeed?
- [ ] Open Databricks → Workflows → recent runs of `grantpud-medallion-pipeline`. Did the most recent run succeed?
- [ ] Open the storage browser. Are bronze files present for the expected date partitions?
- [ ] Open the Databricks SQL editor. Does `SELECT MAX(period_utc) FROM grantpud.silver.electricity_hourly` return a recent timestamp?
- [ ] Open the .NET monitoring dashboard. Are there active alerts?

If the answer to questions 1 and 2 is yes, the pipeline is healthy and the issue is likely consumer-side. If question 1 failed, start with "Pipeline failures: `pl_bronze_ingest_daily`." If question 2 failed but question 1 passed, start with "Silver notebook fails" or "Gold notebook fails" based on the Databricks run details.

## Pipeline failures

### RB-DATA-001: `pl_bronze_ingest_daily` fails

**Severity**: SEV2 by default. Upgrade to SEV1 if bronze has been failing for more than 24 consecutive hours.

**Symptoms**

- ADF Monitor view shows the daily run in red
- .NET monitoring dashboard surfaces an alert keyed to `pl_bronze_ingest_daily`
- No new files appear in `bronze/eia/` or `bronze/noaa/` for the target date

**Trigger conditions**

- Azure Monitor alert rule on ADF pipeline failure events for this pipeline
- Event Grid event with `eventType = Microsoft.DataFactory.PipelineRunFinished` and `data.status = Failed`

**Diagnose**

1. Open ADF Studio, navigate to Monitor → Pipeline runs, filter by `pl_bronze_ingest_daily`.
2. Click the failed run to expand the activity-level view.
3. Identify which activity failed. Likely culprits, in rough order of historical frequency:
   - `Get_EIA_ApiKey` or `Get_NOAA_Token` (Key Vault access)
   - `Copy_EIA_to_Bronze` (EIA API outage, rate limit, schema change)
   - `Copy_NOAA_to_Bronze` (NOAA API outage, rate limit)
   - `Trigger_Databricks_Job` (Databricks PAT expired or job ID changed)

**Recover**

- For transient API failures (5xx from EIA or NOAA): re-run the pipeline manually with the same `runDate`. ADF idempotently overwrites the bronze partition.
- For credential issues: see RB-DATA-007 (API credential refresh).
- For Databricks invocation failures: see RB-DATA-002.

Re-run manually via Author tab → open `pl_bronze_ingest_daily` → Debug, supply the failed date as the `runDate` parameter.

**Verify recovery**

- The re-run completes with green status in Monitor.
- Storage browser shows the bronze files at the expected partition path with non-zero size.
- The downstream Databricks job kicks off and completes within 10 minutes.

**Rollback**

Bronze writes are idempotent overwrites of a specific date partition, so there is no traditional rollback. If a re-run produces a worse result than the original failure (rare), delete the date partition manually via `az storage blob delete-batch` and re-run.

---

### RB-DATA-002: Databricks job invocation fails

**Severity**: SEV2.

**Symptoms**

- ADF's `Trigger_Databricks_Job` activity fails with HTTP 401, 403, or 404
- Bronze files are present but silver and gold tables do not update

**Trigger conditions**

- ADF Monitor view shows the Web activity in red within `pl_bronze_ingest_daily`

**Diagnose**

| Status code | Cause |
|-------------|-------|
| 401 | Databricks PAT in Key Vault is expired or invalid |
| 403 | PAT lacks the `jobs` scope, or workspace policy restricts token use |
| 404 | The job ID in the activity body no longer exists; check Databricks Workflows |

**Recover**

- 401 or 403: regenerate the PAT in Databricks (Settings → Developer → Access tokens) with the `jobs` scope. Update the `databricks-pat` secret in Key Vault:

  ```
  az keyvault secret set --vault-name <kv-name> --name databricks-pat --value "<new-token>"
  ```

  ADF picks up the new value on next pipeline run.

- 404: open Databricks Workflows, confirm `grantpud-medallion-pipeline` still exists. If it was deleted, recreate from `notebooks/`. Update the job ID in the `Trigger_Databricks_Job` activity body in ADF.

**Verify recovery**

- Re-run `pl_bronze_ingest_daily` in Debug mode.
- The `Trigger_Databricks_Job` activity returns HTTP 200 with a `run_id` in the response.
- The triggered Databricks job appears in Workflows within 30 seconds and progresses through its tasks.

**Rollback**

If the new PAT does not work, revert by setting the Key Vault secret to the previous value (Key Vault retains version history under Secrets → select secret → Versions). The previous version is the immediate fallback while the root cause is investigated.

---

### RB-DATA-003: Silver notebook fails

**Severity**: SEV2.

**Symptoms**

- Databricks Workflows shows `bronze_to_silver_eia` or `bronze_to_silver_noaa` task in red
- `silver_to_gold` task is skipped because upstream failed

**Trigger conditions**

- Databricks job run alert on task failure for `grantpud-medallion-pipeline`

**Diagnose**

1. Open Databricks → Workflows → the failed run → click the failed task → View logs.
2. Look for the actual exception. Most likely:
   - `AnalysisException: Schema mismatch` — upstream JSON shape changed; see RB-DATA-008.
   - Data quality `ValueError` raised by the notebook (unexpected nulls, duplicates, or unit mismatches)
   - `Path does not exist` — bronze ingestion failed silently or wrote to an unexpected path

**Recover**

- Schema mismatch: see RB-DATA-008.
- Data quality exception: the source data has an issue. Inspect the offending records via the silver notebook's exploded DataFrame. Quarantine the bad rows if appropriate (write to `bronze/quarantine/`) or escalate to the source owner.
- Missing bronze data: confirm bronze ingestion ran for the affected date. If not, run `pl_bronze_ingest_daily` manually first, then re-run the failed Databricks job.

**Verify recovery**

- Re-run the failed task in Databricks Workflows.
- The task completes with green status.
- Query the relevant silver table for the affected date range:
  ```sql
  SELECT COUNT(*) FROM grantpud.silver.electricity_hourly
  WHERE year = <year> AND month = <month>;
  ```
- Row count matches expected volume (BPAT: 24 hours × 4 types per day; NOAA: 1 row per day per station).

**Rollback**

Silver MERGE operations are idempotent. If the recovery write produced bad data, restore from Delta time travel:
```sql
RESTORE TABLE grantpud.silver.electricity_hourly TO VERSION AS OF <previous_version>;
```
Find the previous version via `DESCRIBE HISTORY grantpud.silver.electricity_hourly`.

---

### RB-DATA-004: Gold notebook fails

**Severity**: SEV2.

**Symptoms**

- `silver_to_gold` task in Databricks Workflows shows red
- Gold tables are stale; fact table missing rows for recent dates

**Trigger conditions**

- Databricks job run alert on task failure

**Diagnose**

1. Open the failed task logs in Databricks Workflows.
2. Common causes:
   - Silver tables are empty or missing the expected dates (upstream silver failure not surfaced)
   - SCD2 merge conflict (rare; concurrent writes to `dim_balancing_authority`)
   - Join produces null surrogate keys (a row in silver does not have a matching dimension)

**Recover**

- Empty silver: confirm silver notebooks ran successfully for the target date. Re-run them if needed via RB-DATA-003 procedure.
- SCD2 conflict: not expected in current scope since only one BA is loaded. If encountered, check `dim_balancing_authority` for orphan rows where `is_current = true` for the same `ba_code`:
  ```sql
  SELECT ba_code, COUNT(*) FROM grantpud.gold.dim_balancing_authority
  WHERE is_current = true GROUP BY ba_code HAVING COUNT(*) > 1;
  ```
  Manually correct by setting `is_current = false` and `effective_to = current_timestamp()` on the older row.
- Null surrogate keys: inspect the failed grain check output. Either backfill the missing dimension row or skip the offending fact rows.

**Verify recovery**

- Re-run the `silver_to_gold` task.
- Query the fact table for the expected date range:
  ```sql
  SELECT COUNT(*) FROM grantpud.gold.fact_daily_load_weather
  WHERE date_key BETWEEN <start> AND <end>;
  ```
- Verify dimension integrity:
  ```sql
  SELECT COUNT(*) FROM grantpud.gold.fact_daily_load_weather f
  LEFT JOIN grantpud.gold.dim_balancing_authority d ON f.balancing_authority_key = d.ba_key
  WHERE d.ba_key IS NULL;
  ```
  Expected: 0 orphan facts.

**Rollback**

Use Delta time travel as in RB-DATA-003. Gold tables are also MERGE-based and idempotent.

## API failures

### RB-DATA-005: EIA API outage or rate limiting

**Severity**: SEV3 unless outage exceeds 24 hours, then SEV2.

**Symptoms**

- `Copy_EIA_to_Bronze` fails with HTTP 4xx or 5xx, or with timeout
- EIA status page or social media confirms an outage

**Trigger conditions**

- Repeated failures in ADF Monitor view for the EIA Copy activity

**Diagnose**

- Confirm at https://www.eia.gov/opendata/ that the API is operational.
- Check the response body for specific error codes if returned.

**Recover**

- For rate limits (HTTP 429): EIA's documented limits are generous; this is rare. Wait an hour and re-run.
- For sustained outages: the EIA data has a 1-day publication lag anyway. Document the missing date in this runbook's incident log, then backfill via `pl_bronze_backfill` once the API recovers.

**Verify recovery**

- Test the EIA endpoint directly via curl:
  ```
  curl "https://api.eia.gov/v2/electricity/rto/region-data/data/?api_key=<key>&frequency=hourly&data[]=value&facets[respondent][]=BPAT&start=2026-04-01T00&end=2026-04-01T01"
  ```
- Re-run the backfill for the missing date range.
- Confirm bronze partitions and downstream silver/gold rows populate.

**Rollback**

Not applicable; this is a wait-and-retry scenario.

---

### RB-DATA-006: NOAA API outage

**Severity**: SEV3 unless outage exceeds 24 hours, then SEV2.

**Symptoms**

- `Copy_NOAA_to_Bronze` fails with HTTP 4xx or 5xx
- NOAA status page confirms an outage

**Trigger conditions**

- Repeated failures in ADF Monitor view for the NOAA Copy activity

**Diagnose**

- Confirm at https://www.ncdc.noaa.gov/cdo-web/ that the API is operational.
- NOAA's CDO API has historically had higher downtime than EIA. Plan for occasional missing days.

**Recover**

Backfill via `pl_bronze_backfill` once recovered, using the missing date range.

**Verify recovery**

- Test the NOAA endpoint directly with the token header:
  ```
  curl -H "token: <token>" "https://www.ncei.noaa.gov/cdo-web/api/v2/data?datasetid=GHCND&stationid=GHCND:USW00024229&startdate=2026-04-01&enddate=2026-04-01&limit=1000&units=metric"
  ```
- Re-run the backfill.
- Query `grantpud.silver.weather_daily` for the affected dates.

**Rollback**

Not applicable.

---

### RB-DATA-007: API credential refresh

**Severity**: SEV3 for planned rotation, SEV2 for emergency rotation due to suspected compromise.

**When this applies**

- API keys are rotated periodically per security policy
- 401 or 403 responses from EIA or NOAA suggest credential issues
- Security incident requires emergency rotation

**Procedure**

1. Generate a new key:
   - EIA: https://www.eia.gov/opendata/register.php (request resends the same key; for a true rotation, contact EIA support)
   - NOAA: https://www.ncdc.noaa.gov/cdo-web/token
   - Databricks PAT: in Databricks, Settings → Developer → Access tokens → Generate, with `jobs` scope enabled
2. Update the corresponding secret in Key Vault:
   ```
   az keyvault secret set --vault-name <kv-name> --name eia-api-key --value "<new-key>"
   az keyvault secret set --vault-name <kv-name> --name noaa-token --value "<new-token>"
   az keyvault secret set --vault-name <kv-name> --name databricks-pat --value "<new-pat>"
   ```
3. ADF picks up the new value on the next pipeline run. No redeployment required.
4. Document the rotation date in the team's credential inventory.

**Verify**

- Trigger `pl_bronze_ingest_daily` in Debug mode with today's date.
- Confirm Key Vault secret-fetch activities succeed.
- Confirm Copy activities reach the source APIs successfully.

**Rollback**

Key Vault retains secret version history. To revert: Key Vault → Secrets → select secret → Versions → click previous version → Enable. ADF will use the now-active previous version on next run.

## Storage access issues

### RB-DATA-008: Schema drift in a source

**Severity**: SEV2.

**Symptoms**

- Silver notebook fails with `AnalysisException` referencing a missing or unexpected column
- The pipeline was working previously; nothing changed on our side

**Trigger conditions**

- Databricks job run alert on `AnalysisException` in any silver task

**Diagnose**

1. Inspect a recent bronze file in the storage browser. Compare its top-level structure to the StructType defined in the silver notebook.
2. Check the source's release notes or changelog for API version changes.

**Recover**

1. Update the StructType in the silver notebook to reflect the new shape.
2. Test the change against a recent bronze file before merging.
3. Open a PR; require review before merge.
4. Reprocess affected dates via the silver notebook's idempotent MERGE; no bronze re-ingestion is needed unless the source changed historical data.
5. Document the schema change with its effective date in this runbook's change log.

**Verify recovery**

- Silver task completes successfully.
- New columns or transformations produce expected values for a hand-checked sample of rows.
- Downstream gold layer rebuilds without errors.

**Rollback**

Revert the silver notebook change via git. Use Delta time travel on the silver table to restore the previous shape:
```sql
RESTORE TABLE grantpud.silver.electricity_hourly TO VERSION AS OF <previous_version>;
```

---

### RB-DATA-009: ADF cannot write to bronze

**Severity**: SEV1 if it persists more than 30 minutes, otherwise SEV2.

**Symptoms**

- Copy activity fails with `AbfsRestOperationException` or HTTP 403 from the storage endpoint

**Trigger conditions**

- ADF Copy activity failure with `403` or `AuthorizationPermissionMismatch` in the error message

**Diagnose**

- Verify ADF's system-assigned managed identity still has `Storage Blob Data Contributor` on the storage account. Check via Storage account → IAM → Role assignments.
- If the role assignment was recently changed, RBAC propagation can take up to 30 minutes.

**Recover**

- Re-grant the role if missing:
  ```
  az role assignment create \
    --assignee-object-id <adf-mi-object-id> \
    --assignee-principal-type ServicePrincipal \
    --role "Storage Blob Data Contributor" \
    --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<storage>"
  ```
- Wait 10-15 minutes after re-granting, then re-run the pipeline.

**Verify recovery**

- Re-run `pl_bronze_ingest_daily` in Debug mode.
- Confirm bronze files appear in storage browser.

**Rollback**

Not applicable; this is a restoration of expected permissions.

---

### RB-DATA-010: Databricks cannot read bronze

**Severity**: SEV2.

**Symptoms**

- Silver notebook fails on the initial `spark.read` with a permission error

**Trigger conditions**

- Databricks task failure with storage permission errors in logs

**Diagnose**

- Check Unity Catalog → External Locations → confirm the relevant external location (for example, `loc-grantpud-bronze`) exists and is bound to the correct storage credential.
- Verify the storage credential's Access Connector still exists in Azure and has `Storage Blob Data Contributor` on the storage account.

**Recover**

- Restore missing external location or credential through the Catalog UI.
- Re-grant RBAC on the Access Connector if it was lost:
  ```
  az role assignment create \
    --assignee-object-id <access-connector-mi-object-id> \
    --assignee-principal-type ServicePrincipal \
    --role "Storage Blob Data Contributor" \
    --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<storage>"
  ```

**Verify recovery**

- In a Databricks notebook, run:
  ```python
  display(dbutils.fs.ls("abfss://bronze@<storage>.dfs.core.windows.net/"))
  ```
- The listing should succeed and show recent partition folders.

**Rollback**

Not applicable.

## Backfill operations

### Reprocess a single date

```
ADF Studio → pl_bronze_ingest_daily → Debug → runDate = YYYY-MM-DD
```

Bronze overwrites that date's partition. Then either wait for the silver/gold cascade triggered by ADF, or manually trigger `grantpud-medallion-pipeline` in Databricks Workflows.

### Reprocess a date range

```
ADF Studio → pl_bronze_backfill → Debug → startDate = YYYY-MM-DD, daysToGet = N
```

The backfill iterates `pl_bronze_ingest_daily` for each date sequentially. For N > 30, expect 15-30 minutes total. Silver and gold update incrementally as each date completes.

## Escalation

Time-based and impact-based criteria for escalation.

| Trigger | Action |
|---------|--------|
| No measurable progress on a SEV1 after 30 minutes | Escalate to senior data engineer |
| No measurable progress on a SEV2 after 2 hours | Escalate to senior data engineer |
| Suspected security incident at any severity | Escalate immediately to security on-call |
| Issue requires changes outside the data engineering scope (network, identity, subscription-level RBAC) | Escalate to cloud platform team |
| Source API issue persists beyond 24 hours | Open ticket with source provider |

| Issue type | First responder | Escalate to |
|------------|----------------|-------------|
| Pipeline failure within the data engineering scope | On-call data engineer | Senior data engineer |
| Azure infrastructure outage (region-wide) | Cloud platform team | Microsoft support |
| Source API outage (EIA, NOAA) | On-call data engineer | Source provider's support channel |
| Security incident (credential leak, unauthorized access) | Security on-call | CISO and cloud platform team |

## Incident documentation

For any non-trivial incident, defined as one that required manual intervention beyond a single re-run, or caused downstream impact lasting more than 4 hours, create a postmortem covering:

- Incident timeline (when symptoms appeared, when diagnosed, when resolved)
- Root cause
- Customer impact (which downstream consumers saw stale or missing data)
- Recovery actions taken
- Preventive measures and follow-up tickets

Postmortems live in `docs/incidents/` named `YYYY-MM-DD-summary.md`.

If the incident revealed a gap in this runbook, the postmortem must include a follow-up to update the runbook, and the PR for that update should reference the postmortem.

## Change log

| Date | Change | Author |
|------|--------|--------|
| 2026-05-26 | Initial runbook created during PoC development | Garrett John |