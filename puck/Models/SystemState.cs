using System;
using System.Collections.Generic;
using Puck.Services;

namespace Puck.Models
{
    /// <summary>
    /// Represents the full state of the system, including all constituent device and process states.
    /// </summary>
    public readonly struct SystemState
    {
        // Example properties for temperature controllers
        public double? GroupHeadTemperature { get; }
        public double? ThermoblockTemperature { get; }
        public bool GroupHeadHeaterEnabled { get; }
        public bool ThermoblockHeaterEnabled { get; }

        // Example properties for IoBus
        public bool IsIoBusConnected { get; }
        public double? GroupHeadPressure { get; }
        public double? PumpSpeed { get; }

        // Example properties for valves
        public ValveState GroupHeadValveState { get; }
        public ValveState BackflushValveState { get; }
        public ValveState RecirculationValveState { get; }

        // Example properties for run/process state
        public RunState RunState { get; }
        public bool IsPaused { get; }
        public DateTime? RunStartTimeUtc { get; }
        public double? ExtractionWeight { get; }

        // Add more properties as needed for your system

        public SystemState(
            double? groupHeadTemperature,
            double? thermoblockTemperature,
            bool groupHeadHeaterEnabled,
            bool thermoblockHeaterEnabled,
            bool isIoBusConnected,
            double? groupHeadPressure,
            double? pumpSpeed,
            ValveState groupHeadValveState,
            ValveState backflushValveState,
            ValveState recirculationValveState,
            RunState runState,
            bool isPaused,
            DateTime? runStartTimeUtc,
            double? extractionWeight
        )
        {
            GroupHeadTemperature = groupHeadTemperature;
            ThermoblockTemperature = thermoblockTemperature;
            GroupHeadHeaterEnabled = groupHeadHeaterEnabled;
            ThermoblockHeaterEnabled = thermoblockHeaterEnabled;
            IsIoBusConnected = isIoBusConnected;
            GroupHeadPressure = groupHeadPressure;
            PumpSpeed = pumpSpeed;
            GroupHeadValveState = groupHeadValveState;
            BackflushValveState = backflushValveState;
            RecirculationValveState = recirculationValveState;
            RunState = runState;
            IsPaused = isPaused;
            RunStartTimeUtc = runStartTimeUtc;
            ExtractionWeight = extractionWeight;
        }
    }
} 