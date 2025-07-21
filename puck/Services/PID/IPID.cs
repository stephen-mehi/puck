using System;

namespace puck.Services.PID
{
    /// <summary>
    /// Interface for a PID controller.
    /// </summary>
    public interface IPID
    {
        /// <summary>
        /// Proportional Gain.
        /// </summary>
        double Kp { get; }

        /// <summary>
        /// Integral Gain.
        /// </summary>
        double Ki { get; }

        /// <summary>
        /// Derivative Gain.
        /// </summary>
        double Kd { get; }

        /// <summary>
        /// Derivative filter coefficient.
        /// </summary>
        double N { get; }

        /// <summary>
        /// Upper output limit of the controller.
        /// </summary>
        double OutputUpperLimit { get; }

        /// <summary>
        /// Lower output limit of the controller.
        /// </summary>
        double OutputLowerLimit { get; }


        /// <summary>
        /// PID iterator, call this function every sample period to get the current controller output.
        /// </summary>
        /// <param name="setPoint">Current Desired Setpoint</param>
        /// <param name="processValue">Current Process Value</param>
        /// <param name="ts">Timespan Since Last Iteration</param>
        /// <returns>Current Controller Output</returns>
        double PID_iterate(double setPoint, double processValue, TimeSpan ts);
    }
} 