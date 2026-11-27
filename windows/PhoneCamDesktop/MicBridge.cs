using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhoneCamDesktop;

internal sealed record AudioDevice(string Id, string Name)
{
    public bool IsVirtualCable => Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Plays the iPhone's microphone audio into a Windows playback device, normally VB-CABLE's
/// "CABLE Input". Teams or Zoom then pick "CABLE Output" as their microphone.
/// Start/Stop/device changes must happen on the UI thread; <see cref="Write"/> can come from any thread.
/// </summary>
internal sealed class MicBridge : IDisposable
{
    /// <summary>Keep at most this much audio queued; more means the phone's clock runs fast, so drop the backlog.</summary>
    private static readonly TimeSpan MaxLatency = TimeSpan.FromMilliseconds(250);

    private readonly MMDeviceEnumerator _enumerator = new();
    private WasapiOut? _output;
    private volatile BufferedWaveProvider? _buffer;
    private volatile int _sampleRate;
    private volatile int _requestedRate;
    private float _peak;

    /// <summary>Raised (on any thread) when incoming audio needs a different format than the open device.</summary>
    public event Action<int>? FormatChanged;

    public AudioDevice? Device { get; private set; }
    public bool Enabled { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Decaying peak level, 0–1, for a meter.</summary>
    public float Level
    {
        get
        {
            float p = _peak;
            _peak = p * 0.6f;
            return p;
        }
    }

    public List<AudioDevice> PlaybackDevices()
    {
        try
        {
            return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new AudioDevice(d.ID, d.FriendlyName))
                .OrderByDescending(d => d.IsVirtualCable)
                .ThenBy(d => d.Name)
                .ToList();
        }
        catch (Exception)
        {
            return new List<AudioDevice>();
        }
    }

    public AudioDevice? FindVirtualCable() => PlaybackDevices().FirstOrDefault(d => d.IsVirtualCable);

    public void SetDevice(AudioDevice? device)
    {
        Device = device;
        Restart();
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Restart();
    }

    /// <summary>Call on the UI thread after <see cref="FormatChanged"/>.</summary>
    public void UseSampleRate(int sampleRate)
    {
        if (sampleRate == _sampleRate) return;
        _sampleRate = sampleRate;
        Restart();
    }

    /// <summary>One audio message from the phone: [UInt32 BE rate][UInt8 channels][UInt8 0][Int16 LE PCM].</summary>
    public void Write(byte[] payload)
    {
        if (payload.Length < 8) return;
        int rate = (int)BinaryPrimitives.ReadUInt32BigEndian(payload);
        if (rate != _sampleRate)
        {
            if (rate != _requestedRate)
            {
                _requestedRate = rate; // ask once, not for every packet
                FormatChanged?.Invoke(rate);
            }
            return;
        }

        int peak = 0;
        for (int i = 6; i + 1 < payload.Length; i += 2)
        {
            int v = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(i)));
            if (v > peak) peak = v;
        }
        float level = Math.Min(1f, peak / 32767f);
        if (level > _peak) _peak = level;

        var buffer = _buffer;
        if (buffer is null) return;
        if (buffer.BufferedDuration > MaxLatency) buffer.ClearBuffer();
        buffer.AddSamples(payload, 6, payload.Length - 6);
    }

    private void Restart()
    {
        Stop();
        Error = null;
        if (!Enabled || Device is null || _sampleRate <= 0) return;

        try
        {
            var device = _enumerator.GetDevice(Device.Id);
            var mix = device.AudioClient.MixFormat;

            var buffer = new BufferedWaveProvider(new WaveFormat(_sampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true, // play silence when the phone is late instead of stopping
            };
            ISampleProvider samples = buffer.ToSampleProvider();
            if (mix.SampleRate != _sampleRate) samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
            samples = new MonoToChannels(samples, mix.Channels);

            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
            output.Init(samples);
            output.Play();
            _output = output;
            _buffer = buffer;
        }
        catch (Exception ex)
        {
            Error = "Couldn't play to “" + Device.Name + "”: " + ex.Message;
            Stop();
        }
    }

    private void Stop()
    {
        _buffer = null;
        var output = _output;
        _output = null;
        try
        {
            output?.Stop();
            output?.Dispose();
        }
        catch (Exception) { /* device may already be gone */ }
    }

    public void Dispose()
    {
        Stop();
        _enumerator.Dispose();
    }

    /// <summary>Copies a mono signal to every channel of the device.</summary>
    private sealed class MonoToChannels(ISampleProvider source, int channels) : ISampleProvider
    {
        private float[] _mono = Array.Empty<float>();

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, Math.Max(1, channels));

        public int Read(float[] buffer, int offset, int count)
        {
            int ch = WaveFormat.Channels;
            int frames = count / ch;
            if (_mono.Length < frames) _mono = new float[frames];
            int read = source.Read(_mono, 0, frames);
            for (int f = 0; f < read; f++)
            {
                float v = _mono[f];
                int o = offset + f * ch;
                for (int c = 0; c < ch; c++) buffer[o + c] = v;
            }
            return read * ch;
        }
    }
}
