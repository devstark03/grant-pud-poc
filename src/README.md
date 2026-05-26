# .NET Monitoring Service

The PipelineMonitor solution is a .NET 8 application that provides operational visibility into the ADF pipeline runs.

## Projects

- **PipelineMonitor.Worker** - A Worker Service that subscribes to Azure Event Grid events emitted by ADF pipeline activities. It processes success and failure events, persists run metadata to Azure SQL, and triggers alerts on failures.
- **PipelineMonitor.Api** - An ASP.NET Core Web API that serves pipeline run status, history, and health data to the operations dashboard.
- **PipelineMonitor.Tests** - Unit and integration tests for both the Worker and API projects.

## Solution

The `PipelineMonitor.sln` solution file ties the three projects together. Individual project files will be added as implementation progresses.
