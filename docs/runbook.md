# Runbook

This runbook documents operational procedures for the Grant PUD PoC data pipelines. It covers common failure modes, recovery steps, and escalation paths. All pipeline monitoring alerts route through the .NET monitoring service and are visible on the operations dashboard.

## Pipeline Failures

### pl_bronze_ingest_daily

**Symptoms:** Scheduled trigger fires but the pipeline run shows as failed in ADF Monitor.

**Common causes:**

- API rate limiting or temporary unavailability
- Expired or revoked API keys in Key Vault
- ADLS Gen2 storage access issues (SAS token expiry, RBAC misconfiguration)

**Recovery steps:**

TODO: Document recovery steps for each failure mode.

### pl_bronze_backfill

**Symptoms:** Manually triggered backfill run fails partway through a date range.

**Common causes:**

- API throttling during large historical pulls
- Timeout on large date ranges
- Concurrent runs conflicting on partition paths

**Recovery steps:**

TODO: Document recovery steps, including how to resume from a partial backfill.

## API Outages

### EIA API

**Detection:** Pipeline activity `act_call_eia_api` fails with HTTP 5xx or timeout.

TODO: Document fallback behavior and retry configuration.

### NOAA CDO API

**Detection:** Pipeline activity `act_call_noaa_api` fails with HTTP 5xx or timeout.

TODO: Document fallback behavior and retry configuration.

## Storage Access Issues

**Symptoms:** Pipeline activities fail with 403 Forbidden or storage timeout errors when writing to ADLS Gen2.

**Possible causes:**

- Managed identity permissions revoked or not propagated
- Storage account firewall rules blocking ADF integration runtime
- SAS token expiry (if used instead of managed identity)

TODO: Document diagnostic steps and resolution procedures.

## Escalation Contacts

| Role | Contact | When to Escalate |
|------|---------|-----------------|
| Data Engineer (primary) | TODO | Pipeline failures not resolved within 1 hour |
| Platform / Infra | TODO | Storage, networking, or identity issues |
| API Data Sources | TODO | Sustained API outages beyond documented SLAs |
