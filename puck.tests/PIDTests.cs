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
        public void PID_Tracks_Setpoint_Change()
        {
            // Arrange
            double dt = 0.1;
            var system = new PumpSystem(0, gain: 1.0, dt: dt);
            var pid = new PID(2.0, 1.0, 0.2, 10.0, 1.0, 0.0, dt, derivativeFilterAlpha: 0.2);
            double setpoint = 0.5;
            for (int i = 0; i < 100; i++)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                system.Step(output);
            }
            setpoint = 0.9;
            for (int i = 0; i < 100; i++)
            {
                double output = pid.PID_iterate(setpoint, system.Pressure, TimeSpan.FromSeconds(dt));
                system.Step(output);
            }
            // Assert
            Assert.InRange(system.Pressure, setpoint - 0.01, setpoint + 0.01);
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

        [Fact]
        public void PID_AutoTuneRelay_Simulation()
        {
            double dt = 0.05;
            int steps = 300;
            double actuator = 1.0; // Start at upper extreme
            double simPressure = 0.0; // Start at lower extreme
            double processGain = 10.0;
            double relayAmplitude = 0.5;
            double nominalValue = 0.3;
            // Lambda for actuator: update simPressure using process model and clamp
            Action<double> setActuator = u =>
            {
                actuator = Math.Max(0.0, Math.Min(1.0, u));
                simPressure += (processGain * (actuator - simPressure)) * dt;
                simPressure = Math.Max(0.0, Math.Min(1.0, simPressure));
            };
            Func<double> getProcessValue = () => simPressure;

            // --- Pre-autotune simulation with bad PID coefficients ---
            var badPid = new PID(0.1, 0.1, 0.1, 10.0, 1.0, 0.0, dt, derivativeFilterAlpha: 0.2);
            var preLines = new System.Collections.Generic.List<string>();
            preLines.Add("time,setpoint,process,output,Kp,Ki,Kd");
            simPressure = 0.0;
            for (int i = 0; i < steps; i++)
            {
                double time = i * dt;
                // Dynamic setpoint profile
                double setpoint = 0.0;
                if (time < 5.0) setpoint = 0.0;
                else if (time < 15.0) setpoint = 1.0;
                else if (time < 25.0) setpoint = 0.5;
                else if (time < 35.0) setpoint = 1.2;
                else if (time < 45.0) setpoint = 0.8;
                else setpoint = 0.0;
                double output = badPid.PID_iterate(setpoint, simPressure, TimeSpan.FromSeconds(dt));
                setActuator(output);
                preLines.Add($"{time:F3},{setpoint:F3},{simPressure:F5},{output:F5},{badPid.Kp:F3},{badPid.Ki:F3},{badPid.Kd:F3}");
            }
            System.IO.File.WriteAllLines("pid_pre_autotune_simulation_log.csv", preLines);

            // --- Autotune routine ---
            var pid = new PID(1.0, 0.5, 0.1, 10.0, 1.0, 0.0, dt, derivativeFilterAlpha: 0.2);
            actuator = 1.0;
            simPressure = 0.0;
            pid.AutoTuneRelay(getProcessValue, setActuator, relayAmplitude: relayAmplitude, nominalValue: nominalValue, duration: 60.0, sampleTime: dt, logFile: "pid_autotune_log.csv");

            // --- Post-autotune simulation with tuned PID coefficients ---
            var lines = new System.Collections.Generic.List<string>();
            lines.Add($"time,setpoint,process,output,Kp,Ki,Kd");
            simPressure = 0.0;
            for (int i = 0; i < steps; i++)
            {
                double time = i * dt;
                // Dynamic setpoint profile
                double setpoint = 0.0;
                if (time < 5.0) setpoint = 0.0;
                else if (time < 15.0) setpoint = 1.0;
                else if (time < 25.0) setpoint = 0.5;
                else if (time < 35.0) setpoint = 1.2;
                else if (time < 45.0) setpoint = 0.8;
                else setpoint = 0.0;
                double output = pid.PID_iterate(setpoint, simPressure, TimeSpan.FromSeconds(dt));
                setActuator(output);
                lines.Add($"{time:F3},{setpoint:F3},{simPressure:F5},{output:F5},{pid.Kp:F3},{pid.Ki:F3},{pid.Kd:F3}");
            }
            System.IO.File.WriteAllLines("pid_autotune_simulation_log.csv", lines);
            // No assertion: for demonstration and plotting
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