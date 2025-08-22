using System;
using System.Collections.Generic;
using System.Linq;

namespace puck.Services.PID
{
    /// <summary>
    /// Parameters for PID simulation.
    /// </summary>
    public class PidParameters
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
    /// Result of a PID
    /// </summary>
    public class PidResult
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
    public delegate Task<PidResult> PidEvalDelegate(PidParameters parameters, CancellationToken ct);

    public class GeneticTunerOptions
    {
        public int Generations { get; set; } = 50;
        public int PopulationSize { get; set; } = 30;
        public (double min, double max) KpRange { get; set; } = (0.0, 10.0);
        public (double min, double max) KiRange { get; set; } = (0.0, 10.0);
        public (double min, double max) KdRange { get; set; } = (0.0, 10.0);
        public double EliteFraction { get; set; } = 0.1; // 10%
        public int TournamentSize { get; set; } = 3;
        public bool EvaluateInParallel { get; set; } = false;
        public int Patience { get; set; } = 15; // early stop patience (generations)
        public double MinImprovement { get; set; } = 1e-6; // relative improvement threshold
        public double InitialMutationRate { get; set; } = 0.3;
        public double FinalMutationRate { get; set; } = 0.05;
        public double InitialMutationStrength { get; set; } = 0.3; // fraction of range
        public double FinalMutationStrength { get; set; } = 0.05;
        // Optional per-generation progress hook: (generation, best (Kp,Ki,Kd,Cost))
        public Action<int, (double Kp, double Ki, double Kd, double Cost)>? OnGenerationEvaluated { get; set; }
    }

    /// <summary>
    /// Genetic algorithm-based PID tuner. Optimizes Kp, Ki, Kd for a given process simulation.
    /// Usage:
    ///   var tuner = new GeneticPidTuner();
    ///   var best = tuner.Tune(simulateProcess, baseParameters);
    /// </summary>
    public class GeneticPidTuner
    {
        private readonly Random _rand;

        public GeneticPidTuner(int? seed = null)
        {
            _rand = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        public async Task<(double Kp, double Ki, double Kd)> TuneAsync(
            PidEvalDelegate simulateProcess,
            PidParameters baseParameters,
            GeneticTunerOptions options,
            CancellationToken ct)
        {
            // Individual: (Kp, Ki, Kd, cost)
            var population = new List<(double Kp, double Ki, double Kd, double Cost)>();
            for (int i = 0; i < options.PopulationSize; i++)
            {
                var ind = (
                    Kp: RandomInRange(options.KpRange),
                    Ki: RandomInRange(options.KiRange),
                    Kd: RandomInRange(options.KdRange),
                    Cost: double.MaxValue
                );
                population.Add(ind);
            }

            double bestCost = double.MaxValue;
            int stagnantGenerations = 0;

            for (int gen = 0; gen < options.Generations; gen++)
            {
                // Adaptive mutation schedule
                double t = options.Generations > 1 ? (double)gen / (options.Generations - 1) : 1.0;
                double mutationRate = Lerp(options.InitialMutationRate, options.FinalMutationRate, t);
                double mutationStrength = Lerp(options.InitialMutationStrength, options.FinalMutationStrength, t);

                // Evaluate
                await EvaluatePopulationAsync(population, simulateProcess, baseParameters, options, ct);
                population = population.OrderBy(x => x.Cost).ToList();
                options.OnGenerationEvaluated?.Invoke(gen, population[0]);

                // Early stopping check
                double currentBest = population[0].Cost;
                if (bestCost == double.MaxValue || RelativeImprovement(bestCost, currentBest) > options.MinImprovement)
                {
                    bestCost = currentBest;
                    stagnantGenerations = 0;
                }
                else
                {
                    stagnantGenerations++;
                    if (stagnantGenerations >= options.Patience)
                        break;
                }

                // Elitism
                int eliteCount = Math.Max(1, (int)Math.Round(options.EliteFraction * options.PopulationSize));
                eliteCount = Math.Min(eliteCount, options.PopulationSize - 1);
                var newPop = new List<(double, double, double, double)>(population.Take(eliteCount));

                // Fill the rest
                while (newPop.Count < options.PopulationSize)
                {
                    var parent1 = TournamentSelect(population, options.TournamentSize);
                    var parent2 = TournamentSelect(population, options.TournamentSize);
                    var child = ArithmeticCrossover(parent1, parent2);
                    child = Mutate(child, options.KpRange, options.KiRange, options.KdRange, mutationRate, mutationStrength);
                    child.Cost = double.MaxValue;
                    newPop.Add(child);
                }
                population = newPop;
            }

            // Final evaluation and result
            await EvaluatePopulationAsync(population, simulateProcess, baseParameters, options, ct);
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

        private (double Kp, double Ki, double Kd, double Cost) ArithmeticCrossover(
            (double Kp, double Ki, double Kd, double Cost) p1,
            (double Kp, double Ki, double Kd, double Cost) p2)
        {
            // Blend parents with random alpha per gene for smoother search
            double a1 = _rand.NextDouble();
            double a2 = _rand.NextDouble();
            double a3 = _rand.NextDouble();
            return (
                Kp: a1 * p1.Kp + (1 - a1) * p2.Kp,
                Ki: a2 * p1.Ki + (1 - a2) * p2.Ki,
                Kd: a3 * p1.Kd + (1 - a3) * p2.Kd,
                Cost: double.MaxValue
            );
        }

        private (double Kp, double Ki, double Kd, double Cost) Mutate(
            (double Kp, double Ki, double Kd, double Cost) ind,
            (double min, double max) kpRange,
            (double min, double max) kiRange,
            (double min, double max) kdRange,
            double mutationRate,
            double mutationStrength)
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

        private PidParameters CloneParameters(PidParameters p)
        {
            return new PidParameters
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

        private async Task EvaluatePopulationAsync(
            List<(double Kp, double Ki, double Kd, double Cost)> population,
            PidEvalDelegate simulateProcess,
            PidParameters baseParameters,
            GeneticTunerOptions options,
            CancellationToken ct)
        {
            if (options.EvaluateInParallel)
            {
                var tasks = new List<Task>();
                for (int i = 0; i < population.Count; i++)
                {
                    int idx = i;
                    tasks.Add(Task.Run(async () =>
                    {
                        var ind = population[idx];
                        var simParams = CloneParameters(baseParameters);
                        simParams.Kp = ind.Kp;
                        simParams.Ki = ind.Ki;
                        simParams.Kd = ind.Kd;
                        double cost = (await simulateProcess(simParams, ct)).Cost;
                        if (double.IsNaN(cost) || double.IsInfinity(cost)) cost = double.MaxValue;
                        population[idx] = (ind.Kp, ind.Ki, ind.Kd, cost);
                    }));
                }
                await Task.WhenAll(tasks);
            }
            else
            {
                for (int i = 0; i < population.Count; i++)
                {
                    var ind = population[i];
                    var simParams = CloneParameters(baseParameters);
                    simParams.Kp = ind.Kp;
                    simParams.Ki = ind.Ki;
                    simParams.Kd = ind.Kd;
                    double cost = (await simulateProcess(simParams, ct)).Cost;
                    if (double.IsNaN(cost) || double.IsInfinity(cost)) cost = double.MaxValue;
                    population[i] = (ind.Kp, ind.Ki, ind.Kd, cost);
                }
            }
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Max(0, Math.Min(1, t));

        private static double RelativeImprovement(double previous, double current)
        {
            if (previous <= 0 || double.IsInfinity(previous) || double.IsNaN(previous)) return double.PositiveInfinity;
            return (previous - current) / Math.Abs(previous);
        }
    }
}
