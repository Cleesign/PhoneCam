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
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            Ui.Scale = g.DpiX / 96f;
        Application.Run(new MainForm());
    }
}
