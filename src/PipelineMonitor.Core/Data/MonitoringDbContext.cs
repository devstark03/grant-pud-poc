using Microsoft.EntityFrameworkCore;
using PipelineMonitor.Core.Models;

namespace PipelineMonitor.Core.Data;

public class MonitoringDbContext : DbContext {
    public MonitoringDbContext(DbContextOptions<MonitoringDbContext> options)
        : base(options) {
    }

    public DbSet<PipelineRun> PipelineRuns => Set<PipelineRun>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<PipelineRun>(entity => {
            entity.ToTable("PipelineRuns");
            entity.HasIndex(e => e.EventGridEventId).IsUnique();
            entity.HasIndex(e => new { e.PipelineName, e.StartTime });
            entity.HasIndex(e => new { e.Status, e.StartTime });
        });

        modelBuilder.Entity<Alert>(entity => {
            entity.ToTable("Alerts");
            entity.HasIndex(e => new { e.CreatedAt, e.Severity });
        });
    }
}