using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace puck.Services.IoBus
{
    public interface IPhoenixProxy : IDisposable
    {
        IReadOnlyDictionary<ushort, DigitalIoState?> DigitalInputState { get; }
        IReadOnlyDictionary<ushort, DigitalIoState?> DigitalOutputState { get; }
        IReadOnlyDictionary<ushort, AnalogIoState?> AnalogInputState { get; }
        IReadOnlyDictionary<ushort, AnalogIoState?> AnalogOutputState { get; }
        Task SetDigitalOutputStateAsync(ushort index, bool state, CancellationToken ct);
        Task SetAnalogOutputStateAsync(ushort index, double state, CancellationToken ct);
    }
} 