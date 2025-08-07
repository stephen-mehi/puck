using System;
using System.Collections.Generic;
using System.Linq;

namespace puck.Services.PID
{
    /// <summary>
    /// Parameters for PID simulation.
    /// </summary>
    public class PidSimulationParameters
    {
        public double Setpoint { get; set; }
        public double Kp { get; set; }
        public double Ki { get; set; }
        public double Kd { get; set; }
        public double SimulationTime { get; set; } = 20.0;
        public double TimeStep { get; set; } = 0.1;
        public double InitialProcessValue { get; set; } = 0.0;
        // Add more fields as needed
    }

    /// <summary>
    /// Result of a PID simulation.
    /// </summary>
    public class PidSimulationResult
    {
        public double Cost { get; set; }
        public List<double> ProcessValues { get; set; } = new List<double>();
        public List<double> Outputs { get; set; } = new List<double>();
        // Add more fields as needed
    }

    /// <summary>
    /// Delegate for PID simulation. Takes parameters, returns result.
    /// </summary>
    /// <param name="parameters">Simulation parameters (setpoint, gains, etc.)</param>
    /// <returns>Simulation result (cost, time series, etc.)</returns>
    public delegate PidSimulationResult PidSimulationDelegate(PidSimulationParameters parameters);

    /// <summary>
    /// Genetic algorithm-based PID tuner. Optimizes Kp, Ki, Kd for a given process simulation.
    /// Usage:
    ///   var tuner = new GeneticPidTuner();
    ///   var best = tuner.Tune(simulateProcess, baseParameters);
    /// </summary>
    public class GeneticPidTuner
    {
        private readonly Random _rand = new Random();

        /// <summary>
        /// Tune PID parameters using a genetic algorithm.
        /// </summary>
        /// <param name="simulateProcess">Delegate: simulates process and returns result (cost, etc.)</param>
        /// <param name="baseParameters">Base simulation parameters (setpoint, sim time, etc.)</param>
        /// <param name="generations">Number of generations</param>
        /// <param name="populationSize">Population size</param>
        /// <param name="kpRange">(min, max) for Kp</param>
        /// <param name="kiRange">(min, max) for Ki</param>
        /// <param name="kdRange">(min, max) for Kd</param>
        /// <returns>Tuple of (Kp, Ki, Kd) with lowest cost</returns>
        public (double Kp, double Ki, double Kd) Tune(
            PidSimulationDelegate simulateProcess,
            PidSimulationParameters baseParameters,
            int generations = 50,
            int populationSize = 30,
            (double min, double max)? kpRange = null,
            (double min, double max)? kiRange = null,
            (double min, double max)? kdRange = null)
        {
            kpRange ??= (0.0, 10.0);
            kiRange ??= (0.0, 10.0);
            kdRange ??= (0.0, 10.0);

            // Individual: (Kp, Ki, Kd, cost)
            var population = new List<(double Kp, double Ki, double Kd, double Cost)>();
            for (int i = 0; i < populationSize; i++)
            {
                var ind = (
                    Kp: RandomInRange(kpRange.Value),
                    Ki: RandomInRange(kiRange.Value),
                    Kd: RandomInRange(kdRange.Value),
                    Cost: double.MaxValue
                );
                population.Add(ind);
            }

            for (int gen = 0; gen < generations; gen++)
            {
                // Evaluate
                for (int i = 0; i < population.Count; i++)
                {
                    var ind = population[i];
                    var simParams = CloneParameters(baseParameters);
                    simParams.Kp = ind.Kp;
                    simParams.Ki = ind.Ki;
                    simParams.Kd = ind.Kd;
                    double cost = simulateProcess(simParams).Cost;
                    population[i] = (ind.Kp, ind.Ki, ind.Kd, cost);
                }
                // Sort by cost (lower is better)
                population = population.OrderBy(x => x.Cost).ToList();

                // Elitism: keep top 10%
                int eliteCount = Math.Max(1, populationSize / 10);
                var newPop = new List<(double, double, double, double)>(population.Take(eliteCount));

                // Crossover and mutation to fill the rest
                while (newPop.Count < populationSize)
                {
                    // Tournament selection
                    var parent1 = TournamentSelect(population);
                    var parent2 = TournamentSelect(population);
                    // Crossover
                    var child = Crossover(parent1, parent2);
                    // Mutation
                    child = Mutate(child, kpRange.Value, kiRange.Value, kdRange.Value);
                    child.Cost = double.MaxValue; // will be evaluated next gen
                    newPop.Add(child);
                }
                population = newPop;
            }
            // Final evaluation
            for (int i = 0; i < population.Count; i++)
            {
                var ind = population[i];
                var simParams = CloneParameters(baseParameters);
                simParams.Kp = ind.Kp;
                simParams.Ki = ind.Ki;
                simParams.Kd = ind.Kd;
                double cost = simulateProcess(simParams).Cost;
                population[i] = (ind.Kp, ind.Ki, ind.Kd, cost);
            }
            var best = population.OrderBy(x => x.Cost).First();
            return (best.Kp, best.Ki, best.Kd);
        }

