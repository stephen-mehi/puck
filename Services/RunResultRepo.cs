using Microsoft.Extensions.Caching.Memory;
using puck.Services;

public class RunResultRepo
{
    private RunResult _latest;
    private readonly object _lock = new object();


    public RunResult? GetLatestRunResultOrDefault()
    {
        return _latest;
    }

    public void SetLatestRunResult(RunResult runResult)
    {
        lock (_lock)
        {
            _latest = runResult;
        }
    }


    public void Commit()
    {
        lock (_lock)
        {
            //TODO: PERSIST
        }
    }
}