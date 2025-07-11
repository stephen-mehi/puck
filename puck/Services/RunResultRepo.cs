using Microsoft.Extensions.Caching.Memory;
using Puck.Services;

public class RunResultRepo
{
    private RunResult? _latestRunResult;
    private readonly object _lock = new object();


    public RunResult? GetLatestRunResultOrDefault()
    {
        return _latestRunResult;
    }

    public void SetLatestRunResult(RunResult result)
    {
        _latestRunResult = result;
    }

    public RunResult? GetLatestRunResult()
    {
        return _latestRunResult;
    }

    public void Commit()
    {
        lock (_lock)
        {
            //TODO: PERSIST
        }
    }
}