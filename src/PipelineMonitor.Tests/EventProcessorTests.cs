using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PipelineMonitor.Core.Data;
using PipelineMonitor.Core.Events;
using PipelineMonitor.Core.Services;

namespace PipelineMonitor.Tests;

/// <summary>
/// Tests for EventProcessor cover the contract that ADF Event Grid events get
/// translated into PipelineRun rows correctly, that duplicate deliveries are
/// suppressed via the EventGridEventId unique constraint, and that pipeline
/// failures produce alerts. These are the behaviors that other parts of the
/// system rely on; if EventProcessor breaks, the dashboard misrepresents what's
/// happening in production.
/// </summary>
public class EventProcessorTests : IDisposable {
    private readonly SqliteConnection _connection;
    private readonly MonitoringDbContext _db;
    private readonly EventProcessor _sut;

    public EventProcessorTests() {
        // SQLite in-memory mode. Connection must stay open for the lifetime of
        // the test or the database is dropped. Using SQLite rather than the
        // EF Core InMemory provider because InMemory doesn't enforce unique
        // constraints, which we explicitly want to exercise.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MonitoringDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new MonitoringDbContext(options);
        _db.Database.EnsureCreated();

        _sut = new EventProcessor(_db);
    }

    public void Dispose() {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ProcessAsync_PipelineRunSucceeded_PersistsRunWithExpectedFields() {
        var startTime = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var endTime = startTime.AddMinutes(3);
        var envelope = new EventGridEnvelope {
            Id = "event-001",
            EventType = "Microsoft.DataFactory.PipelineRunSucceeded",
            Subject = "/subscriptions/.../factories/adf-grantpud-poc/pipelineruns/run-abc",
            EventTime = endTime,
            Data = new AdfPipelineEventData {
                PipelineName = "pl_bronze_ingest_daily",
                RunId = "run-abc",
                Status = "Succeeded",
                FactoryName = "adf-grantpud-poc",
                StartTime = startTime,
                EndTime = endTime,
                Message = null,
            },
        };

        var result = await _sut.ProcessAsync(envelope);

        result.Should().Be(EventProcessingResult.Persisted);

        var run = await _db.PipelineRuns.SingleAsync();
        run.PipelineName.Should().Be("pl_bronze_ingest_daily");
        run.Status.Should().Be("Succeeded");
        run.StartTime.Should().Be(startTime);
        run.EndTime.Should().Be(endTime);
        run.DurationSeconds.Should().Be(180);
        run.EventGridEventId.Should().Be("event-001");

        var alertCount = await _db.Alerts.CountAsync();
        alertCount.Should().Be(0, "successful runs should not produce alerts");
    }

    [Fact]
    public async Task ProcessAsync_PipelineRunFailed_CreatesAlertAndRecordsErrorMessage() {
        var envelope = new EventGridEnvelope {
            Id = "event-002",
            EventType = "Microsoft.DataFactory.PipelineRunFailed",
            Subject = "/subscriptions/.../pipelineruns/run-fail",
            EventTime = DateTime.UtcNow,
            Data = new AdfPipelineEventData {
                PipelineName = "pl_bronze_ingest_daily",
                RunId = "run-fail",
                Status = "Failed",
                FactoryName = "adf-grantpud-poc",
                StartTime = DateTime.UtcNow.AddMinutes(-5),
                EndTime = DateTime.UtcNow,
                Message = "Copy_EIA_to_Bronze failed: HTTP 503 from EIA API",
            },
        };

        await _sut.ProcessAsync(envelope);

        var alert = await _db.Alerts.SingleAsync();
        alert.PipelineName.Should().Be("pl_bronze_ingest_daily");
        alert.PipelineRunId.Should().Be("run-fail");
        alert.Severity.Should().Be("SEV2");
        alert.Message.Should().Contain("Copy_EIA_to_Bronze failed");
        alert.AcknowledgedAt.Should().BeNull("new alerts are unacknowledged by default");

        var run = await _db.PipelineRuns.SingleAsync();
        run.Status.Should().Be("Failed");
        run.ErrorMessage.Should().Be("Copy_EIA_to_Bronze failed: HTTP 503 from EIA API");
    }

    [Fact]
    public async Task ProcessAsync_DuplicateEventGridEventId_IsSuppressed() {
        var envelope = new EventGridEnvelope {
            Id = "event-duplicate",
            EventType = "Microsoft.DataFactory.PipelineRunSucceeded",
            Subject = "/subscriptions/.../pipelineruns/run-x",
            EventTime = DateTime.UtcNow,
            Data = new AdfPipelineEventData {
                PipelineName = "pl_bronze_ingest_daily",
                RunId = "run-x",
                Status = "Succeeded",
                FactoryName = "adf-grantpud-poc",
                StartTime = DateTime.UtcNow.AddMinutes(-2),
                EndTime = DateTime.UtcNow,
            },
        };

        var firstResult = await _sut.ProcessAsync(envelope);
        var secondResult = await _sut.ProcessAsync(envelope);

        firstResult.Should().Be(EventProcessingResult.Persisted);
        secondResult.Should().Be(EventProcessingResult.Duplicate,
            "Event Grid delivers at-least-once; second delivery of same event ID must not double-write");

        var runCount = await _db.PipelineRuns.CountAsync();
        runCount.Should().Be(1, "duplicate events must not create duplicate run records");
    }

    [Fact]
    public async Task ProcessAsync_UnsupportedEventType_IsIgnoredWithoutPersisting() {
        var envelope = new EventGridEnvelope {
            Id = "event-unsupported",
            EventType = "Microsoft.Storage.BlobCreated",
            Subject = "/subscriptions/.../blobServices/default/containers/bronze/blobs/test.json",
            EventTime = DateTime.UtcNow,
            Data = new AdfPipelineEventData(),
        };

        var result = await _sut.ProcessAsync(envelope);

        result.Should().Be(EventProcessingResult.Ignored,
            "the processor should ignore events outside the ADF pipeline run namespace");

        var runCount = await _db.PipelineRuns.CountAsync();
        runCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_StatusMissing_NormalizesFromEventType() {
        // ADF event payloads can arrive without a status field populated.
        // The processor must infer the status from the event type so the
        // dashboard never displays "Unknown" for a real run.
        var envelope = new EventGridEnvelope {
            Id = "event-no-status",
            EventType = "Microsoft.DataFactory.PipelineRunFailed",
            Subject = "/subscriptions/.../pipelineruns/run-no-status",
            EventTime = DateTime.UtcNow,
            Data = new AdfPipelineEventData {
                PipelineName = "pl_bronze_ingest_daily",
                RunId = "run-no-status",
                Status = "",
                FactoryName = "adf-grantpud-poc",
                StartTime = DateTime.UtcNow.AddMinutes(-1),
                EndTime = DateTime.UtcNow,
            },
        };

        await _sut.ProcessAsync(envelope);

        var run = await _db.PipelineRuns.SingleAsync();
        run.Status.Should().Be("Failed",
            "status must be derived from event type when the payload field is empty");

        var alert = await _db.Alerts.SingleOrDefaultAsync();
        alert.Should().NotBeNull("derived Failed status should still produce an alert");
    }
}