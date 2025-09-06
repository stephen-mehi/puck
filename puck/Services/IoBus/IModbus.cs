using NModbus;

namespace puck.Services.IoBus
{
    //
    // Summary:
    //     Modbus master device.
    public interface IModbus: IDisposable
    {

        //
        // Summary:
        //     Reads from 1 to 2000 contiguous coils status.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of coils to read.
        //
        // Returns:
        //     Coils status.
        bool[] ReadCoils(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Asynchronously reads from 1 to 2000 contiguous coils status.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of coils to read.
        //
        // Returns:
        //     A task that represents the asynchronous read operation.
        Task<bool[]> ReadCoilsAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Reads from 1 to 2000 contiguous discrete input status.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of discrete inputs to read.
        //
        // Returns:
        //     Discrete inputs status.
        bool[] ReadInputs(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Asynchronously reads from 1 to 2000 contiguous discrete input status.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of discrete inputs to read.
        //
        // Returns:
        //     A task that represents the asynchronous read operation.
        Task<bool[]> ReadInputsAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Reads contiguous block of holding registers.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of holding registers to read.
        //
        // Returns:
        //     Holding registers status.
        ushort[] ReadHoldingRegisters(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Asynchronously reads contiguous block of holding registers.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of holding registers to read.
        //
        // Returns:
        //     A task that represents the asynchronous read operation.
        Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Reads contiguous block of input registers.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of holding registers to read.
        //
        // Returns:
        //     Input registers status.
        ushort[] ReadInputRegisters(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Asynchronously reads contiguous block of input registers.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startAddress:
        //     Address to begin reading.
        //
        //   numberOfPoints:
        //     Number of holding registers to read.
        //
        // Returns:
        //     A task that represents the asynchronous read operation.
        Task<ushort[]> ReadInputRegistersAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints);

        //
        // Summary:
        //     Writes a single coil value.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   coilAddress:
        //     Address to write value to.
        //
        //   value:
        //     Value to write.
        void WriteSingleCoil(byte slaveAddress, ushort coilAddress, bool value);

        //
        // Summary:
        //     Asynchronously writes a single coil value.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   coilAddress:
        //     Address to write value to.
        //
        //   value:
        //     Value to write.
        //
        // Returns:
        //     A task that represents the asynchronous write operation.
        Task WriteSingleCoilAsync(byte slaveAddress, ushort coilAddress, bool value);

        //
        // Summary:
        //     Writes a single holding register.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   registerAddress:
        //     Address to write.
        //
        //   value:
        //     Value to write.
        void WriteSingleRegister(byte slaveAddress, ushort registerAddress, ushort value);

        //
        // Summary:
        //     Asynchronously writes a single holding register.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   registerAddress:
        //     Address to write.
        //
        //   value:
        //     Value to write.
        //
        // Returns:
        //     A task that represents the asynchronous write operation.
        Task WriteSingleRegisterAsync(byte slaveAddress, ushort registerAddress, ushort value);

        //
        // Summary:
        //     Writes a block of 1 to 123 contiguous registers.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   startAddress:
        //     Address to begin writing values.
        //
        //   data:
        //     Values to write.
        void WriteMultipleRegisters(byte slaveAddress, ushort startAddress, ushort[] data);

        //
        // Summary:
        //     Asynchronously writes a block of 1 to 123 contiguous registers.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   startAddress:
        //     Address to begin writing values.
        //
        //   data:
        //     Values to write.
        //
        // Returns:
        //     A task that represents the asynchronous write operation.
        Task WriteMultipleRegistersAsync(byte slaveAddress, ushort startAddress, ushort[] data);

        //
        // Summary:
        //     Writes a sequence of coils.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   startAddress:
        //     Address to begin writing values.
        //
        //   data:
        //     Values to write.
        void WriteMultipleCoils(byte slaveAddress, ushort startAddress, bool[] data);

        //
        // Summary:
        //     Asynchronously writes a sequence of coils.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of the device to write to.
        //
        //   startAddress:
        //     Address to begin writing values.
        //
        //   data:
        //     Values to write.
        //
        // Returns:
        //     A task that represents the asynchronous write operation.
        Task WriteMultipleCoilsAsync(byte slaveAddress, ushort startAddress, bool[] data);

        //
        // Summary:
        //     Performs a combination of one read operation and one write operation in a single
        //     Modbus transaction. The write operation is performed before the read.
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startReadAddress:
        //     Address to begin reading (Holding registers are addressed starting at 0).
        //
        //   numberOfPointsToRead:
        //     Number of registers to read.
        //
        //   startWriteAddress:
        //     Address to begin writing (Holding registers are addressed starting at 0).
        //
        //   writeData:
        //     Register values to write.
        ushort[] ReadWriteMultipleRegisters(byte slaveAddress, ushort startReadAddress, ushort numberOfPointsToRead, ushort startWriteAddress, ushort[] writeData);

        //
        // Summary:
        //     Asynchronously performs a combination of one read operation and one write operation
        //     in a single Modbus transaction. The write operation is performed before the read.
        //
        //
        // Parameters:
        //   slaveAddress:
        //     Address of device to read values from.
        //
        //   startReadAddress:
        //     Address to begin reading (Holding registers are addressed starting at 0).
        //
        //   numberOfPointsToRead:
        //     Number of registers to read.
        //
        //   startWriteAddress:
        //     Address to begin writing (Holding registers are addressed starting at 0).
        //
        //   writeData:
        //     Register values to write.
        //
        // Returns:
        //     A task that represents the asynchronous operation
        Task<ushort[]> ReadWriteMultipleRegistersAsync(byte slaveAddress, ushort startReadAddress, ushort numberOfPointsToRead, ushort startWriteAddress, ushort[] writeData);

        //
        // Summary:
        //     Write a file record to the device.
        //
        // Parameters:
        //   slaveAdress:
        //     Address of device to write values to
        //
        //   fileNumber:
        //     The Extended Memory file number
        //
        //   startingAddress:
        //     The starting register address within the file
        //
        //   data:
        //     The data to be written
        void WriteFileRecord(byte slaveAdress, ushort fileNumber, ushort startingAddress, byte[] data);

        //
        // Summary:
        //     Executes the custom message.
        //
        // Parameters:
        //   request:
        //     The request.
        //
        // Type parameters:
        //   TResponse:
        //     The type of the response.
        TResponse ExecuteCustomMessage<TResponse>(IModbusMessage request) where TResponse : IModbusMessage, new();
    }
}
