using System.Drawing;
using System.Text.Json;

namespace PhoneCamDesktop;

internal sealed class MainForm : Form
{
    private static readonly (string Label, int W, int H)[] OutputSizes =
    {
        ("1280 × 720 (recommended)", 1280, 720),
        ("1920 × 1080", 1920, 1080),
        ("640 × 480", 640, 480),
    };

    private static readonly (string Label, double Value)[] Qualities =
    {
        ("Low (smallest, fastest)", 0.4),
        ("Medium", 0.6),
        ("High", 0.8),
    };

    private const float OutputFps = 30f;

    private readonly PhoneLink _link = new();
    private readonly object _pipelineLock = new();
    private FrameRenderer _renderer;
    private VirtualCamera? _vcam;
    private string? _vcamError;

    // Picture settings, read from the receive thread.
    private volatile int _rotation;
    private volatile bool _mirror;
    private volatile bool _fill = true;

    private long _frameCount;
    private long _lastFrameCount;
    private long _lastPreviewTicks;
    private bool _phoneConnected;
    private bool _applyingPhoneStatus;

    // UI
    private readonly Label _phoneStatus = new() { AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly Label _fpsLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly RadioButton _backCam = new() { Text = "Back camera", AutoSize = true, Checked = true };
    private readonly RadioButton _frontCam = new() { Text = "Front camera", AutoSize = true };
    private readonly ComboBox _phoneResolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly ComboBox _rotationBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly CheckBox _mirrorBox = new() { Text = "Mirror image", AutoSize = true };
    private readonly ComboBox _fitBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly ComboBox _outputSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly Label _vcamStatus = new() { AutoSize = true, MaximumSize = new Size(300, 0) };
    private readonly PictureBox _preview = new()
    {
        Dock = DockStyle.Fill,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.Black,
    };
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    public MainForm()
    {
        Text = "PhoneCam — iPhone as webcam";
        ClientSize = new Size(1040, 620);
        MinimumSize = new Size(820, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        _renderer = new FrameRenderer(OutputSizes[0].W, OutputSizes[0].H);
        BuildLayout();
        WireEvents();

        _outputSize.SelectedIndex = 0; // creates the virtual camera
        _link.Start();
        _uiTimer.Start();
    }

    // ───────────────────────────── Layout ─────────────────────────────

    private void BuildLayout()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(14, 12, 10, 12),
        };

        panel.Controls.Add(Heading("iPhone"));
        _phoneStatus.Text = "Starting…";
        panel.Controls.Add(_phoneStatus);
        panel.Controls.Add(_fpsLabel);

        panel.Controls.Add(Heading("Camera"));
        var camRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        camRow.Controls.Add(_backCam);
        camRow.Controls.Add(_frontCam);
        panel.Controls.Add(camRow);

        panel.Controls.Add(SubLabel("Phone resolution"));
        _phoneResolution.Items.AddRange(new object[] { "720p", "1080p" });
        _phoneResolution.SelectedIndex = 0;
        panel.Controls.Add(_phoneResolution);

        panel.Controls.Add(SubLabel("Image quality"));
        foreach (var q in Qualities) _quality.Items.Add(q.Label);
        _quality.SelectedIndex = 1;
        panel.Controls.Add(_quality);

        panel.Controls.Add(Heading("Picture"));
        panel.Controls.Add(SubLabel("Rotation"));
        _rotationBox.Items.AddRange(new object[] { "0°", "90°", "180°", "270°" });
        _rotationBox.SelectedIndex = 0;
        panel.Controls.Add(_rotationBox);
        panel.Controls.Add(SubLabel("Framing"));
        _fitBox.Items.AddRange(new object[] { "Fill frame (crop edges)", "Fit whole image (black bars)" });
        _fitBox.SelectedIndex = 0;
        panel.Controls.Add(_fitBox);
        panel.Controls.Add(_mirrorBox);

        panel.Controls.Add(Heading("Virtual webcam"));
        panel.Controls.Add(SubLabel("Output size"));
        foreach (var s in OutputSizes) _outputSize.Items.Add(s.Label);
        panel.Controls.Add(_outputSize);
        panel.Controls.Add(_vcamStatus);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(panel, 0, 0);
        layout.Controls.Add(_preview, 1, 0);
        Controls.Add(layout);
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 11f),
        Margin = new Padding(0, 14, 0, 4),
    };

    private static Label SubLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 2),
    };

    // ───────────────────────────── Events ─────────────────────────────

    private void WireEvents()
    {
        _backCam.CheckedChanged += (_, _) => { if (_backCam.Checked) SendToPhone("camera", "back"); };
        _frontCam.CheckedChanged += (_, _) => { if (_frontCam.Checked) SendToPhone("camera", "front"); };
        _phoneResolution.SelectedIndexChanged += (_, _) =>
            SendToPhone("resolution", (string)_phoneResolution.SelectedItem!);
        _quality.SelectedIndexChanged += (_, _) =>
            SendToPhone("quality", Qualities[Math.Max(0, _quality.SelectedIndex)].Value);

        _rotationBox.SelectedIndexChanged += (_, _) => _rotation = _rotationBox.SelectedIndex * 90;
        _mirrorBox.CheckedChanged += (_, _) => _mirror = _mirrorBox.Checked;
        _fitBox.SelectedIndexChanged += (_, _) => _fill = _fitBox.SelectedIndex == 0;
        _outputSize.SelectedIndexChanged += (_, _) => RecreateVirtualCamera();

        _link.StateChanged += s => OnUi(() => _phoneStatus.Text = s);
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
            SendToPhone("camera", _frontCam.Checked ? "front" : "back");
            SendToPhone("resolution", (string)_phoneResolution.SelectedItem!);
            SendToPhone("quality", Qualities[Math.Max(0, _quality.SelectedIndex)].Value);
        }
        else
        {
            lock (_pipelineLock) SendPlaceholder();
            SetPreview(null);
        }
    }

    /// <summary>The phone reports its state (e.g. after someone taps Front/Back on the phone).</summary>
    private void ApplyPhoneStatus(JsonElement status)
    {
        _applyingPhoneStatus = true;
        try
        {
            if (status.TryGetProperty("camera", out var cam))
            {
                bool front = cam.GetString() == "front";
                _frontCam.Checked = front;
                _backCam.Checked = !front;
            }
            if (status.TryGetProperty("resolution", out var res))
            {
                int i = _phoneResolution.Items.IndexOf(res.GetString() ?? "");
                if (i >= 0) _phoneResolution.SelectedIndex = i;
            }
            if (status.TryGetProperty("quality", out var q) && q.TryGetDouble(out double qv))
            {
                int nearest = 0;
                for (int i = 1; i < Qualities.Length; i++)
                    if (Math.Abs(Qualities[i].Value - qv) < Math.Abs(Qualities[nearest].Value - qv)) nearest = i;
                _quality.SelectedIndex = nearest;
            }
            if (status.TryGetProperty("error", out var err) && !string.IsNullOrEmpty(err.GetString()))
                _phoneStatus.Text = "iPhone: " + err.GetString();
            else if (status.TryGetProperty("cameraAuthorized", out var auth) && auth.ValueKind == JsonValueKind.False)
                _phoneStatus.Text = "Allow camera access for PhoneCam on the iPhone (Settings › PhoneCam).";
        }
        finally
        {
            _applyingPhoneStatus = false;
        }
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
            if (now - _lastPreviewTicks >= 50) // ~20 fps preview is plenty
            {
                _lastPreviewTicks = now;
                preview = _renderer.CreatePreview(960);
            }
        }
        if (preview != null) OnUi(() => SetPreview(preview), onDropped: preview.Dispose);
    }

    private void RecreateVirtualCamera()
    {
        var (_, w, h) = OutputSizes[Math.Max(0, _outputSize.SelectedIndex)];
        lock (_pipelineLock)
        {
            _vcam?.Dispose();
            _vcam = null;
            _renderer.Dispose();
            _renderer = new FrameRenderer(w, h);
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

    /// <summary>Caller holds _pipelineLock.</summary>
    private void SendPlaceholder()
    {
        _renderer.RenderMessage("Waiting for iPhone…\nOpen PhoneCam on your phone");
        var vcam = _vcam;
        if (vcam != null) _renderer.UsePixels(vcam.Send);
    }

    private void SetPreview(Bitmap? image)
    {
        var old = _preview.Image;
        _preview.Image = image;
        old?.Dispose();
    }

    private void UpdateStats()
    {
        long frames = Interlocked.Read(ref _frameCount);
        long fps = frames - _lastFrameCount;
        _lastFrameCount = frames;
        _fpsLabel.Text = _phoneConnected ? $"{fps} frames/s" : "";

        if (_vcamError != null)
        {
            _vcamStatus.ForeColor = Color.Firebrick;
            _vcamStatus.Text = _vcamError;
        }
        else if (_vcam != null)
        {
            bool inUse = false;
            try { inUse = _vcam.IsInUse; } catch { /* ignore */ }
            _vcamStatus.ForeColor = SystemColors.ControlText;
            _vcamStatus.Text = inUse
                ? "“DirectShow Softcam” is in use by an app ✓"
                : "Ready. In Zoom, Teams, OBS or your browser, choose the camera “DirectShow Softcam”.";
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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
        }
        base.Dispose(disposing);
    }
}
