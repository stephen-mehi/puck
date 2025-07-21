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
    private readonly double _derivativeSetpointWeight;
    // Derivative filter alpha (0 < alpha < 1)
    private double _derivativeFilterAlpha;
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
    // Soft start (damped start) fields
    private int _softStartSteps = 5;
    private int _softStartCounter = 0;
    private bool _softStartActive = false;
    private double _softStartOutput = 0;
    private double _prevSetpoint = double.NaN;
    private const double _softStartThreshold = 0.01;

    /// <summary>
    /// PID Constructor
    /// </summary>
    /// <param name="kp">Proportional Gain</param>
    /// <param name="ki">Integral Gain</param>
    /// <param name="kd">Derivative Gain</param>
    /// <param name="n">Derivative Filter Coefficient</param>
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
        _derivativeSetpointWeight = derivativeSetpointWeight;
        _derivativeFilterAlpha = derivativeFilterAlpha;
        _maxOutputRate = maxOutputRate;
        _setpointRampRate = setpointRampRate;
        _deadband = deadband;
        _integralSeparationBand = integralSeparationBand;
        _feedforward = 0.0;
        _lastSetpoint = double.NaN;
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
    public double OutputUpperLimit { get; }
    /// <summary>
    /// Lower output limit of the controller
    /// This should obviously be a numerically lesser value than the upper output limit.
    /// </summary>
    public double OutputLowerLimit { get; }

    /// <summary>
    /// Optional feedforward term to be added to the output.
    /// </summary>
    public double Feedforward { get => _feedforward; set => _feedforward = value; }

    /// <summary>
    /// Set the derivative filter alpha (0 < alpha < 1).
    /// </summary>
    public double DerivativeFilterAlpha { get => _derivativeFilterAlpha; set { if (value > 0 && value < 1) _derivativeFilterAlpha = value; } }

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

            // Soft start: detect setpoint change
            if (!double.IsNaN(_prevSetpoint) && Math.Abs(setPoint - _prevSetpoint) > _softStartThreshold)
            {
                _softStartActive = true;
                _softStartCounter = _softStartSteps;
                _softStartOutput = _lastOutput;
            }
            _prevSetpoint = setPoint;

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

            // Deadband: if error is within deadband, output is zero (plus feedforward), and do not update integral/derivative
            if (Math.Abs(error) < _deadband)
            {
                // Do not update integral or derivative
                return _feedforward;
            }

            // Integral separation
            bool integrate = Math.Abs(error) < _integralSeparationBand;

            // Integral term with advanced anti-windup (conditional integration)
            double previousIntegral = _integral;
            if (integrate)
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
            double filteredDerivative = _derivativeFilterAlpha * rawDerivative + (1 - _derivativeFilterAlpha) * _lastDerivative;
            _lastDerivative = filteredDerivative;

            // PID formula with setpoint weighting and feedforward
            double output = Kp * proportionalError + Ki * _integral + Kd * filteredDerivative + _feedforward;

            // Clamp output and apply anti-windup (back calculation)
            double unclampedOutput = output;
            WindupAlarm = false;
            if (output > OutputUpperLimit)
            {
                output = OutputUpperLimit;
                // Back-calculation anti-windup: bleed off integral
                if (integrate)
                    _integral = previousIntegral + (OutputUpperLimit - unclampedOutput) / Ki;
                WindupAlarm = true;
            }
            else if (output < OutputLowerLimit)
            {
                output = OutputLowerLimit;
                if (integrate)
                    _integral = previousIntegral + (OutputLowerLimit - unclampedOutput) / Ki;
                WindupAlarm = true;
            }

            // Soft start: damp output after setpoint change
            if (_softStartActive && _softStartCounter > 0)
            {
                double alpha = 1.0 / _softStartCounter;
                output = (1 - alpha) * _softStartOutput + alpha * output;
                _softStartOutput = output;
                _softStartCounter--;
                if (_softStartCounter <= 0)
                    _softStartActive = false;
            }

            // Output rate limiting
            if (_maxOutputRate > 0)
            {
                double maxDelta = _maxOutputRate * _samplePeriod;
                output = Math.Max(_lastOutput - maxDelta, Math.Min(_lastOutput + maxDelta, output));
            }

            // Save for next iteration
            _lastProcessValue = processValue;
            _lastOutput = output;

            // Instability alarm: detect oscillation or excessive output
            InstabilityAlarm = Math.Abs(output) >= 0.95 * Math.Max(Math.Abs(OutputUpperLimit), Math.Abs(OutputLowerLimit));

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
    /// Relay-based auto-tuning for PID. Applies relay (on-off) control to the actuator, records process response,
    /// estimates ultimate gain and period, and sets PID gains using Ziegler-Nichols rules.
    /// </summary>
    /// <param name="getProcessValue">Delegate to get the current process value (e.g., pressure)</param>
    /// <param name="setActuator">Delegate to set the actuator (e.g., pump speed)</param>
    /// <param name="relayAmplitude">Amplitude of relay (fraction of actuator range, e.g., 0.2 for 20%)</param>
    /// <param name="nominalValue">Nominal actuator value to oscillate around (e.g., 0.5 for 50%)</param>
    /// <param name="duration">Total duration of auto-tune (seconds)</param>
    /// <param name="sampleTime">Sample time (seconds)</param>
    /// <param name="logFile">Optional: file to log data for analysis</param>
    public void AutoTuneRelay(
        Func<double> getProcessValue,
        Action<double> setActuator,
        double relayAmplitude = 0.2,
        double nominalValue = 0.5,
        double duration = 60.0,
        double sampleTime = 0.1,
        string? logFile = null)
    {
        var log = new System.Collections.Generic.List<string>();
        log.Add("time,actuator,process");
        var processValues = new System.Collections.Generic.List<double>();
        var actuatorValues = new System.Collections.Generic.List<double>();
        var times = new System.Collections.Generic.List<double>();
        double t = 0;
        double actuator = nominalValue + relayAmplitude;
        setActuator(actuator);
        bool relayState = true; // true = high, false = low
        double hysteresis = 0.01; // Small band to prevent chattering
        // Ensure initial process value is offset from nominal value for first switch
        // (Document: user should start process above or below nominal value)
        int switchCount = 0;
        var switchTimes = new System.Collections.Generic.List<double>();
        var switchValues = new System.Collections.Generic.List<double>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (t < duration)
        {
            double processValue = getProcessValue();
            log.Add($"{t:F3},{actuator:F5},{processValue:F5}");
            processValues.Add(processValue);
            actuatorValues.Add(actuator);
            times.Add(t);
            // Relay logic: switch actuator when process crosses nominal value +/- hysteresis
            if (relayState && processValue > nominalValue + hysteresis)
            {
                relayState = false;
                actuator = nominalValue - relayAmplitude;
                setActuator(actuator);
                switchTimes.Add(t);
                switchValues.Add(processValue);
                switchCount++;
                System.Diagnostics.Debug.WriteLine($"[Relay Switch] t={t:F2}, actuator={actuator:F3}, process={processValue:F3} (HIGH->LOW)");
            }
            else if (!relayState && processValue < nominalValue - hysteresis)
            {
                relayState = true;
                actuator = nominalValue + relayAmplitude;
                setActuator(actuator);
                switchTimes.Add(t);
                switchValues.Add(processValue);
                switchCount++;
                System.Diagnostics.Debug.WriteLine($"[Relay Switch] t={t:F2}, actuator={actuator:F3}, process={processValue:F3} (LOW->HIGH)");
            }
            System.Diagnostics.Debug.WriteLine($"[Relay Step] t={t:F2}, actuator={actuator:F3}, process={processValue:F3}");
            System.Threading.Thread.Sleep((int)(sampleTime * 1000));
            t += sampleTime;
        }
        setActuator(nominalValue); // Restore actuator
        stopwatch.Stop();
        // Analyze oscillation: use last few cycles
        int nCycles = (switchTimes.Count - 1) / 2;
        if (nCycles < 2)
            throw new Exception("Auto-tune failed: not enough oscillations detected.");
        // Period: average time between every other switch (full cycle)
        double periodSum = 0;
        int periodCount = 0;
        for (int i = switchTimes.Count - 2 * nCycles; i < switchTimes.Count - 2; i += 2)
        {
            periodSum += switchTimes[i + 2] - switchTimes[i];
            periodCount++;
        }
        double Pu = periodSum / periodCount;
        // Amplitude: average peak-to-peak process value
        double ampSum = 0;
        int ampCount = 0;
        for (int i = switchValues.Count - 2 * nCycles; i < switchValues.Count - 2; i += 2)
        {
            ampSum += Math.Abs(switchValues[i + 1] - switchValues[i]);
            ampCount++;
        }
        double a = ampSum / ampCount / 2.0; // amplitude (half peak-to-peak)
        // Ultimate gain
        double Ku = (4 * relayAmplitude) / (Math.PI * a);
        // Ziegler-Nichols PID tuning
        double Kp = 0.6 * Ku;
        double Ti = 0.5 * Pu;
        double Td = 0.125 * Pu;
        // Set new gains (thread-safe)
        SetGains(Kp, Ti, Td);
        if (logFile != null)
            System.IO.File.WriteAllLines(logFile, log);
    }
}
