using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

namespace PhoneCamDesktop;

/// <summary>
/// Keeps a connection to the PhoneCam app on the iPhone over USB, reconnecting automatically.
/// Wire format (both directions): [UInt32 big-endian length][UInt8 type][payload].
/// </summary>
internal sealed class PhoneLink : IDisposable
{
    public const ushort PhonePort = 5858;

    private const byte TypeFrame = 0x01;
    private const byte TypeStatus = 0x02;
    private const byte TypeAudio = 0x03;
    private const byte TypeCommand = 0x10;

    /// <summary>Human-readable connection state. Raised on a background thread.</summary>
    public event Action<string>? StateChanged;
    /// <summary>Raised on a background thread.</summary>
    public event Action<bool>? ConnectionChanged;
    /// <summary>JPEG frame from the phone. Raised on the receive thread — handle it synchronously.</summary>
    public event Action<byte[]>? FrameReceived;
    /// <summary>Status JSON from the phone. Raised on a background thread.</summary>
    public event Action<JsonElement>? StatusReceived;
    /// <summary>Microphone audio from the phone. Raised on the receive thread — handle it quickly.</summary>
    public event Action<byte[]>? AudioReceived;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile NetworkStream? _stream;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    public async Task SendCommandAsync(IDictionary<string, object> command)
    {
        var stream = _stream;
        if (stream is null) return;

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(command);
        byte[] packet = new byte[5 + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)json.Length);
        packet[4] = TypeCommand;
        json.CopyTo(packet, 5);

        await _sendLock.WaitAsync();
        try { await stream.WriteAsync(packet); }
        catch (Exception) { /* the receive loop notices the broken connection */ }
        finally { _sendLock.Release(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string lastState = "";
        void Report(string s)
        {
            if (s == lastState) return;
            lastState = s;
            StateChanged?.Invoke(s);
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var devices = await Usbmux.ListDevicesAsync(ct);
                var device = devices.FirstOrDefault(d => d.ConnectionType == "USB");
                if (device is null)
                {
                    Report("No iPhone found. Connect it with the USB-C cable, unlock it and tap “Trust”.");
                    await Task.Delay(1000, ct);
                    continue;
                }

                TcpClient client;
                try
                {
                    client = await Usbmux.ConnectAsync(device.DeviceId, PhonePort, ct);
                }
                catch (IOException)
                {
                    Report("iPhone found. Open the PhoneCam app on the phone.");
                    await Task.Delay(1000, ct);
                    continue;
                }

                using (client)
                {
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 1 << 20;
                    var stream = client.GetStream();
                    _stream = stream;
                    Report("Connected to iPhone.");
                    ConnectionChanged?.Invoke(true);
                    try
                    {
                        await SendCommandAsync(new Dictionary<string, object> { ["requestStatus"] = true });
                        await ReadLoopAsync(stream, ct);
                    }
                    finally
                    {
                        _stream = null;
                        ConnectionChanged?.Invoke(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (UsbmuxUnavailableException ex)
            {
                Report(ex.Message);
                await DelayQuietly(2000, ct);
            }
            catch (Exception)
            {
                Report("iPhone disconnected. Reconnecting…");
                await DelayQuietly(1000, ct);
            }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[5];
        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, ct);
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
            byte type = header[4];
            if (length < 0 || length > 20_000_000) throw new IOException("Invalid message size.");

            byte[] payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct);

            switch (type)
            {
                case TypeFrame:
                    FrameReceived?.Invoke(payload);
                    break;
                case TypeAudio:
                    AudioReceived?.Invoke(payload);
                    break;
                case TypeStatus:
                    using (var doc = JsonDocument.Parse(payload))
                        StatusReceived?.Invoke(doc.RootElement.Clone());
                    break;
            }
        }
    }

    private static async Task DelayQuietly(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream?.Dispose();
    }
}
