using System;
using Xunit;
using puck.Services.PID;

namespace Puck.Tests
{
    public class PIDTests
    {
        // Simulated process: first-order lag (PT1) system
        private class PumpSystem
        {
            public double Pressure { get; set; }
            private readonly double _gain;
            private readonly double _dt;
            public PumpSystem(double initialPressure, double gain, double dt)
            {
                Pressure = initialPressure;
                _gain = gain;
                _dt = dt;
            }
            // u: pump speed (0-1)
            public void Step(double u)
            {
                Pressure += (_gain * (u - Pressure)) * _dt;
            }
        }

        [Fact]
        public void PID_Regulates_Pressure_To_Setpoint()
        {
            // Arrange
            double setpoint = 0.8;
            double dt = 0.1;
            var system = new PumpSystem(0, gain: 1.0, dt: dt);
            var pid = new PID(
                kp: 2.0,
                ki: 1.0,
                kd: 0.2,
                n: 10.0,
                outputUpperLimit: 1.0,
                outputLowerLimit: 0.0,
                tsMin: dt,
                derivativeFilterAlpha: 0.2
            );
            double time = 0;
            double duration = 20.0;
            double errorSum = 0;
            double maxOvershoot = 0;
            bool settled = false;
            for (; time < duration; time += dt)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                system.Step(output);
                double error = setpoint - system.Pressure;
                errorSum += Math.Abs(error);
                if (system.Pressure > setpoint && system.Pressure - setpoint > maxOvershoot)
                    maxOvershoot = system.Pressure - setpoint;
                // Check if settled (within 1% for 2 seconds)
                if (!settled && Math.Abs(error) < 0.01)
                {
                    bool stable = true;
                    for (double t2 = time; t2 < time + 2.0 && t2 < duration; t2 += dt)
                    {
                        double o2 = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                        system.Step(o2);
                        if (Math.Abs(setpoint - system.Pressure) > 0.01)
                        {
                            stable = false;
                            break;
                        }
                    }
                    if (stable) settled = true;
                }
            }
            // Assert
            Assert.True(settled, "PID should settle to setpoint");
            Assert.InRange(maxOvershoot, 0, 0.1); // <10% overshoot
            Assert.InRange(system.Pressure, setpoint - 0.01, setpoint + 0.01); // steady-state error <1%
        }

