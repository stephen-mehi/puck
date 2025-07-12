using System;
using Puck.Services;

namespace Puck.Models
{
    /// <summary>
    /// Represents the comprehensive real-time state of the system and process.
    /// </summary>
    public struct ProcessDeviceState
    {
        // Temperatures
        public double? GroupHeadTemperature { get; set; }
        public double? ThermoblockTemperature { get; set; }
        public double? GroupHeadSetpoint { get; set; }
        public double? ThermoblockSetpoint { get; set; }
        public bool GroupHeadHeaterEnabled { get; set; }
        public bool ThermoblockHeaterEnabled { get; set; }

        // IO/Analog
        public bool IsIoBusConnected { get; set; }
        public double? GroupHeadPressure { get; set; }
        public double? PumpSpeed { get; set; }

        // Valves
        public ValveState GroupHeadValveState { get; set; }
        public ValveState BackflushValveState { get; set; }
        public ValveState RecirculationValveState { get; set; }

        // Process/Run
        public RunState RunState { get; set; }
        public bool IsPaused { get; set; }
        public DateTime? RunStartTimeUtc { get; set; }
        public double? ExtractionWeight { get; set; }

        // PID Controller
        public double? PID_Kp { get; set; }
        public double? PID_Ki { get; set; }
        public double? PID_Kd { get; set; }
        public double? PID_N { get; set; }
        public double? PID_OutputUpperLimit { get; set; }
        public double? PID_OutputLowerLimit { get; set; }

        // Timestamp
        public DateTime StateTimestampUtc { get; set; }

        // General status/info message
        public string? GeneralStatusMessage { get; set; }

        public ProcessDeviceState(
            double? groupHeadTemperature,
            double? thermoblockTemperature,
            double? groupHeadSetpoint,
            double? thermoblockSetpoint,
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
            double? extractionWeight,
            double? pid_Kp,
            double? pid_Ki,
            double? pid_Kd,
            double? pid_N,
            double? pid_OutputUpperLimit,
            double? pid_OutputLowerLimit,
            DateTime stateTimestampUtc,
            string? generalStatusMessage = null
        )
        {
            GroupHeadTemperature = groupHeadTemperature;
            ThermoblockTemperature = thermoblockTemperature;
            GroupHeadSetpoint = groupHeadSetpoint;
            ThermoblockSetpoint = thermoblockSetpoint;
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
            PID_Kp = pid_Kp;
            PID_Ki = pid_Ki;
            PID_Kd = pid_Kd;
            PID_N = pid_N;
            PID_OutputUpperLimit = pid_OutputUpperLimit;
            PID_OutputLowerLimit = pid_OutputLowerLimit;
            StateTimestampUtc = stateTimestampUtc;
            GeneralStatusMessage = generalStatusMessage;
        }
    }
} 