using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PhoneCamDesktop;

internal sealed record UsbDevice(int DeviceId, string SerialNumber, string ConnectionType);

internal sealed class UsbmuxUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Talks to Apple's usbmux daemon ("Apple Mobile Device Service" on Windows, listening on
/// 127.0.0.1:27015). It lists iPhones connected by cable and opens TCP tunnels to a port on
/// the phone, over the USB cable.
/// </summary>
internal static class Usbmux
{
    private const int UsbmuxPort = 27015;
    private static int _tag;

    public static async Task<List<UsbDevice>> ListDevicesAsync(CancellationToken ct)
    {
        using var client = await OpenAsync(ct);
        var stream = client.GetStream();
        await SendAsync(stream, BaseMessage("ListDevices"), ct);
        var reply = await ReceiveAsync(stream, ct);

        var result = new List<UsbDevice>();
        if (reply.GetValueOrDefault("DeviceList") is List<object> list)
        {
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                if (item.GetValueOrDefault("Properties") is not Dictionary<string, object> props) continue;
                object idValue = props.GetValueOrDefault("DeviceID") ?? item.GetValueOrDefault("DeviceID") ?? 0L;
                result.Add(new UsbDevice(
                    Convert.ToInt32(idValue),
                    props.GetValueOrDefault("SerialNumber") as string ?? "",
                    props.GetValueOrDefault("ConnectionType") as string ?? ""));
            }
        }
        return result;
    }

    /// <summary>Opens a tunnel to <paramref name="port"/> on the phone. The returned socket is a raw byte pipe.</summary>
    public static async Task<TcpClient> ConnectAsync(int deviceId, ushort port, CancellationToken ct)
    {
        var client = await OpenAsync(ct);
        try
        {
            var stream = client.GetStream();
            var msg = BaseMessage("Connect");
            msg["DeviceID"] = deviceId;
            msg["PortNumber"] = (int)(ushort)((port << 8) | (port >> 8)); // network byte order
            await SendAsync(stream, msg, ct);

            var reply = await ReceiveAsync(stream, ct);
            long result = Convert.ToInt64(reply.GetValueOrDefault("Number") ?? -1L);
            if (result != 0)
            {
                throw new IOException(result switch
                {
                    2 => "The iPhone is no longer connected.",
                    3 => "The iPhone refused the connection (is PhoneCam open on the phone?).",
                    _ => $"usbmux error {result}.",
                });
            }
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> OpenAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, UsbmuxPort, ct);
            return client;
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new UsbmuxUnavailableException(
                "Apple Mobile Device Service isn't running. Install the Apple Devices app (Microsoft Store) or iTunes.", ex);
        }
    }

    private static Dictionary<string, object> BaseMessage(string type) => new()
    {
        ["MessageType"] = type,
        ["ClientVersionString"] = "PhoneCamDesktop 1.0",
        ["ProgName"] = "PhoneCamDesktop",
        ["kLibUSBMuxVersion"] = 3,
    };

    private static async Task SendAsync(NetworkStream stream, Dictionary<string, object> message, CancellationToken ct)
    {
        byte[] body = Plist.Serialize(message);
        byte[] packet = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), (uint)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);  // protocol version 1 = plist
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 8);  // message type 8 = plist
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), (uint)Interlocked.Increment(ref _tag));
        body.CopyTo(packet, 16);
        await stream.WriteAsync(packet, ct);
    }

    private static async Task<Dictionary<string, object>> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[16];
        await stream.ReadExactlyAsync(header, ct);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < 16 || length > 16 * 1024 * 1024)
            throw new IOException("Invalid reply from Apple Mobile Device Service.");

        byte[] body = new byte[length - 16];
        await stream.ReadExactlyAsync(body, ct);
        return Plist.Parse(body) as Dictionary<string, object>
               ?? throw new IOException("Unexpected reply from Apple Mobile Device Service.");
    }
}
