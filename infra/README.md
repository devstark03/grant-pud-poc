# Infrastructure

This directory will hold Bicep or ARM templates for reproducible infrastructure provisioning.

The current PoC infrastructure was provisioned manually via the Azure portal. The intent is to codify all resource definitions here so the environment can be torn down and recreated reliably. Expected templates will cover:

- Resource group and tagging
- ADLS Gen2 storage account with hierarchical namespace
- Azure Data Factory instance
- Databricks workspace (Premium tier)
- Azure SQL Database
- Azure Key Vault
- Event Grid system topic and subscriptions
- Microsoft Purview account
- Managed identity assignments and RBAC roles
