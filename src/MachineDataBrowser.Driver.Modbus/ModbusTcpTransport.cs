using System.Buffers.Binary;
using System.Net.Sockets;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.Modbus;

/// <summary>
/// One Modbus TCP connection. Requests are sent one at a time (many devices accept only one outstanding request);
/// a failed connection is dropped and opened again by the next request.
/// </summary>
internal sealed class ModbusTcpTransport(string host, int port, byte unitId, TimeSpan timeout) : IAsyncDisposable
{
    public const int MaxRegisters = 125;
    public const int MaxBits = 2000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;
    private long _requests;
    private long _failures;
    private bool _disposed;

    /// <summary>Raised after each request: <c>true</c> when the device answered (also with a Modbus exception).</summary>
    public event Action<bool>? LinkChanged;

    public string Host => host;

    public int Port => port;

    public byte UnitId => unitId;

    public TimeSpan Timeout => timeout;

    public long Requests => Interlocked.Read(ref _requests);

    public long Failures => Interlocked.Read(ref _failures);

    public int Connects { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool[]> ReadBitsAsync(ModbusArea area, ushort start, int count, CancellationToken cancellationToken)
    {
        var function = area == ModbusArea.Coil ? (byte)1 : (byte)2;
        var response = await RequestAsync(ReadRequest(function, start, count), cancellationToken).ConfigureAwait(false);
        if (response.Length < 2 || response[1] < (count + 7) / 8 || response.Length < 2 + response[1])
        {
            throw new DeviceClientException(DeviceStatusCodes.BadDecodingError, "Short Modbus response.");
        }

        var bits = new bool[count];
        for (var i = 0; i < count; i++)
        {
            bits[i] = (response[2 + (i / 8)] & (1 << (i % 8))) != 0;
        }

        return bits;
    }

    public async Task<ushort[]> ReadRegistersAsync(ModbusArea area, ushort start, int count, CancellationToken cancellationToken)
    {
        var function = area == ModbusArea.HoldingRegister ? (byte)3 : (byte)4;
        var response = await RequestAsync(ReadRequest(function, start, count), cancellationToken).ConfigureAwait(false);
        if (response.Length < 2 || response[1] < count * 2 || response.Length < 2 + (count * 2))
        {
            throw new DeviceClientException(DeviceStatusCodes.BadDecodingError, "Short Modbus response.");
        }

        var registers = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + (2 * i)));
        }

        return registers;
    }

    public Task WriteCoilAsync(ushort address, bool value, CancellationToken cancellationToken)
    {
        var pdu = new byte[5];
        pdu[0] = 5;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value ? (ushort)0xFF00 : (ushort)0);
        return RequestAsync(pdu, cancellationToken);
    }

    public Task WriteRegistersAsync(ushort address, ushort[] values, CancellationToken cancellationToken)
    {
        byte[] pdu;
        if (values.Length == 1)
        {
            pdu = new byte[5];
            pdu[0] = 6;
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), values[0]);
        }
        else
        {
            pdu = new byte[6 + (values.Length * 2)];
            pdu[0] = 16;
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
            pdu[5] = (byte)(values.Length * 2);
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + (2 * i)), values[i]);
            }
        }

        return RequestAsync(pdu, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            Close();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] ReadRequest(byte function, ushort start, int count)
    {
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)count);
        return pdu;
    }

    /// <summary>Sends one PDU and returns the response PDU (function code first).</summary>
    private async Task<byte[]> RequestAsync(byte[] pdu, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _requests);
            byte[] response;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(timeout);
                try
                {
                    var stream = await EnsureConnectedAsync(cts.Token).ConfigureAwait(false);
                    response = await ExchangeAsync(stream, pdu, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Fail();
                    throw new DeviceClientException(DeviceStatusCodes.BadTimeout, $"No answer from {host}:{port} (unit {unitId}) within {timeout.TotalSeconds:0.#} s.");
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException && !_disposed)
                {
                    Fail();
                    throw new DeviceClientException(DeviceStatusCodes.BadCommunicationError, $"{host}:{port}: {ex.Message}", ex);
                }
                catch (DeviceClientException)
                {
                    Fail();
                    throw;
                }
            }

            LinkChanged?.Invoke(true);
            if ((response[0] & 0x80) != 0)
            {
                Interlocked.Increment(ref _failures);
                throw ToException(response.Length > 1 ? response[1] : (byte)0);
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Fail()
    {
        Interlocked.Increment(ref _failures);
        Close();
        LinkChanged?.Invoke(false);
    }

    private async Task<byte[]> ExchangeAsync(NetworkStream stream, byte[] pdu, CancellationToken cancellationToken)
    {
        var id = ++_transactionId;
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, id);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame, 7);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

        var header = new byte[7];
        while (true)
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 || length is < 2 or > 260)
            {
                throw new DeviceClientException(DeviceStatusCodes.BadDecodingError, "Not a Modbus TCP response.");
            }

            var body = new byte[length - 1];
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

            // Answers to requests that timed out earlier arrive late; skip them.
            if (BinaryPrimitives.ReadUInt16BigEndian(header) == id)
            {
                return body;
            }
        }
    }

    private async Task<NetworkStream> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_stream is { } open)
        {
            return open;
        }

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new DeviceClientException(DeviceStatusCodes.BadTimeout, $"Connecting to {host}:{port} timed out.");
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new DeviceClientException(DeviceStatusCodes.BadNotConnected, $"Can't connect to {host}:{port}: {ex.Message}", ex);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
        Connects++;
        return _stream;
    }

    private void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    private static DeviceClientException ToException(byte code) => code switch
    {
        1 => new(DeviceStatusCodes.BadNotReadable, "Modbus exception 1: the device doesn't support this function."),
        2 => new(DeviceStatusCodes.BadNodeIdUnknown, "Modbus exception 2: illegal data address (the device has no such address)."),
        3 => new(DeviceStatusCodes.BadOutOfRange, "Modbus exception 3: illegal data value."),
        4 => new(DeviceStatusCodes.BadUnexpectedError, "Modbus exception 4: server device failure."),
        6 => new(DeviceStatusCodes.BadTimeout, "Modbus exception 6: server device busy."),
        10 => new(DeviceStatusCodes.BadNoCommunication, "Modbus exception 10: gateway path unavailable."),
        11 => new(DeviceStatusCodes.BadNoCommunication, "Modbus exception 11: gateway target device failed to respond."),
        _ => new(DeviceStatusCodes.Bad, $"Modbus exception {code}."),
    };
}
