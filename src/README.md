# .NET Monitoring Service

The PipelineMonitor solution is a .NET 8 application that provides operational visibility into the ADF pipeline runs.

## Projects

- **PipelineMonitor.Api** - An ASP.NET Core Web API that serves pipeline run status, history, and health data to the operations dashboard.
- **PipelineMonitor.Core** - The Core library that connects the API with the functionality of this project
- **PipelineMonitor.Tests** - Unit and integration tests for both the Worker and API projects.

## Solution

The `PipelineMonitor.sln` solution file ties the three projects together. Individual project files will be added as implementation progresses.
