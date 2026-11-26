using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace PhoneCamDesktop;

internal sealed class MainForm : Form
{
    private static readonly (string Label, int W, int H)[] OutputSizes =
    {
        ("480p", 640, 480),
        ("720p", 1280, 720),
        ("1080p", 1920, 1080),
    };

    private static readonly double[] Qualities = { 0.4, 0.6, 0.8 };
    private const float OutputFps = 30f;

    private readonly PhoneLink _link = new();
    private readonly object _pipelineLock = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private FrameRenderer _renderer;
    private VirtualCamera? _vcam;
    private string? _vcamError;
    private Bitmap? _watermark;

    // Picture settings, read from the receive thread.
    private volatile int _rotation;
    private volatile bool _mirror;
    private volatile bool _fill;

    private long _frameCount;
    private long _lastFrameCount;
    private long _fps;
    private long _lastPreviewTicks;
    private bool _phoneConnected;
    private bool _applyingPhoneStatus;
    private string _linkState = "Starting…";
    private string? _phoneProblem;

    // UI
    private readonly PreviewView _preview = new() { Dock = DockStyle.Fill };
    private readonly Header _header = new();
    private readonly Segmented _camera = new("Back", "Front");
    private readonly Segmented _resolution = new("720p", "1080p");
    private readonly Segmented _quality = new("Low", "Medium", "High");
    private readonly ToggleSwitch _autoRotate = new();
    private readonly Note _orientationNote = new();
    private readonly Segmented _rotationBox = new("0°", "90°", "180°", "270°");
    private readonly Segmented _framing = new("Fill", "Fit");
    private readonly ToggleSwitch _mirrorSwitch = new();
    private readonly ToggleSwitch _wmSwitch = new();
    private readonly PillButton _wmChoose = new() { Text = "Choose image…" };
    private readonly Segmented _wmPosition = new("↖", "↗", "↙", "↘", "●") { Font = Ui.Body };
    private readonly Slider _wmSize = new(0.05f, 0.6f);
    private readonly Slider _wmOpacity = new(0.1f, 1f);
    private readonly Row _wmImageRow;
    private readonly Row _wmPositionRow;
    private readonly Row _wmSizeRow;
    private readonly Row _wmOpacityRow;
    private readonly Segmented _outputSize = new("480p", "720p", "1080p");
    private readonly Note _vcamNote = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    public MainForm()
    {
        Text = "PhoneCam";
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.Body;
        Icon = Ui.AppIcon;
        ClientSize = new Size(Ui.S(1200), Ui.S(780));
        MinimumSize = new Size(Ui.S(900), Ui.S(560));
        StartPosition = FormStartPosition.CenterScreen;

        _wmImageRow = new Row("Image", _wmChoose);
        _wmPositionRow = new Row("Position", _wmPosition);
        _wmSizeRow = new Row("Size", _wmSize);
        _wmOpacityRow = new Row("Opacity", _wmOpacity);

        ApplySettingsToControls();
        var (_, w, h) = OutputSizes[_outputSize.SelectedIndex];
        _renderer = new FrameRenderer(w, h);

        BuildLayout();
        WireEvents();

        RecreateVirtualCamera();
        UpdateWatermarkUi();
        UpdateOverlay();
        _link.Start();
        _uiTimer.Start();
    }

    // ───────────────────────────── Layout ─────────────────────────────

    private void BuildLayout()
    {
        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = Ui.S(390),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Ui.Bg,
            Padding = new Padding(Ui.S(4), Ui.S(20), Ui.S(20), Ui.S(8)),
        };
        side.HandleCreated += (_, _) => NativeUi.DarkScrollbars(side.Handle);

        var camera = new Card("Camera");
        camera.Controls.Add(new Row("Camera", _camera));
        camera.Controls.Add(new Row("Resolution", _resolution));
        camera.Controls.Add(new Row("Quality", _quality));

        var picture = new Card("Picture");
        picture.Controls.Add(new Row("Follow phone rotation", _autoRotate, stretch: false));
        picture.Controls.Add(_orientationNote);
        picture.Controls.Add(new Row("Extra rotation", _rotationBox));
        picture.Controls.Add(new Row("Framing", _framing));
        picture.Controls.Add(new Row("Mirror", _mirrorSwitch, stretch: false));

        var watermark = new Card("Watermark");
        watermark.Controls.Add(new Row("Show watermark", _wmSwitch, stretch: false));
        watermark.Controls.Add(_wmImageRow);
        watermark.Controls.Add(_wmPositionRow);
        watermark.Controls.Add(_wmSizeRow);
        watermark.Controls.Add(_wmOpacityRow);

        var webcam = new Card("Virtual webcam");
        webcam.Controls.Add(new Row("Output size", _outputSize));
        webcam.Controls.Add(_vcamNote);