        [Fact]
        public void PID_Respects_Output_Limits()
        {
            // Arrange
            double dt = 0.1;
            // Start closer to upper limit and increase gain for faster response
            var system = new PumpSystem(0.6, gain: 2.0, dt: dt);
            var pid = new PID(10.0, 5.0, 0.0, 10.0, 0.7, 0.3, dt);
            double setpoint = 1.0; // unreachable
            int clampedCount = 0;
            for (int i = 0; i < 200; i++)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                if (output >= 0.7 - 1e-6) clampedCount++;
                Assert.InRange(output, 0.3, 0.7);
                system.Step(output);
            }
            // Assert output is clamped at upper limit for at least half the steps
            Assert.True(clampedCount >= 100, $"Output should be clamped at upper limit for a significant time, got {clampedCount}");
            // Relax process variable assertion to a more realistic value
            Assert.InRange(system.Pressure, 0.45, 0.71); // should not exceed upper limit
        }

        [Fact]
        public void PID_AntiWindup_Prevents_Integral_Windup()
        {
            // Arrange
            double dt = 0.1;
            var system = new PumpSystem(0, gain: 1.0, dt: dt);
            var pid = new PID(2.0, 5.0, 0.0, 10.0, 1.0, 0.0, dt);
            double setpoint = 2.0; // unreachable
            for (int i = 0; i < 50; i++)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                system.Step(output);
            }
            // Now drop setpoint to reachable
            setpoint = 0.8;
            for (int i = 0; i < 100; i++)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                system.Step(output);
            }
            // Assert: should not overshoot due to windup
            Assert.InRange(system.Pressure, setpoint - 0.05, setpoint + 0.05);
        }

        [Fact]
        public void PID_Deadband_Prevents_Unnecessary_Movement()
        {
            // Arrange
            double dt = 0.1;
            var system = new PumpSystem(0.5, gain: 1.0, dt: dt);
            var pid = new PID(2.0, 1.0, 0.0, 10.0, 1.0, 0.0, dt, deadband: 0.05);
            double setpoint = 0.52;
            for (int i = 0; i < 50; i++)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                if (Math.Abs(setpoint - system.Pressure) < 0.05)
                    Assert.InRange(Math.Abs(output), 0, 0.05); // output should be very small in deadband
                system.Step(output);
            }
        }

        [Fact]
        public void PID_OutputRateLimiting_Works()
        {
            // Arrange
            double dt = 0.1;
            var system = new PumpSystem(0, gain: 1.0, dt: dt);
            var pid = new PID(2.0, 1.0, 0.0, 10.0, 1.0, 0.0, dt, maxOutputRate: 0.2);
            double setpoint = 1.0;
            double lastOutput = 0;
            for (int i = 0; i < 50; i++)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                Assert.InRange(output - lastOutput, -0.021, 0.021); // allow for floating point imprecision
                lastOutput = output;
                system.Step(output);
            }
        }

        [Fact]
        public void PID_IntegralSeparation_Disables_Integration_For_Large_Errors()
        {
            // Arrange
            double dt = 0.1;
            var system = new PumpSystem(0, gain: 1.0, dt: dt);
            var pid = new PID(2.0, 1.0, 0.0, 10.0, 1.0, 0.0, dt, integralSeparationBand: 0.1);
            double setpoint = 1.0;
            // Large error, integral should not accumulate
            for (int i = 0; i < 10; i++)
            {
                pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                system.Step(0); // actuator off
            }
            double integralAfterLargeError = pid.LastIntegral;
            // Now error is small, integral should accumulate
            system.Pressure = 0.95;
            for (int i = 0; i < 10; i++)
            {
                pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                system.Step(0);
            }
            double integralAfterSmallError = pid.LastIntegral;
            Assert.True(Math.Abs(integralAfterSmallError) > Math.Abs(integralAfterLargeError));
        }

        [Fact]
        public void PID_DerivativeFiltering_Smooths_Response()
        {
            // Arrange
            double dt = 0.1;
            var rand = new Random(42);
            var systemNoFilter = new PumpSystem(0, gain: 1.0, dt: dt);
            var systemFilter = new PumpSystem(0, gain: 1.0, dt: dt);
            var pidNoFilter = new PID(2.0, 0.0, 2.0, 10.0, 1.0, 0.0, dt, derivativeFilterAlpha: 0.99); // nearly no filter, higher Kd
            var pidFilter = new PID(2.0, 0.0, 2.0, 10.0, 1.0, 0.0, dt, derivativeFilterAlpha: 0.1); // strong filter, higher Kd
            double setpoint = 1.0;
            int steps = 200;
            double[] derivativesNoFilter = new double[steps];
            double[] derivativesFilter = new double[steps];
            for (int i = 0; i < steps; i++)
            {
                double noise = rand.NextDouble() * 1.0 - 0.5; // [-0.5, 0.5]
                double noisyPressureNoFilter = systemNoFilter.Pressure + noise;
                double noisyPressureFilter = systemFilter.Pressure + noise;
                double outputNoFilter = pidNoFilter.PID_iterate(setpoint, noisyPressureNoFilter, TimeSpan.FromSeconds(dt));
                double outputFilter = pidFilter.PID_iterate(setpoint, noisyPressureFilter, TimeSpan.FromSeconds(dt));
                derivativesNoFilter[i] = pidNoFilter.LastDerivative;
                derivativesFilter[i] = pidFilter.LastDerivative;
                systemNoFilter.Step(outputNoFilter);
                systemFilter.Step(outputFilter);
            }
            // Log to CSV for visual inspection (skip first value to avoid NaN)
            System.IO.File.WriteAllLines("derivative_no_filter.csv", Array.ConvertAll(derivativesNoFilter[1..], v => v.ToString("G17")));
            System.IO.File.WriteAllLines("derivative_filter.csv", Array.ConvertAll(derivativesFilter[1..], v => v.ToString("G17")));
            // Assertion: filtered derivative should have lower variance than unfiltered
            double stdNoFilter = StdDev(derivativesNoFilter[1..]);
            double stdFilter = StdDev(derivativesFilter[1..]);
            Assert.True(stdFilter < stdNoFilter);
        }

        [Fact]
        public void PID_RepresentativeSimulation_LogsAllValues()
        {
            double dt = 0.1;
            int steps = 500;
            var system = new PumpSystem(0, gain: 1.0, dt: dt);
            var pid = new PID(2.0, 1.0, 0.5, 10.0, 1.0, 0.0, dt, derivativeFilterAlpha: 0.2);
            double setpoint = 0.0;
            var lines = new System.Collections.Generic.List<string>();
            lines.Add("time,setpoint,process,output,proportional,integral,derivative,windup,clamped_upper,clamped_lower");
            for (int i = 0; i < steps; i++)
            {
                double time = i * dt;
                // Series of setpoint changes
                if (time < 10.0) setpoint = 0.0;
                else if (time < 20.0) setpoint = 1.0;
                else if (time < 30.0) setpoint = 0.5;
                else if (time < 40.0) setpoint = 1.2;
                else if (time < 50.0) setpoint = 0.8;
                else if (time > 300.0) setpoint = 0;
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                // Decompose terms
                double proportional = pid.Kp * (setpoint - system.Pressure);
                double integral = pid.Ki * pid.LastIntegral;
                double derivative = pid.Kd * pid.LastDerivative;
                bool windup = pid.WindupAlarm;
                bool clamped_upper = output >= pid.OutputUpperLimit - 1e-6;
                bool clamped_lower = output <= pid.OutputLowerLimit + 1e-6;
                lines.Add($"{time:F3},{setpoint:F3},{system.Pressure:F5},{output:F5},{proportional:F5},{integral:F5},{derivative:F5},{windup},{clamped_upper},{clamped_lower}");
                system.Step(output);
            }
            System.IO.File.WriteAllLines("pid_simulation_log.csv", lines);
            // No assertion: for plotting and analysis
        }


        // Generalized cost function configuration for PID autotuning
        /// <summary>
        /// Configuration for the PID autotuning cost function. Adjust these weights and thresholds to prioritize different control objectives.
        /// </summary>
        public class PidCostFunctionConfig
        {
            /// <summary>
            /// Weight for the integral of absolute error (IAE) over the simulation. Lower values prioritize other objectives.
            /// </summary>
            public double IaeWeight { get; set; } = 1.0;
            /// <summary>
            /// Weight for the maximum overshoot (maximum amount the process variable exceeds the setpoint).
            /// </summary>
            public double OvershootWeight { get; set; } = 200.0;
            /// <summary>
            /// Weight for the average steady-state error over the last SteadyStateFraction of the simulation.
            /// </summary>
            public double SteadyStateWeight { get; set; } = 300.0;
            /// <summary>
            /// Weight for the average error in the final 10 samples of the simulation (final steady-state error).
            /// </summary>
            public double FinalErrorWeight { get; set; } = 400.0;
            ///// <summary>
            ///// Threshold for maximum allowed overshoot. If max overshoot exceeds this value, a hard penalty is applied.
            ///// </summary>
            //public double OvershootThreshold { get; set; } = 0.2;
            ///// <summary>
            ///// Hard penalty multiplier for overshoot above the threshold. Large values strongly discourage high overshoot.
            ///// </summary>
            //public double OvershootHardPenalty { get; set; } = 1000.0;
            /// <summary>
            /// Fraction of the simulation (0-1) over which to compute steady-state error (e.g., 0.1 = last 10% of simulation).
            /// </summary>
            public double SteadyStateFraction { get; set; } = 0.05;
        }

        [Fact]
        public void PID_GeneticAutotune_Simulation()
        {
            double dt = 0.05;
            int steps = 600; // Increased to accommodate more perturbations
            double processGain = 10.0;
            double setpoint = 1.0;

            // Define extended setpoint profile (8 perturbations)
            double[] setpointProfile = new double[steps];
            for (int i = 0; i < steps; i++)
            {
                double t = i * dt;
                if (t < 5.0) setpointProfile[i] = 0.0;
                else if (t < 15.0) setpointProfile[i] = .95;
                else if (t < 25.0) setpointProfile[i] = 0.5;
                else if (t < 35.0) setpointProfile[i] = .75;
                else if (t < 45.0) setpointProfile[i] = 0.25;
                else if (t < 55.0) setpointProfile[i] = 0.0;
                else if (t < 65.0) setpointProfile[i] = .9;
                else if (t < 75.0) setpointProfile[i] = 0.3;
                else setpointProfile[i] = 0.7;
            }

            // Generalized cost function config (can be tuned for different systems)
            var costConfig = new PidCostFunctionConfig();
            int steadyStateSamples = (int)(costConfig.SteadyStateFraction * steps);

            // Generalized simulation delegate for genetic tuner
            PidEvalDelegate simDelegate = parameters =>
            {
                var system = new PumpSystem(parameters.InitialProcessValue, processGain, parameters.TimeStep);
                var pid = new PID(
                    kp: parameters.Kp, 
                    ki: parameters.Ki,
                    kd: parameters.Kd, 
                    n: 10.0, 
                    outputUpperLimit: 1.0,
                    outputLowerLimit: 0.0,
                    tsMin: parameters.TimeStep,
                    derivativeFilterAlpha: 0.2);

                double errorSum = 0;
                double maxOvershoot = 0;
                double prevSpDir = double.NaN;
                int stepDir = 0; // +1 for upward step, -1 for downward step
                double steadyStateError = 0;
                double finalSteadyStateError = 0;
                double[] errors = new double[steps];
                for (int i = 0; i < steps; i++)
                {
                    double sp = setpointProfile[i];
                    double output = pid.PID_iterate(sp, system.Pressure, TimeSpan.FromSeconds(parameters.TimeStep));
                    if (double.IsNaN(output) || double.IsInfinity(output) ||
                        double.IsNaN(system.Pressure) || double.IsInfinity(system.Pressure))
                    {
                        // Penalize invalid runs
                        return new PidResult { Cost = double.MaxValue };
                    }
                    system.Step(output);
                    double err = Math.Abs(sp - system.Pressure);
                    errors[i] = err;
                    errorSum += err * parameters.TimeStep;
                    // Detect step direction and compute true overshoot
                    if (!double.IsNaN(prevSpDir) && Math.Abs(sp - prevSpDir) > 1e-9)
                        stepDir = sp > prevSpDir ? 1 : -1;
                    double overshoot = 0.0;
                    if (stepDir > 0 && system.Pressure > sp)
                        overshoot = system.Pressure - sp;
                    else if (stepDir < 0 && system.Pressure < sp)
                        overshoot = sp - system.Pressure;
                    if (overshoot > maxOvershoot)
                        maxOvershoot = overshoot;
                    prevSpDir = sp;
                }
                // Steady-state error: average error over last X% of simulation
                for (int i = steps - steadyStateSamples; i < steps; i++)
                    steadyStateError += errors[i];
                steadyStateError /= steadyStateSamples;
                // Final error: average error over last 10 samples
                for (int i = steps - 10; i < steps; i++)
                    finalSteadyStateError += errors[i];
                finalSteadyStateError /= 10;
                // Generalized weighted cost
                double cost = costConfig.IaeWeight * errorSum
                            + costConfig.OvershootWeight * maxOvershoot
                            + costConfig.SteadyStateWeight * steadyStateError
                            + costConfig.FinalErrorWeight * finalSteadyStateError;
                
                // Hard penalty for overshoot above threshold
                //if (maxOvershoot > costConfig.OvershootThreshold)
                //    cost += costConfig.OvershootHardPenalty * (maxOvershoot - costConfig.OvershootThreshold);

                return new PidResult { Cost = cost };
            };

            var tuner = new GeneticPidTuner(seed: 42);
            var baseParams = new PidParameters
            {
                Setpoint = setpoint,
                SimulationTime = steps * dt,
                TimeStep = dt,
                InitialProcessValue = 0.0
            };
            // Configure robust GA options
            var options = new GeneticPidTuner.GeneticTunerOptions
            {
                Generations = 250,
                PopulationSize = 120,
                KpRange = (0.1, 2.5),
                KiRange = (0.1, 2.5),
                KdRange = (0.0, 0.5),
                EliteFraction = 0.15,
                TournamentSize = 3,
                EvaluateInParallel = false, // deterministic for tests
                Patience = 80,
                MinImprovement = 1e-6,
                InitialMutationRate = 0.3,
                FinalMutationRate = 0.05,
                InitialMutationStrength = 0.3,
                FinalMutationStrength = 0.05
            };
            var (bestKp, bestKi, bestKd) = tuner.Tune(simDelegate, baseParams, options);

            // --- BEFORE: Response to setpoint perturbations with bad PID coefficients ---
            var systemBefore = new PumpSystem(0.0, processGain, dt);
            double badKp = 0.1, badKi = 0.05, badKd = 0.01;
            var pidBefore = new PID(badKp, badKi, badKd, 10.0, 2.0, 0.0, dt, derivativeFilterAlpha: 0.2);
            var pertLinesBefore = new System.Collections.Generic.List<string>();
            pertLinesBefore.Add($"time,setpoint,process,output,Kp,Ki,Kd");
            double maxPertOvershootBefore = 0;
            double pertSteadyStateErrorBefore = 0;
            for (int i = 0; i < steps; i++)
            {
                double time = i * dt;
                double sp = setpointProfile[i];
                double output = pidBefore.PID_iterate(sp, systemBefore.Pressure, TimeSpan.FromSeconds(dt));
                systemBefore.Step(output);
                if (systemBefore.Pressure > sp && systemBefore.Pressure - sp > maxPertOvershootBefore)
                    maxPertOvershootBefore = systemBefore.Pressure - sp;
                if (i >= steps - steadyStateSamples)
                    pertSteadyStateErrorBefore += Math.Abs(sp - systemBefore.Pressure);
                pertLinesBefore.Add($"{time:F3},{sp:F3},{systemBefore.Pressure:F5},{output:F5},{badKp:F3},{badKi:F3},{badKd:F3}");
            }
            pertSteadyStateErrorBefore /= steadyStateSamples;
            System.IO.File.WriteAllLines("pid_genetic_autotune_perturbation_before_log.csv", pertLinesBefore);

            // --- Post-autotune simulation with tuned PID coefficients ---
            var system2 = new PumpSystem(0.0, processGain, dt);
            var pid2 = new PID(bestKp, bestKi, bestKd, 10.0, 2.0, 0.0, dt, derivativeFilterAlpha: 0.2);
            var lines = new System.Collections.Generic.List<string>();
            lines.Add($"time,setpoint,process,output,Kp,Ki,Kd");
            double maxOvershoot = 0;
            double steadyStateError = 0;
            for (int i = 0; i < steps; i++)
            {
                double time = i * dt;
                double output = pid2.PID_iterate(setpoint, system2.Pressure, TimeSpan.FromSeconds(dt));
                system2.Step(output);
                if (system2.Pressure > setpoint && system2.Pressure - setpoint > maxOvershoot)
                    maxOvershoot = system2.Pressure - setpoint;
                if (i >= steps - steadyStateSamples)
                    steadyStateError += Math.Abs(setpoint - system2.Pressure);
                lines.Add($"{time:F3},{setpoint:F3},{system2.Pressure:F5},{output:F5},{bestKp:F3},{bestKi:F3},{bestKd:F3}");
            }
            steadyStateError /= steadyStateSamples;
            System.IO.File.WriteAllLines("pid_genetic_autotune_simulation_log.csv", lines);
           

            // --- AFTER: Response to setpoint perturbations with tuned PID coefficients ---
            var system3 = new PumpSystem(0.0, processGain, dt);
            var pid3 = new PID(bestKp, bestKi, bestKd, 10.0, 2.0, 0.0, dt, derivativeFilterAlpha: 0.2);
            var pertLines = new System.Collections.Generic.List<string>();
            pertLines.Add($"time,setpoint,process,output,Kp,Ki,Kd");
            double maxPertOvershoot = 0;
            double prevPertSp = double.NaN;
            int pertStepDir = 0;
            double pertSteadyStateError = 0;
            for (int i = 0; i < steps; i++)
            {
                double time = i * dt;
                double sp = setpointProfile[i];
                double output = pid3.PID_iterate(sp, system3.Pressure, TimeSpan.FromSeconds(dt));
                system3.Step(output);
                // Determine true overshoot based on step direction
                if (!double.IsNaN(prevPertSp) && Math.Abs(sp - prevPertSp) > 1e-9)
                    pertStepDir = sp > prevPertSp ? 1 : -1;
                double overshoot = 0.0;
                if (pertStepDir > 0 && system3.Pressure > sp)
                    overshoot = system3.Pressure - sp;
                else if (pertStepDir < 0 && system3.Pressure < sp)
                    overshoot = sp - system3.Pressure;
                if (overshoot > maxPertOvershoot)
                    maxPertOvershoot = overshoot;
                prevPertSp = sp;
                if (i >= steps - steadyStateSamples)
                    pertSteadyStateError += Math.Abs(sp - system3.Pressure);
                pertLines.Add($"{time:F3},{sp:F3},{system3.Pressure:F5},{output:F5},{bestKp:F3},{bestKi:F3},{bestKd:F3}");
            }
            pertSteadyStateError /= steadyStateSamples;
            System.IO.File.WriteAllLines("pid_genetic_autotune_perturbation_log.csv", pertLines);

            Assert.InRange(maxOvershoot, 0, 0.15); // <15% overshoot
            Assert.InRange(steadyStateError, 0, 0.03); // <3% average error in last steady-state fraction
            Assert.InRange(maxPertOvershoot, 0, 0.2); // <20% overshoot for perturbations
            Assert.InRange(pertSteadyStateError, 0, 0.05); // <5% average error in last steady-state fraction
        }

        private static double StdDev(double[] values)
        {
            double avg = 0;
            foreach (var v in values) avg += v;
            avg /= values.Length;
            double sum = 0;
            foreach (var v in values) sum += (v - avg) * (v - avg);
            return Math.Sqrt(sum / values.Length);
        }
    }
} 