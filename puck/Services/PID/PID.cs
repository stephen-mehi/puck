using System;


namespace puck.Services.PID;

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
        Func<double, CancellationToken, Task> actuate,
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

                    while (!combinedCt.IsCancellationRequested)
                    {
                        var processVar = getProcessValue();
                        var output = pid.PID_iterate(setpoint, processVar, currentTime - previousTime);
                        await actuate(output, combinedCt.Token);

                        await Task.Delay(100, combinedCt.Token);
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

public class PID : IPID
{
    // Sample period in seconds
    private double _samplePeriod;
    // Integral term for anti-windup
    private double _integral = 0;
    // Last process value for derivative on measurement
    private double _lastProcessValue = 0;
    // Last derivative (for filtering)
    private double _lastDerivative = 0;
    // Last output (for rate limiting)
    private double _lastOutput = 0;
    // Thread safety
    private readonly object _lock = new object();
    // Minimum allowed sample period
    private double _tsMin;
    // Setpoint weighting
    private readonly double _proportionalSetpointWeight;
    // Derivative filter using standard first-order "dirty derivative" with coefficient N
    // d_filtered += alpha * (d_raw - d_filtered), where alpha = Ts / (1/N + Ts)
    // Output rate limit (units per second)
    private double _maxOutputRate;
    // Setpoint ramp rate (units per second)
    private double _setpointRampRate;
    // Deadband (error units)
    private double _deadband;
    // Integral separation band (error units)
    private double _integralSeparationBand;
    // Feedforward term
    private double _feedforward;
    // Diagnostics
    public double LastIntegral => _integral;
    public double LastDerivative => _lastDerivative;
    public double LastOutput => _lastOutput;
    public double LastError { get; private set; }
    public bool WindupAlarm { get; private set; }
    public bool InstabilityAlarm { get; private set; }
    // Setpoint ramping
    private double _lastSetpoint = double.NaN;
    // Bumpless transfer support
    private double _prevSetpointForBumpless = double.NaN;
    private const double _bumplessSetpointThreshold = 0.01;
    private double _lastKp;
    private double _lastKi;
    private double _lastKd;
    // Optional output low-pass filter (time constant in seconds; 0 disables)
    private double _outputFilterTimeConstant = 0.0;
    private double _lastFilteredOutput = 0.0;

    /// <summary>
    /// PID Constructor
    /// </summary>
    /// <param name="kp">Proportional Gain</param>
    /// <param name="ki">Integral Gain</param>
    /// <param name="kd">Derivative Gain</param>
    /// <param name="n">Derivative filter coefficient (rad/s). Larger N = less filtering.</param>
    /// <param name="outputUpperLimit">Controller Upper Output Limit</param>
    /// <param name="outputLowerLimit">Controller Lower Output Limit</param>
    /// <param name="tsMin">Minimum allowed sample period (seconds)</param>
    /// <param name="proportionalSetpointWeight">Setpoint weighting for proportional term (default 1.0)</param>
    /// <param name="derivativeSetpointWeight">Setpoint weighting for derivative term (default 0.0)</param>
    /// <param name="derivativeFilterAlpha">Alpha for derivative low-pass filter (0 < alpha < 1, default 0.1)</param>
    /// <param name="maxOutputRate">Maximum output rate (units/sec, 0 disables, default 0)</param>
    /// <param name="setpointRampRate">Setpoint ramp rate (units/sec, 0 disables, default 0)</param>
    /// <param name="deadband">Deadband for error (default 0)</param>
    /// <param name="integralSeparationBand">Integral separation band (default double.MaxValue, disables)</param>
    public PID(
        double kp,
        double ki,
        double kd,
        double n,
        double outputUpperLimit,
        double outputLowerLimit,
        double tsMin = 0.001,
        double proportionalSetpointWeight = 1.0,
        double derivativeSetpointWeight = 0.0,
        double derivativeFilterAlpha = 0.1,
        double maxOutputRate = 0.0,
        double setpointRampRate = 0.0,
        double deadband = 0.0,
        double integralSeparationBand = double.MaxValue)
    {
        if (outputUpperLimit <= outputLowerLimit)
            throw new ArgumentException("Output upper limit must be greater than lower limit.");
        if (kp < 0 || ki < 0 || kd < 0 || n < 0)
            throw new ArgumentException("Gains and filter coefficient must be non-negative.");
        if (tsMin <= 0)
            throw new ArgumentException("Minimum sample period must be positive.");
        if (derivativeFilterAlpha <= 0 || derivativeFilterAlpha >= 1)
            throw new ArgumentException("Derivative filter alpha must be between 0 and 1.");
        Kp = kp;
        Ki = ki;
        Kd = kd;
        N = n;
        OutputUpperLimit = outputUpperLimit;
        OutputLowerLimit = outputLowerLimit;
        _tsMin = tsMin;
        _proportionalSetpointWeight = proportionalSetpointWeight;
        // derivativeSetpointWeight not used (DoM). Keep N for filtering; Alpha path deprecated.
        _maxOutputRate = maxOutputRate;
        _setpointRampRate = setpointRampRate;
        _deadband = deadband;
        _integralSeparationBand = integralSeparationBand;
        _feedforward = 0.0;
        _lastSetpoint = double.NaN;
        _lastKp = Kp;
        _lastKi = Ki;
        _lastKd = Kd;
        _lastFilteredOutput = 0.0;
    }

    /// <summary>
    /// Proportional Gain, consider resetting controller if this parameter is drastically changed.
    /// </summary>
    public double Kp { get; private set; }
    /// <summary>
    /// Integral Gain, consider resetting controller if this parameter is drastically changed.
    /// </summary>
    public double Ki { get; private set; }
    /// <summary>
    /// Derivative Gain, consider resetting controller if this parameter is drastically changed.
    /// </summary>
    public double Kd { get; private set; }
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
    public double OutputUpperLimit { get; private set; }
    /// <summary>
    /// Lower output limit of the controller
    /// This should obviously be a numerically lesser value than the upper output limit.
    /// </summary>
    public double OutputLowerLimit { get; private set; }

    /// <summary>
    /// Optional feedforward term to be added to the output.
    /// </summary>
    public double Feedforward { get => _feedforward; set => _feedforward = value; }

    /// <summary>
    /// Set the derivative filter alpha (0 < alpha < 1).
    /// </summary>
    // Deprecated: kept for API compatibility, unused in filtering path
    public double DerivativeFilterAlpha { get => 0; set { /* no-op */ } }

    /// <summary>
    /// Set the output rate limit (units/sec, 0 disables).
    /// </summary>
    public double MaxOutputRate { get => _maxOutputRate; set => _maxOutputRate = value; }

    /// <summary>
    /// Set the setpoint ramp rate (units/sec, 0 disables).
    /// </summary>
    public double SetpointRampRate { get => _setpointRampRate; set => _setpointRampRate = value; }

    /// <summary>
    /// Set the deadband (error units).
    /// </summary>
    public double Deadband { get => _deadband; set => _deadband = value; }

    /// <summary>
    /// Set the integral separation band (error units).
    /// </summary>
    public double IntegralSeparationBand { get => _integralSeparationBand; set => _integralSeparationBand = value; }

    /// <summary>
    /// Optional output low-pass filter time constant in seconds. Set to 0 to disable.
    /// </summary>
    public double OutputFilterTimeConstant { get => _outputFilterTimeConstant; set => _outputFilterTimeConstant = value < 0 ? 0 : value; }

    /// <summary>
    /// PID iterator, call this function every sample period to get the current controller output.
    /// Implements advanced anti-windup, derivative filtering, setpoint ramping, feedforward, output rate limiting, integral separation, deadband, and diagnostics.
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
        lock (_lock)
        {
            // Ensure the timespan is not too small or zero.
            _samplePeriod = ts.TotalSeconds >= _tsMin ? ts.TotalSeconds : _tsMin;
            if (_samplePeriod <= 0 || double.IsNaN(_samplePeriod) || double.IsInfinity(_samplePeriod))
            {
                _samplePeriod = 0.001; // Defensive: never zero or negative
            }

            // Setpoint ramping
            double rampedSetpoint = setPoint;
            bool isFirstCall = double.IsNaN(_lastSetpoint);
            if (!isFirstCall && _setpointRampRate > 0)
            {
                double maxDelta = _setpointRampRate * _samplePeriod;
                rampedSetpoint = Math.Max(_lastSetpoint - maxDelta, Math.Min(_lastSetpoint + maxDelta, setPoint));
            }
            _lastSetpoint = rampedSetpoint;

            // Setpoint weighting for proportional and derivative terms
            double proportionalError = _proportionalSetpointWeight * rampedSetpoint - processValue;
            double error = rampedSetpoint - processValue;
            LastError = error;

            // Deadband flag: if true, we will skip integral and derivative state updates
            bool inDeadband = Math.Abs(error) < _deadband;

            // Integral separation
            bool integrate = !inDeadband && Math.Abs(error) < _integralSeparationBand;

            // Integral term with advanced anti-windup (conditional integration)
            double previousIntegral = _integral;
            if (integrate && Ki > 0)
                _integral += error * _samplePeriod;

            // Derivative on measurement (to avoid derivative kick) with filtering
            double rawDerivative;
            if (isFirstCall)
            {
                _lastProcessValue = processValue;
                _lastDerivative = 0;
                rawDerivative = 0;
            }
            else
            {
                rawDerivative = -(processValue - _lastProcessValue) / _samplePeriod;
                if (double.IsNaN(rawDerivative) || double.IsInfinity(rawDerivative))
                {
                    // Defensive: log and set to 0
                    System.Diagnostics.Debug.WriteLine($"PID: NaN derivative detected. processValue={processValue}, _lastProcessValue={_lastProcessValue}, _samplePeriod={_samplePeriod}");
                    rawDerivative = 0;
                }
            }
            // Dirty derivative filter using N
            double alpha = _samplePeriod / (1.0 / Math.Max(N, double.Epsilon) + _samplePeriod);
            double filteredDerivative = inDeadband
                ? _lastDerivative
                : _lastDerivative + alpha * (rawDerivative - _lastDerivative);
            if (!inDeadband)
            {
                _lastDerivative = filteredDerivative;
            }

            // Bumpless transfer on significant setpoint change (prevents output jump)
            if (!double.IsNaN(_prevSetpointForBumpless) && Math.Abs(rampedSetpoint - _prevSetpointForBumpless) > _bumplessSetpointThreshold && Ki > 0)
            {
                double proportionalTerm = Kp * proportionalError;
                double derivativeTerm = Kd * _lastDerivative;
                double desiredUnclamped = _lastOutput;
                _integral = (desiredUnclamped - _feedforward - proportionalTerm - derivativeTerm) / Ki;
            }
            _prevSetpointForBumpless = rampedSetpoint;

            // Bumpless transfer on gain changes
            if ((Kp != _lastKp || Ki != _lastKi || Kd != _lastKd) && Ki > 0)
            {
                double proportionalTerm = Kp * proportionalError;
                double derivativeTerm = Kd * _lastDerivative;
                double desiredUnclamped = _lastOutput;
                _integral = (desiredUnclamped - _feedforward - proportionalTerm - derivativeTerm) / Ki;
                _lastKp = Kp;
                _lastKi = Ki;
                _lastKd = Kd;
            }

            // PID formula with setpoint weighting and feedforward
            double output = inDeadband
                ? _feedforward
                : Kp * proportionalError + Ki * _integral + Kd * filteredDerivative + _feedforward;

            // Clamp output and apply anti-windup (back calculation)
            double unclampedOutput = output;
            WindupAlarm = false;
            if (output > OutputUpperLimit)
            {
                output = OutputUpperLimit;
                // Back-calculation anti-windup: bleed off integral (unconditional when clamped)
                if (Ki > 0)
                    _integral = previousIntegral + (OutputUpperLimit - unclampedOutput) / Ki;
                WindupAlarm = true;
            }
            else if (output < OutputLowerLimit)
            {
                output = OutputLowerLimit;
                if (Ki > 0)
                    _integral = previousIntegral + (OutputLowerLimit - unclampedOutput) / Ki;
                WindupAlarm = true;
            }

            // Output rate limiting
            if (_maxOutputRate > 0)
            {
                double maxDelta = _maxOutputRate * _samplePeriod;
                output = Math.Max(_lastOutput - maxDelta, Math.Min(_lastOutput + maxDelta, output));
            }

            // Optional output low-pass filtering
            if (_outputFilterTimeConstant > 0)
            {
                double alpha = _samplePeriod / (_outputFilterTimeConstant + _samplePeriod);
                output = _lastFilteredOutput + alpha * (output - _lastFilteredOutput);
                // Re-clamp after filtering to enforce bounds strictly
                output = Math.Max(OutputLowerLimit, Math.Min(OutputUpperLimit, output));
                _lastFilteredOutput = output;
            }

            // Save for next iteration
            _lastProcessValue = processValue; // keep measurement history even in deadband
            _lastOutput = output;

            // Instability alarm: detect oscillation or excessive output
            // Instability alarm relative to the sign-relevant reachable bound
            double bound = output >= 0 ? OutputUpperLimit : OutputLowerLimit;
            InstabilityAlarm = Math.Abs(output) >= 0.95 * Math.Abs(bound);

            return output;
        }
    }

    /// <summary>
    /// Reset controller history effectively resetting the controller.
    /// </summary>
    public void ResetController()
    {
        lock (_lock)
        {
            _integral = 0;
            _lastProcessValue = 0;
            _lastDerivative = 0;
            _lastOutput = 0;
            _lastSetpoint = double.NaN;
            WindupAlarm = false;
            InstabilityAlarm = false;
        }
    }

    // --- Auto-tuning and gain scheduling hooks (not implemented, stub for future) ---
    /// <summary>
    /// Placeholder for auto-tuning algorithm (not implemented).
    /// </summary>
    public void AutoTune() { /* Not implemented */ }
    /// <summary>
    /// Placeholder for gain scheduling (not implemented).
    /// </summary>
    public void GainSchedule() { /* Not implemented */ }

    /// <summary>
    /// Set PID gains at runtime (thread-safe).
    /// </summary>
    public void SetGains(double kp, double ki, double kd)
    {
        lock (_lock)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
        }
    }

    /// <summary>
    /// Set output limits at runtime (thread-safe). Upper must be greater than lower.
    /// </summary>
    public void SetOutputLimits(double upper, double lower)
    {
        if (upper <= lower) throw new ArgumentException("Output upper limit must be greater than lower limit.");
        lock (_lock)
        {
            OutputUpperLimit = upper;
            OutputLowerLimit = lower;
            // Keep last output within new bounds to avoid large jumps
            _lastOutput = Math.Max(OutputLowerLimit, Math.Min(OutputUpperLimit, _lastOutput));
            _lastFilteredOutput = Math.Max(OutputLowerLimit, Math.Min(OutputUpperLimit, _lastFilteredOutput));
        }
    }
}