        _header.Margin = new Padding(Ui.S(4), 0, 0, Ui.S(16));
        side.Controls.Add(_header);
        foreach (var card in new Control[] { camera, picture, watermark, webcam })
        {
            card.Margin = new Padding(0, 0, 0, Ui.S(12));
            side.Controls.Add(card);
        }

        void FitWidths()
        {
            int width = side.ClientSize.Width - side.Padding.Horizontal;
            foreach (Control c in side.Controls) c.Width = Math.Max(0, width - c.Margin.Horizontal);
        }
        side.ClientSizeChanged += (_, _) => FitWidths();

        Controls.Add(side);
        Controls.Add(_preview);
        _preview.BringToFront(); // Fill is docked last
        FitWidths();
    }

    private void ApplySettingsToControls()
    {
        var s = _settings;
        _camera.SelectedIndex = s.Camera;
        _resolution.SelectedIndex = s.Resolution;
        _quality.SelectedIndex = s.Quality;
        _autoRotate.Checked = s.AutoRotate;
        _rotationBox.SelectedIndex = s.Rotation;
        _framing.SelectedIndex = s.Fill ? 0 : 1;
        _mirrorSwitch.Checked = s.Mirror;
        _outputSize.SelectedIndex = s.OutputSize;
        _wmPosition.SelectedIndex = s.WatermarkPosition;
        _wmSize.Value = s.WatermarkSize;
        _wmOpacity.Value = s.WatermarkOpacity;
        if (s.WatermarkPath is { } path && File.Exists(path)) _watermark = TryLoadImage(path, showError: false);
        _wmSwitch.Checked = s.WatermarkEnabled && _watermark != null;

        _rotation = _rotationBox.SelectedIndex * 90;
        _fill = _framing.SelectedIndex == 0;
        _mirror = _mirrorSwitch.Checked;
    }

    private void SaveSettings()
    {
        var s = _settings;
        s.Camera = _camera.SelectedIndex;
        s.Resolution = _resolution.SelectedIndex;
        s.Quality = _quality.SelectedIndex;
        s.AutoRotate = _autoRotate.Checked;
        s.Rotation = _rotationBox.SelectedIndex;
        s.Fill = _framing.SelectedIndex == 0;
        s.Mirror = _mirrorSwitch.Checked;
        s.OutputSize = _outputSize.SelectedIndex;
        s.WatermarkEnabled = _wmSwitch.Checked;
        s.WatermarkPosition = _wmPosition.SelectedIndex;
        s.WatermarkSize = _wmSize.Value;
        s.WatermarkOpacity = _wmOpacity.Value;
        s.Save();
    }

    // ───────────────────────────── Events ─────────────────────────────

    private void WireEvents()
    {
        _camera.SelectedIndexChanged += (_, _) => SendToPhone("camera", _camera.SelectedIndex == 1 ? "front" : "back");
        _resolution.SelectedIndexChanged += (_, _) => SendToPhone("resolution", _resolution.SelectedIndex == 1 ? "1080p" : "720p");
        _quality.SelectedIndexChanged += (_, _) => SendToPhone("quality", Qualities[_quality.SelectedIndex]);
        _autoRotate.CheckedChanged += (_, _) => SendToPhone("autoRotate", _autoRotate.Checked);

        _rotationBox.SelectedIndexChanged += (_, _) => _rotation = _rotationBox.SelectedIndex * 90;
        _framing.SelectedIndexChanged += (_, _) => _fill = _framing.SelectedIndex == 0;
        _mirrorSwitch.CheckedChanged += (_, _) => _mirror = _mirrorSwitch.Checked;
        _outputSize.SelectedIndexChanged += (_, _) => RecreateVirtualCamera();

        _wmSwitch.CheckedChanged += (_, _) =>
        {
            if (_wmSwitch.Checked && _watermark == null && !ChooseWatermark())
            {
                _wmSwitch.Checked = false;
                return;
            }
            ApplyWatermark();
        };
        _wmChoose.Click += (_, _) => { if (ChooseWatermark()) ApplyWatermark(); };
        _wmPosition.SelectedIndexChanged += (_, _) => ApplyWatermark();
        _wmSize.ValueChanged += (_, _) => ApplyWatermark();
        _wmOpacity.ValueChanged += (_, _) => ApplyWatermark();

        _link.StateChanged += s => OnUi(() => { _linkState = s; UpdateOverlay(); });
        _link.ConnectionChanged += connected => OnUi(() => OnPhoneConnectionChanged(connected));
        _link.StatusReceived += status => OnUi(() => ApplyPhoneStatus(status));
        _link.FrameReceived += OnFrame;

        _uiTimer.Tick += (_, _) => UpdateStats();
    }

    private void SendToPhone(string key, object value)
    {
        if (_applyingPhoneStatus) return; // don't echo back what the phone just told us
        _ = _link.SendCommandAsync(new Dictionary<string, object> { [key] = value });
    }

    private void OnPhoneConnectionChanged(bool connected)
    {
        _phoneConnected = connected;
        if (connected)
        {
            // Push the current choices to the phone.
            SendToPhone("camera", _camera.SelectedIndex == 1 ? "front" : "back");
            SendToPhone("resolution", _resolution.SelectedIndex == 1 ? "1080p" : "720p");
            SendToPhone("quality", Qualities[_quality.SelectedIndex]);
            SendToPhone("autoRotate", _autoRotate.Checked);
        }
        else
        {
            _phoneProblem = null;
            _orientationNote.Text = "";
            lock (_pipelineLock) SendPlaceholder();
        }
        UpdateOverlay();
    }

    /// <summary>The phone reports its state (e.g. after someone taps a button on the phone).</summary>
    private void ApplyPhoneStatus(JsonElement status)
    {
        _applyingPhoneStatus = true;
        try
        {
            if (status.TryGetProperty("camera", out var cam))
                _camera.SelectedIndex = cam.GetString() == "front" ? 1 : 0;
            if (status.TryGetProperty("resolution", out var res))
                _resolution.SelectedIndex = res.GetString() == "1080p" ? 1 : 0;
            if (status.TryGetProperty("quality", out var q) && q.TryGetDouble(out double qv))
            {
                int nearest = 0;
                for (int i = 1; i < Qualities.Length; i++)
                    if (Math.Abs(Qualities[i] - qv) < Math.Abs(Qualities[nearest] - qv)) nearest = i;
                _quality.SelectedIndex = nearest;
            }
            if (status.TryGetProperty("autoRotate", out var ar) && ar.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _autoRotate.Checked = ar.GetBoolean();
            if (status.TryGetProperty("orientation", out var o))
            {
                _orientationNote.Text = "Phone is held " + (o.GetString() switch
                {
                    "portrait" => "upright (portrait)",
                    "portraitUpsideDown" => "upside down",
                    "landscapeLeft" => "sideways, top to the left",
                    "landscapeRight" => "sideways, top to the right",
                    _ => "—",
                });
            }

            _phoneProblem = null;
            if (status.TryGetProperty("error", out var err) && !string.IsNullOrEmpty(err.GetString()))
                _phoneProblem = err.GetString();
            else if (status.TryGetProperty("cameraAuthorized", out var auth) && auth.ValueKind == JsonValueKind.False)
                _phoneProblem = "Allow camera access for PhoneCam on the iPhone (Settings › PhoneCam).";
            UpdateOverlay();
        }
        finally
        {
            _applyingPhoneStatus = false;
        }
    }

    // ───────────────────────────── Watermark ─────────────────────────────

    private bool ChooseWatermark()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a watermark image",
            Filter = "Images (*.png; *.jpg; *.jpeg)|*.png;*.jpg;*.jpeg",
        };
        try
        {
            if (_settings.WatermarkPath is { } last) dialog.InitialDirectory = Path.GetDirectoryName(last);
        }
        catch (ArgumentException) { /* ignore a bad stored path */ }

        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        var image = TryLoadImage(dialog.FileName, showError: true);
        if (image == null) return false;

        var old = _watermark;
        _watermark = image;
        _settings.WatermarkPath = dialog.FileName;
        if (!_wmSwitch.Checked) _wmSwitch.Checked = true; // applies it
        else ApplyWatermark();
        old?.Dispose(); // the renderer now holds the new one
        return true;
    }

    /// <summary>Loads into memory (so the file isn't locked) as 32-bit ARGB, keeping PNG transparency.</summary>
    private Bitmap? TryLoadImage(string path, bool showError)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var img = Image.FromStream(stream);
            var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
            }
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
        {
            if (showError)
                MessageBox.Show(this, "This image couldn't be opened.\n\n" + ex.Message, "PhoneCam",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
    }

    private void ApplyWatermark()
    {
        lock (_pipelineLock)
        {
            PushWatermark();
            if (!_phoneConnected) SendPlaceholder();
        }
        UpdateWatermarkUi();
    }

    /// <summary>Caller holds _pipelineLock.</summary>
    private void PushWatermark() =>
        _renderer.SetWatermark(_wmSwitch.Checked ? _watermark : null,
            (WatermarkPosition)_wmPosition.SelectedIndex, _wmSize.Value, _wmOpacity.Value);

    private void UpdateWatermarkUi()
    {
        bool on = _wmSwitch.Checked;
        ShowInCard(_wmImageRow, on);
        ShowInCard(_wmPositionRow, on);
        ShowInCard(_wmSizeRow, on);
        ShowInCard(_wmOpacityRow, on);
        _wmChoose.Text = _watermark != null && _settings.WatermarkPath != null
            ? Path.GetFileName(_settings.WatermarkPath)
            : "Choose image…";
        _wmSizeRow.Text = $"Size  {Math.Round(_wmSize.Value * 100)}%";
        _wmOpacityRow.Text = $"Opacity  {Math.Round(_wmOpacity.Value * 100)}%";
    }

    // ───────────────────────────── Video pipeline ─────────────────────────────

    /// <summary>Runs on the receive thread, so a slow PC naturally slows the phone down.</summary>
    private void OnFrame(byte[] jpeg)
    {
        Bitmap? preview = null;
        lock (_pipelineLock)
        {
            try
            {
                _renderer.Render(jpeg, _rotation, _mirror, _fill);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
            {
                return; // corrupt frame — skip it
            }

            var vcam = _vcam;
            if (vcam != null) _renderer.UsePixels(vcam.Send);
            Interlocked.Increment(ref _frameCount);

            long now = Environment.TickCount64;
            if (now - _lastPreviewTicks >= 40) // ~25 fps preview
            {
                _lastPreviewTicks = now;
                preview = _renderer.CreatePreview(1280);
            }
        }
        if (preview != null) OnUi(() => SetPreview(preview), onDropped: preview.Dispose);
    }

    private void RecreateVirtualCamera()
    {
        var (_, w, h) = OutputSizes[_outputSize.SelectedIndex];
        lock (_pipelineLock)
        {
            _vcam?.Dispose();
            _vcam = null;
            _renderer.Dispose();
            _renderer = new FrameRenderer(w, h);
            PushWatermark();
            try
            {
                _vcam = VirtualCamera.Create(w, h, OutputFps);
                _vcamError = null;
            }
            catch (Exception ex)
            {
                _vcamError = ex.Message;
            }
            if (!_phoneConnected) SendPlaceholder();
        }
        UpdateStats();
    }

    /// <summary>Caller holds _pipelineLock. Also shows the placeholder in the preview.</summary>
    private void SendPlaceholder()
    {
        _renderer.RenderMessage("PhoneCam  ·  waiting for iPhone");
        var vcam = _vcam;
        if (vcam != null) _renderer.UsePixels(vcam.Send);
        var preview = _renderer.CreatePreview(1280);
        OnUi(() => SetPreview(preview), onDropped: preview.Dispose);
    }

    private void SetPreview(Bitmap? image)
    {
        var old = _preview.Image;
        _preview.Image = image;
        old?.Dispose();
    }

    private void UpdateOverlay()
    {
        string badge = _phoneConnected ? $"LIVE  ·  {_fps} fps" : "OFFLINE";
        if (!_phoneConnected)
            _preview.SetOverlay(false, badge, "Waiting for your iPhone", _linkState);
        else if (_phoneProblem != null)
            _preview.SetOverlay(true, badge, "iPhone camera", _phoneProblem);
        else
            _preview.SetOverlay(true, badge, null, null);

        _header.SetStatus(_phoneConnected ? "iPhone connected" : "No iPhone connected", _phoneConnected);
        ShowInCard(_orientationNote, _phoneConnected && _orientationNote.Text.Length > 0);
    }

    private static void ShowInCard(Control control, bool visible)
    {
        if (control.Parent is Card card) card.SetVisible(control, visible);
        else control.Visible = visible;
    }

    private void UpdateStats()
    {
        long frames = Interlocked.Read(ref _frameCount);
        _fps = frames - _lastFrameCount;
        _lastFrameCount = frames;
        UpdateOverlay();

        if (_vcamError != null)
        {
            _vcamNote.TextColor = Ui.Error;
            _vcamNote.Text = _vcamError;
        }
        else if (_vcam != null)
        {
            bool inUse = false;
            try { inUse = _vcam.IsInUse; } catch { /* ignore */ }
            _vcamNote.TextColor = inUse ? Ui.Ok : Ui.Muted;
            _vcamNote.Text = inUse
                ? "● In use by an app"
                : "In Teams, Zoom, OBS or your browser, choose the camera “DirectShow Softcam”.";
        }
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private void OnUi(Action action, Action? onDropped = null)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            onDropped?.Invoke();
            return;
        }
        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            onDropped?.Invoke();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.DarkTitleBar(Handle, Ui.Bg);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_phoneConnected)
            lock (_pipelineLock) SendPlaceholder();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        _uiTimer.Stop();
        _link.Dispose();
        lock (_pipelineLock)
        {
            _vcam?.Dispose();
            _vcam = null;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_pipelineLock) _renderer.Dispose();
            _uiTimer.Dispose();
            _preview.Image?.Dispose();
            _watermark?.Dispose();
        }
        base.Dispose(disposing);
    }
}