        private double RandomInRange((double min, double max) range)
        {
            return range.min + _rand.NextDouble() * (range.max - range.min);
        }

        private (double Kp, double Ki, double Kd, double Cost) TournamentSelect(List<(double Kp, double Ki, double Kd, double Cost)> pop, int k = 3)
        {
            var selected = new List<(double, double, double, double)>();
            for (int i = 0; i < k; i++)
                selected.Add(pop[_rand.Next(pop.Count)]);
            return selected.OrderBy(x => x.Item4).First();
        }

        private (double Kp, double Ki, double Kd, double Cost) Crossover(
            (double Kp, double Ki, double Kd, double Cost) p1,
            (double Kp, double Ki, double Kd, double Cost) p2)
        {
            // Uniform crossover
            return (
                Kp: _rand.NextDouble() < 0.5 ? p1.Kp : p2.Kp,
                Ki: _rand.NextDouble() < 0.5 ? p1.Ki : p2.Ki,
                Kd: _rand.NextDouble() < 0.5 ? p1.Kd : p2.Kd,
                Cost: double.MaxValue
            );
        }

        private (double Kp, double Ki, double Kd, double Cost) Mutate(
            (double Kp, double Ki, double Kd, double Cost) ind,
            (double min, double max) kpRange,
            (double min, double max) kiRange,
            (double min, double max) kdRange,
            double mutationRate = 0.2,
            double mutationStrength = 0.2)
        {
            double Kp = ind.Kp, Ki = ind.Ki, Kd = ind.Kd;
            if (_rand.NextDouble() < mutationRate)
                Kp = Clamp(Kp + (RandomInRange((-mutationStrength, mutationStrength)) * (kpRange.max - kpRange.min)), kpRange.min, kpRange.max);
            if (_rand.NextDouble() < mutationRate)
                Ki = Clamp(Ki + (RandomInRange((-mutationStrength, mutationStrength)) * (kiRange.max - kiRange.min)), kiRange.min, kiRange.max);
            if (_rand.NextDouble() < mutationRate)
                Kd = Clamp(Kd + (RandomInRange((-mutationStrength, mutationStrength)) * (kdRange.max - kdRange.min)), kdRange.min, kdRange.max);
            return (Kp, Ki, Kd, double.MaxValue);
        }

        private double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

        private PidSimulationParameters CloneParameters(PidSimulationParameters p)
        {
            return new PidSimulationParameters
            {
                Setpoint = p.Setpoint,
                Kp = p.Kp,
                Ki = p.Ki,
                Kd = p.Kd,
                SimulationTime = p.SimulationTime,
                TimeStep = p.TimeStep,
                InitialProcessValue = p.InitialProcessValue
                // Add more fields as needed
            };
        }
    }
}
