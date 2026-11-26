using System.Reflection;
using System.Runtime.InteropServices;

namespace PhoneCamDesktop;

/// <summary>P/Invoke bindings for softcam (https://github.com/tshino/softcam), a DirectShow virtual camera.</summary>
internal static class SoftcamNative
{
    private const string Dll = "softcam.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr scCreateCamera(int width, int height, float framerate);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void scDeleteCamera(IntPtr camera);

    /// <summary>image_bits: width*height*3 bytes, BGR, top-down rows.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void scSendFrame(IntPtr camera, IntPtr imageBits);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool scIsConnected(IntPtr camera);

    public static IEnumerable<string> CandidatePaths()
    {
        var fromEnv = Environment.GetEnvironmentVariable("SOFTCAM_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv)) yield return fromEnv;
        yield return Path.Combine(AppContext.BaseDirectory, Dll);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Softcam", Dll);
    }

    public static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(Dll, StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals("softcam", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var path in CandidatePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }
        return IntPtr.Zero; // fall back to the default search → DllNotFoundException
    }
}

/// <summary>The "DirectShow Softcam" webcam that Zoom, Teams, OBS, browsers etc. can select.</summary>
internal sealed class VirtualCamera : IDisposable
{
    private readonly object _lock = new();
    private IntPtr _camera;

    public int Width { get; }
    public int Height { get; }

    private VirtualCamera(IntPtr camera, int width, int height)
    {
        _camera = camera;
        Width = width;
        Height = height;
    }

    public static VirtualCamera Create(int width, int height, float fps)
    {
        IntPtr camera;
        try
        {
            camera = SoftcamNative.scCreateCamera(width, height, fps);
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException(
                "softcam.dll not found. Put it next to PhoneCamDesktop.exe, in C:\\Program Files\\Softcam, " +
                "or set the SOFTCAM_DLL environment variable (see README).");
        }
        catch (BadImageFormatException)
        {
            throw new InvalidOperationException("softcam.dll is not a 64-bit (x64) build.");
        }

        if (camera == IntPtr.Zero)
            throw new InvalidOperationException(
                "Couldn't start the virtual camera. Another program (or another PhoneCam window) may already be sending to it.");

        return new VirtualCamera(camera, width, height);
    }

    /// <summary>True while some app (Zoom, Teams, a browser…) has the webcam open.</summary>
    public bool IsInUse
    {
        get
        {
            lock (_lock) return _camera != IntPtr.Zero && SoftcamNative.scIsConnected(_camera);
        }
    }

    /// <summary>Sends one frame: Width*Height*3 bytes, BGR, top-down.</summary>
    public void Send(IntPtr bgrTopDown)
    {
        lock (_lock)
        {
            if (_camera != IntPtr.Zero) SoftcamNative.scSendFrame(_camera, bgrTopDown);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_camera == IntPtr.Zero) return;
            SoftcamNative.scDeleteCamera(_camera);
            _camera = IntPtr.Zero;
        }
    }
}
