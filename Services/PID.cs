using System;


namespace puck.Services;

public class PidControlLoop : IDisposable
{
    private bool _isDisposed;
    private Task? _controlTask;
    private readonly SemaphoreSlim _lock;
    private readonly CancellationTokenSource _ctSrc;

    public PidControlLoop()
    {
        _lock = new SemaphoreSlim(1, 1);
        _ctSrc = new CancellationTokenSource();
    }

    public async Task StartControlLoopAsync(
        double setpoint,
        Func<double> getProcessValue,
        Action<double> actuate,
        double proportionalGain,
        double integralGain,
        double derivativeGain,
        double derivativeFilterCoefficient,
        double outputUpperLimit,
        double outputLowerLimit,
        CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0))
            throw new Exception("Cannot start control loop if one already started");

        try
        {
            var pid =
                new PID(
                    proportionalGain,
                    integralGain,
                    derivativeGain,
                    derivativeFilterCoefficient,
                    outputUpperLimit,
                    outputLowerLimit);


            _controlTask = Task.Run(async () =>
            {
                using (var combinedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _ctSrc.Token))
                {
                    var currentTime = DateTime.UtcNow;
                    var previousTime = currentTime - TimeSpan.FromMilliseconds(100);

                    while (!_ctSrc.IsCancellationRequested)
                    {
                        var processVar = getProcessValue();
                        var output = pid.PID_iterate(setpoint, processVar, currentTime - previousTime);
                        actuate(output);

                        await Task.Delay(100, _ctSrc.Token);
                        previousTime = currentTime;
                        currentTime = DateTime.UtcNow;
                    }
                }
            });

            await _controlTask;
        }
        finally
        {
            _lock.Release();
        }

    }


    #region IDisposable

    protected void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            _ctSrc.Cancel();
            _controlTask?.Wait(5000);
            _ctSrc?.Dispose();
            _lock.Dispose();
        }

        _isDisposed = true;
    }

    public void Dispose()
    {
        // Dispose of unmanaged resources.
        Dispose(true);
        // Suppress finalization.
        GC.SuppressFinalize(this);
    }

    #endregion
}

public class PID
{
    //Sample period in seconds
    private double _samplePeriod;
    // Rollup parameter
    private double _K;
    // Rollup parameters
    private double _b0, _b1, _b2;
    // Rollup parameters
    private double _a0, _a1, _a2;
    // Current output
    private double _output = 0;
    // Output one iteration old
    private double _previousOutput = 0;
    // Output two iterations old
    private double _twicePreviousOutput = 0;
    // Current error
    private double _error = 0;
    // Error one iteration old
    private double _previousError = 0;
    // Error two iterations old
    private double _twicePreviousError = 0;

    // Minimum allowed sample period to avoid dividing by zero
    // The Ts value can be mistakenly set to too low of a value or zero on the first iteration.
    // TsMin by default is set to 1 millisecond.
    private double _tsMin = 0.001;

    /// <summary>
    /// PID Constructor
    /// </summary>
    /// <param name="kp">Proportional Gain</param>
    /// <param name="ki">Integral Gain</param>
    /// <param name="kd">Derivative Gain</param>
    /// <param name="n">Derivative Filter Coefficient</param>
    /// <param name="outputUpperLimit">Controller Upper Output Limit</param>
    /// <param name="outputLowerLimit">Controller Lower Output Limit</param>
    public PID(
        double kp,
        double ki,
        double kd,
        double n,
        double outputUpperLimit,
        double outputLowerLimit)
    {
        Kp = kp;
        Ki = ki;
        Kd = kd;
        N = n;
        OutputUpperLimit = outputUpperLimit;
        OutputLowerLimit = outputLowerLimit;
    }

    /// <summary>
    /// Proportional Gain, consider resetting controller if this parameter is drastically changed.
    /// </summary>
    public double Kp { get; }

    /// <summary>
    /// Integral Gain, consider resetting controller if this parameter is drastically changed.
    /// </summary>
    public double Ki { get; }

    /// <summary>
    /// Derivative Gain, consider resetting controller if this parameter is drastically changed.
    /// </summary>
    public double Kd { get; }

    /// <summary>
    /// Derivative filter coefficient.
    /// A smaller N for more filtering.
    /// A larger N for less filtering.
    /// Consider resetting controller if this parameter is drastically changed.
    /// </summary>
    public double N { get; }


    /// <summary>
    /// Upper output limit of the controller.
    /// This should obviously be a numerically greater value than the lower output limit.
    /// </summary>
    public double OutputUpperLimit { get; }

    /// <summary>
    /// Lower output limit of the controller
    /// This should obviously be a numerically lesser value than the upper output limit.
    /// </summary>
    public double OutputLowerLimit { get; }



    /// <summary>
    /// PID iterator, call this function every sample period to get the current controller output.
    /// setpoint and processValue should use the same units.
    /// </summary>
    /// <param name="setPoint">Current Desired Setpoint</param>
    /// <param name="processValue">Current Process Value</param>
    /// <param name="ts">Timespan Since Last Iteration, Use Default Sample Period for First Call</param>
    /// <returns>Current Controller Output</returns>
    public double PID_iterate(
        double setPoint,
        double processValue,
        TimeSpan ts)
    {
        // Ensure the timespan is not too small or zero.
        _samplePeriod = (ts.TotalSeconds >= _tsMin) ? ts.TotalSeconds : _tsMin;

        // Calculate rollup parameters
        _K = 2 / _samplePeriod;
        _b0 = Math.Pow(_K, 2) * Kp + _K * Ki + Ki * N + _K * Kp * N + Math.Pow(_K, 2) * Kd * N;
        _b1 = 2 * Ki * N - 2 * Math.Pow(_K, 2) * Kp - 2 * Math.Pow(_K, 2) * Kd * N;
        _b2 = Math.Pow(_K, 2) * Kp - _K * Ki + Ki * N - _K * Kp * N + Math.Pow(_K, 2) * Kd * N;
        _a0 = Math.Pow(_K, 2) + N * _K;
        _a1 = -2 * Math.Pow(_K, 2);
        _a2 = Math.Pow(_K, 2) - _K * N;

        // Age errors and output history
        // Age errors one iteration
        _twicePreviousError = _previousError;
        // Age errors one iteration
        _previousError = _error;
        // Compute new error
        _error = setPoint - processValue;
        // Age outputs one iteration
        _twicePreviousOutput = _previousOutput;
        // Age outputs one iteration
        _previousOutput = _output;
        // Calculate current output
        _output = -_a1 / _a0 * _previousOutput - _a2 / _a0 * _twicePreviousOutput + _b0 / _a0 * _error + _b1 / _a0 * _previousError + _b2 / _a0 * _twicePreviousError;

        // Clamp output if needed
        if (_output > OutputUpperLimit)
        {
            _output = OutputUpperLimit;
        }
        else if (_output < OutputLowerLimit)
        {
            _output = OutputLowerLimit;
        }

        return _output;
    }

    /// <summary>
    /// Reset controller history effectively resetting the controller.
    /// </summary>
    public void ResetController()
    {
        _twicePreviousError = 0;
        _previousError = 0;
        _error = 0;
        _twicePreviousOutput = 0;
        _previousOutput = 0;
        _output = 0;
    }
}
