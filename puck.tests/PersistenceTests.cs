using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Puck.Services;
using Puck.Services.Persistence;
using Xunit;

namespace puck.tests;

public class PersistenceTests
{
    private static PuckDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<PuckDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var db = new PuckDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static RunParameters SampleParams(double ip = 5, int ght = 200, int tbt = 205)
        => new RunParameters
        {
            InitialPumpSpeed = ip,
            GroupHeadTemperatureFarenheit = ght,
            ThermoblockTemperatureFarenheit = tbt,
            PreExtractionTargetTemperatureFarenheit = 200,
            ExtractionWeightGrams = 18,
            MaxExtractionSeconds = 30,
            TargetPressureBar = 9
        };

    [Fact]
    public async Task RunParametersRepo_CRUD_ById_And_Name_And_Activation()
    {
        using var db = CreateInMemoryDb();
        var repo = new RunParametersRepo(db);

        // Create
        var id = await repo.CreateProfileAsync("Default Espresso", SampleParams(), notes: "baseline");
        Assert.True(id > 0);

        // Read by id
        var p = await repo.GetProfileAsync(id);
        Assert.NotNull(p);
        Assert.Equal("Default Espresso", p!.Name);
        Assert.Equal(5, p.InitialPumpSpeed);

        // Read by name
        var byName = await repo.GetProfileByNameAsync("Default Espresso");
        Assert.NotNull(byName);

        // List
        var all = await repo.ListProfilesAsync();
        Assert.Single(all);

        // Update by id
        var updated = await repo.UpdateProfileAsync(id, SampleParams(ip: 7), name: "Default Espresso", notes: "tuned");
        Assert.True(updated);
        var p2 = await repo.GetProfileAsync(id);
        Assert.Equal(7, p2!.InitialPumpSpeed);

        // Update by name
        var updByName = await repo.UpdateProfileByNameAsync("Default Espresso", SampleParams(ip: 6));
        Assert.True(updByName);
        var p3 = await repo.GetProfileByNameAsync("Default Espresso");
        Assert.Equal(6, p3!.InitialPumpSpeed);

        // Activate by id
        var act = await repo.ActivateProfileAsync(id);
        Assert.True(act);
        var active = repo.GetActiveParameters();
        Assert.NotNull(active);
        Assert.Equal(6, active!.InitialPumpSpeed);

        // Activate by name
        var actByName = await repo.ActivateProfileByNameAsync("Default Espresso");
        Assert.True(actByName);
    }

    [Fact]
    public async Task RunParametersRepo_Delete_ClearsActivation()
    {
        using var db = CreateInMemoryDb();
        var repo = new RunParametersRepo(db);
        var id = await repo.CreateProfileAsync("Shot", SampleParams());
        await repo.ActivateProfileAsync(id);
        Assert.NotNull(repo.GetActiveParameters());
        var del = await repo.DeleteProfileAsync(id);
        Assert.True(del);
        Assert.Null(repo.GetActiveParameters());
    }

    [Fact]
    public async Task PidProfile_UniqueName_Enforced()
    {
        using var db = CreateInMemoryDb();
        db.PidProfiles.Add(new PidProfile { Name = "Active", Kp = 1, Ki = 1, Kd = 1 });
        await db.SaveChangesAsync();
        db.PidProfiles.Add(new PidProfile { Name = "Active", Kp = 2, Ki = 2, Kd = 2 });
        await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
    }

    [Fact]
    public async Task CascadeDelete_Profile_Deletes_Runs_And_Samples()
    {
        using var db = CreateInMemoryDb();
        var profile = new PidProfile { Name = "Active", Kp = 1, Ki = 1, Kd = 1 };
        db.PidProfiles.Add(profile);
        await db.SaveChangesAsync();

        var run = new PidAutotuneRun
        {
            PidProfileId = profile.Id,
            DtSeconds = 0.1,
            Steps = 3,
            TargetPressurePsi = 30,
            MaxSafePressurePsi = 50,
            MinPumpSpeed = 4,
            MaxPumpSpeed = 14,
            BestKp = 1.1,
            BestKi = 1.2,
            BestKd = 0.1,
            Iae = 10,
            MaxOvershoot = 1,
            SteadyStateError = 0.1,
            FinalSteadyStateError = 0.05
        };
        db.PidAutotuneRuns.Add(run);
        await db.SaveChangesAsync();

        db.PidAutotuneSamples.AddRange(
            new PidAutotuneSample { PidAutotuneRunId = run.Id, TimeSeconds = 0.1, Setpoint = 10, Process = 9, Output = 5, Windup = false, ClampedUpper = false, ClampedLower = false },
            new PidAutotuneSample { PidAutotuneRunId = run.Id, TimeSeconds = 0.2, Setpoint = 15, Process = 12, Output = 6, Windup = false, ClampedUpper = false, ClampedLower = false }
        );
        await db.SaveChangesAsync();

        // Delete profile -> cascade should remove runs and samples
        db.PidProfiles.Remove(profile);
        await db.SaveChangesAsync();

        Assert.False(db.PidAutotuneRuns.Any());
        Assert.False(db.PidAutotuneSamples.Any());
    }
}


