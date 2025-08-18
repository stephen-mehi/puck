using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

namespace Puck.Services.Persistence
{
    public class PuckDbContext : DbContext
    {
        public PuckDbContext(DbContextOptions<PuckDbContext> options) : base(options) { }

        public DbSet<PidProfile> PidProfiles => Set<PidProfile>();
        public DbSet<PidAutotuneRun> PidAutotuneRuns => Set<PidAutotuneRun>();
        public DbSet<PidAutotuneSample> PidAutotuneSamples => Set<PidAutotuneSample>();
        public DbSet<RunParametersProfile> RunParametersProfiles => Set<RunParametersProfile>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PidProfile>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(100);
                e.HasIndex(x => x.Name).IsUnique();
                e.Property(x => x.CreatedUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<PidAutotuneRun>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.StartedUtc);
                e.HasOne(x => x.PidProfile)
                    .WithMany(x => x.AutotuneRuns)
                    .HasForeignKey(x => x.PidProfileId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<PidAutotuneSample>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.PidAutotuneRunId, x.TimeSeconds });
                e.HasOne(x => x.AutotuneRun)
                    .WithMany(x => x.Samples)
                    .HasForeignKey(x => x.PidAutotuneRunId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<RunParametersProfile>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).IsRequired().HasMaxLength(100);
                e.HasIndex(x => x.Name).IsUnique();
                e.Property(x => x.CreatedUtc).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });
        }
    }

    public class PidProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty; // e.g., "Default", "Light Roast"
        public double Kp { get; set; }
        public double Ki { get; set; }
        public double Kd { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }

        public List<PidAutotuneRun> AutotuneRuns { get; set; } = new();
    }

    public class PidAutotuneRun
    {
        public int Id { get; set; }
        public int? PidProfileId { get; set; }
        public PidProfile? PidProfile { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedUtc { get; set; }

        // Autotune configuration
        public double DtSeconds { get; set; }
        public int Steps { get; set; }
        public double TargetPressurePsi { get; set; }
        public double MaxSafePressurePsi { get; set; }
        public double MinPumpSpeed { get; set; }
        public double MaxPumpSpeed { get; set; }

        // Resulting best PID
        public double BestKp { get; set; }
        public double BestKi { get; set; }
        public double BestKd { get; set; }

        // Key metrics matching PID_GeneticAutotune_Simulation
        public double Iae { get; set; }
        public double MaxOvershoot { get; set; }
        public double SteadyStateError { get; set; }
        public double FinalSteadyStateError { get; set; }

        public List<PidAutotuneSample> Samples { get; set; } = new();
    }

    public class PidAutotuneSample
    {
        public int Id { get; set; }
        public int PidAutotuneRunId { get; set; }
        public PidAutotuneRun AutotuneRun { get; set; } = null!;

        // Per-sample record (subset similar to PID_GeneticAutotune_Simulation logs)
        public double TimeSeconds { get; set; }
        public double Setpoint { get; set; }
        public double Process { get; set; }
        public double Output { get; set; }
        public bool Windup { get; set; }
        public bool ClampedUpper { get; set; }
        public bool ClampedLower { get; set; }
    }

    public class RunParametersProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty; // e.g., "Default Espresso"
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        // Snapshot of RunParameters
        public double InitialPumpSpeed { get; set; }
        public int GroupHeadTemperatureFarenheit { get; set; }
        public int ThermoblockTemperatureFarenheit { get; set; }
        public int PreExtractionTargetTemperatureFarenheit { get; set; }
        public double ExtractionWeightGrams { get; set; }
        public int MaxExtractionSeconds { get; set; }
        public double TargetPressureBar { get; set; }

        public string? Notes { get; set; }
    }
}


