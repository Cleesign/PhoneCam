using System.Text.Json;

namespace PhoneCamDesktop;

/// <summary>Remembered choices, stored in %AppData%\PhoneCam\settings.json.</summary>
internal sealed class AppSettings
{
    public bool CameraOn { get; set; } = true;    // false = microphone only
    public int Camera { get; set; }               // 0 = back, 1 = front
    public int Resolution { get; set; }           // 0 = 720p, 1 = 1080p
    public int Quality { get; set; } = 1;         // 0 = low, 1 = medium, 2 = high
    public bool AutoRotate { get; set; } = true;
    public int Rotation { get; set; }             // index: 0°, 90°, 180°, 270°
    public bool Fill { get; set; } = true;
    public bool Mirror { get; set; }
    public int OutputSize { get; set; } = 1;      // 0 = 480p, 1 = 720p, 2 = 1080p

    public bool MicEnabled { get; set; }
    public bool MicNoiseReduction { get; set; } = true;
    public string? MicDeviceId { get; set; }

    public bool WatermarkEnabled { get; set; }
    public string? WatermarkPath { get; set; }
    public int WatermarkPosition { get; set; } = (int)PhoneCamDesktop.WatermarkPosition.BottomRight;
    public float WatermarkSize { get; set; } = 0.18f;
    public float WatermarkOpacity { get; set; } = 0.9f;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhoneCam", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { /* corrupt or unreadable: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* not critical */ }
    }
}
