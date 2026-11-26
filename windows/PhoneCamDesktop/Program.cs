using System.Runtime.InteropServices;

namespace PhoneCamDesktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Lets us find softcam.dll next to the exe, via %SOFTCAM_DLL%, or in Program Files\Softcam.
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, SoftcamNative.Resolve);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
