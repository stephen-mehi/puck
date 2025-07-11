using Puck.Services;

public class RunParametersRepo
{
    private RunParameters? _activeParameters;

    public void SetActiveParameters(RunParameters parameters)
    {
        _activeParameters = parameters;
    }

    public RunParameters? GetActiveParameters()
    {
        return _activeParameters;
    }

    public RunParameters GetRunParametersById(string id)
    {
        //TODO: IMPLEMENT PERSISTENCE LAYER
        return null;
    }

}