using System.IO.Ports;

namespace Toki.Hardware.Scales;

/// <summary>A scale on a COM port (RS-232, or USB with a virtual COM port). The port stays open between reads and is reopened after an error.</summary>
public sealed class SerialScale(ScaleSettings settings) : IScale, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(700);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ContinuousStability _stability = new();
    private SerialPort? _port;

    public async Task<ScaleReading> ReadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(Read, ct);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Close(); // unplugged, busy or silent: try again on the next read
            return ScaleReading.Disconnected;
        }
        finally { _lock.Release(); }
    }

    private ScaleReading Read()
    {
        var port = Open();
        port.DiscardInBuffer();
        switch (settings.Protocol)
        {
            case ScaleProtocol.Cas:
                port.Write([ScaleProtocols.Enq], 0, 1);
                if (port.ReadByte() != ScaleProtocols.Ack) return ScaleReading.Disconnected;
                port.Write([ScaleProtocols.Dc1], 0, 1);
                return ScaleProtocols.ParseCas(ReadUntil(port, ScaleProtocols.Eot)) ?? ScaleReading.Disconnected;
            case ScaleProtocol.Toledo8217:
                port.Write(ScaleProtocols.ToledoRequest, 0, ScaleProtocols.ToledoRequest.Length);
                return ScaleProtocols.ParseToledo(ReadUntil(port, ScaleProtocols.Cr)) ?? ScaleReading.Disconnected;
            default:
                ReadUntil(port, ScaleProtocols.Lf, ScaleProtocols.Cr); // the rest of a frame already on its way
                for (var i = 0; i < 3; i++) // skips the empty line between CR and LF
                {
                    var line = System.Text.Encoding.ASCII.GetString(ReadUntil(port, ScaleProtocols.Lf, ScaleProtocols.Cr));
                    if (ScaleProtocols.ParseContinuous(line) is { } frame) return _stability.Next(frame);
                }
                return ScaleReading.Disconnected;
        }
    }

    private static byte[] ReadUntil(SerialPort port, params byte[] ends)
    {
        var bytes = new List<byte>(32);
        while (bytes.Count < 64)
        {
            var b = (byte)port.ReadByte(); // throws TimeoutException when the scale goes quiet
            bytes.Add(b);
            if (Array.IndexOf(ends, b) >= 0) break;
        }
        return [.. bytes];
    }

    private SerialPort Open()
    {
        if (_port is { IsOpen: true }) return _port;
        // Toledo 8217 talks 7 data bits, even parity; CAS and the continuous indicators 8N1.
        var toledo = settings.Protocol == ScaleProtocol.Toledo8217;
        _port = new SerialPort(settings.Port, settings.BaudRate, toledo ? Parity.Even : Parity.None, toledo ? 7 : 8, StopBits.One)
        {
            ReadTimeout = (int)Timeout.TotalMilliseconds,
            WriteTimeout = (int)Timeout.TotalMilliseconds,
        };
        _port.Open();
        return _port;
    }

    private void Close()
    {
        _port?.Dispose();
        _port = null;
    }

    public void Dispose()
    {
        Close();
        _lock.Dispose();
    }
}
