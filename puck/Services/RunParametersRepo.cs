using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Puck.Services;
using Puck.Services.Persistence;

public class RunParametersRepo
{
    private readonly PuckDbContext _db;
    private int? _activeProfileId;

    public RunParametersRepo(PuckDbContext db)
    {
        _db = db;
    }

    // Create
    public async Task<int> CreateProfileAsync(string name, RunParameters parameters, string? notes = null, CancellationToken ct = default)
    {
        var profile = new RunParametersProfile
        {
            Name = name,
            InitialPumpSpeed = parameters.InitialPumpSpeed,
            GroupHeadTemperatureFarenheit = parameters.GroupHeadTemperatureFarenheit,
            ThermoblockTemperatureFarenheit = parameters.ThermoblockTemperatureFarenheit,
            PreExtractionTargetTemperatureFarenheit = parameters.PreExtractionTargetTemperatureFarenheit,
            ExtractionWeightGrams = parameters.ExtractionWeightGrams,
            MaxExtractionSeconds = parameters.MaxExtractionSeconds,
            TargetPressureBar = parameters.TargetPressureBar,
            Notes = notes
        };
        _db.RunParametersProfiles.Add(profile);
        await _db.SaveChangesAsync(ct);
        return profile.Id;
    }

    // Read: by id
    public async Task<RunParametersProfile?> GetProfileAsync(int id, CancellationToken ct = default)
        => await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Id == id, ct);

    // Read: list
    public async Task<List<RunParametersProfile>> ListProfilesAsync(CancellationToken ct = default)
        => await _db.RunParametersProfiles.OrderBy(x => x.Name).ToListAsync(ct);

    // Read: by name
    public async Task<RunParametersProfile?> GetProfileByNameAsync(string name, CancellationToken ct = default)
        => await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Name == name, ct);

    // Update
    public async Task<bool> UpdateProfileAsync(int id, RunParameters parameters, string? name = null, string? notes = null, CancellationToken ct = default)
    {
        var entity = await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null) return false;
        if (!string.IsNullOrWhiteSpace(name)) entity.Name = name;
        entity.InitialPumpSpeed = parameters.InitialPumpSpeed;
        entity.GroupHeadTemperatureFarenheit = parameters.GroupHeadTemperatureFarenheit;
        entity.ThermoblockTemperatureFarenheit = parameters.ThermoblockTemperatureFarenheit;
        entity.PreExtractionTargetTemperatureFarenheit = parameters.PreExtractionTargetTemperatureFarenheit;
        entity.ExtractionWeightGrams = parameters.ExtractionWeightGrams;
        entity.MaxExtractionSeconds = parameters.MaxExtractionSeconds;
        entity.TargetPressureBar = parameters.TargetPressureBar;
        entity.Notes = notes ?? entity.Notes;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateProfileByNameAsync(string name, RunParameters parameters, string? notes = null, CancellationToken ct = default)
    {
        var entity = await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Name == name, ct);
        if (entity == null) return false;
        entity.InitialPumpSpeed = parameters.InitialPumpSpeed;
        entity.GroupHeadTemperatureFarenheit = parameters.GroupHeadTemperatureFarenheit;
        entity.ThermoblockTemperatureFarenheit = parameters.ThermoblockTemperatureFarenheit;
        entity.PreExtractionTargetTemperatureFarenheit = parameters.PreExtractionTargetTemperatureFarenheit;
        entity.ExtractionWeightGrams = parameters.ExtractionWeightGrams;
        entity.MaxExtractionSeconds = parameters.MaxExtractionSeconds;
        entity.TargetPressureBar = parameters.TargetPressureBar;
        entity.Notes = notes ?? entity.Notes;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // Delete
    public async Task<bool> DeleteProfileAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null) return false;
        _db.RunParametersProfiles.Remove(entity);
        await _db.SaveChangesAsync(ct);
        if (_activeProfileId == id) _activeProfileId = null;
        return true;
    }

    public async Task<bool> DeleteProfileByNameAsync(string name, CancellationToken ct = default)
    {
        var entity = await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Name == name, ct);
        if (entity == null) return false;
        _db.RunParametersProfiles.Remove(entity);
        await _db.SaveChangesAsync(ct);
        if (_activeProfileId == entity.Id) _activeProfileId = null;
        return true;
    }

    // Activate profile (set as current)
    public async Task<bool> ActivateProfileAsync(int id, CancellationToken ct = default)
    {
        var entity = await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity == null) return false;
        _activeProfileId = id;
        return true;
    }

    public async Task<bool> ActivateProfileByNameAsync(string name, CancellationToken ct = default)
    {
        var entity = await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Name == name, ct);
        if (entity == null) return false;
        _activeProfileId = entity.Id;
        return true;
    }

    // Get active as RunParameters
    public RunParameters? GetActiveParameters()
    {
        if (!_activeProfileId.HasValue) return null;
        var p = _db.RunParametersProfiles.FirstOrDefault(x => x.Id == _activeProfileId.Value);
        if (p == null) return null;
        return new RunParameters
        {
            InitialPumpSpeed = p.InitialPumpSpeed,
            GroupHeadTemperatureFarenheit = p.GroupHeadTemperatureFarenheit,
            ThermoblockTemperatureFarenheit = p.ThermoblockTemperatureFarenheit,
            PreExtractionTargetTemperatureFarenheit = p.PreExtractionTargetTemperatureFarenheit,
            ExtractionWeightGrams = p.ExtractionWeightGrams,
            MaxExtractionSeconds = p.MaxExtractionSeconds,
            TargetPressureBar = p.TargetPressureBar
        };
    }

    // Convenience: set active by passing parameters (creates or updates a special profile)
    public async Task SetActiveParametersAsync(RunParameters parameters, CancellationToken ct = default)
    {
        const string name = "ActiveRun";
        var existing = await _db.RunParametersProfiles.FirstOrDefaultAsync(x => x.Name == name, ct);
        if (existing == null)
        {
            var id = await CreateProfileAsync(name, parameters, notes: "Ephemeral active run params", ct: ct);
            _activeProfileId = id;
        }
        else
        {
            await UpdateProfileAsync(existing.Id, parameters, notes: existing.Notes, ct: ct);
            _activeProfileId = existing.Id;
        }
    }
}