using Kuroko.Audio;
using Kuroko.Core;
using Kuroko.RAG;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Kuroko.UI;

public partial class MainWindow : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // Win32 Constants for ToolWindow (Hides from Task Manager Apps list)
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    public static extern uint SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private AudioCaptureService? _audioService;
    private TranscriptionService? _transcriptionService;
    private HotkeyService? _hotkeyService;
    private AiService? _aiService;

    private VectorDbService? _vectorDb;
    private EmbeddingService? _embeddingService;

    private TranscriptWindow? _transcriptWindow;
    private OutputWindow? _outputWindow;
    private SettingsWindow? _settingsWindow;

    private bool _isListening = false;
    private string _fullTranscriptBuffer = "";
    private string _apiKey = "";
    private string _modelId = "";

    private bool _transcriptPositioned = false;
    private bool _outputPositioned = false;
    private bool _settingsPositioned = false;

    private string _hotkeyTriggerRaw = "Alt + S";
    private string _hotkeyPanicRaw = "Alt + Q";

    private string _decoyTitle = "Host Process";
    private string _decoyIconPath = "";
    private bool _deepStealthEnabled = false;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettingsFromEnv();
        InitializeWindows();
    }

    private void InitializeWindows()
    {
        _transcriptWindow = new TranscriptWindow();
        _outputWindow = new OutputWindow();
        _settingsWindow = new SettingsWindow();

        _settingsWindow.SettingsUpdated += (s, e) =>
        {
            LoadSettingsFromEnv();
            RegisterHotkeys();
            ApplyDeepStealth(_deepStealthEnabled);
            _aiService?.Dispose();
            _aiService = null;
        };

        _settingsWindow.DecoyUpdated += (s, e) =>
        {
            LoadSettingsFromEnv();
            ApplyDeepStealth(_deepStealthEnabled);
        };

        _settingsWindow.ApiKeyUpdated += (s, key) => _apiKey = key;

        _settingsWindow.TopMostChanged += (s, val) =>
        {
            this.Topmost = val;
            if (_transcriptWindow != null) _transcriptWindow.Topmost = val;
            if (_outputWindow != null) _outputWindow.Topmost = val;
            if (_settingsWindow != null) _settingsWindow.Topmost = val;
        };

        _settingsWindow.DeepStealthChanged += (s, val) => ApplyDeepStealth(val);

        _settingsWindow.ResetLayoutRequested += (s, e) => ResetWindowPositions();
    }

    // Helper to apply WS_EX_TOOLWINDOW style
    private void SetToolWindowStyle(Window window, bool enable)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero) return;

            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);

            if (enable)
            {
                // Apply ToolWindow style (Hides from Alt-Tab and Task Manager "Apps")
                SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            }
            else
            {
                // Remove ToolWindow style
                SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle & ~WS_EX_TOOLWINDOW);
            }
        }
        catch { }
    }

    private void ApplyDeepStealth(bool enable)
    {
        _deepStealthEnabled = enable;

        // 1. Toggle Taskbar Visibility (WPF Property)
        bool showInTaskbar = !enable;
        this.ShowInTaskbar = showInTaskbar;

        if (_transcriptWindow != null) _transcriptWindow.ShowInTaskbar = showInTaskbar;
        if (_outputWindow != null) _outputWindow.ShowInTaskbar = showInTaskbar;
        if (_settingsWindow != null) _settingsWindow.ShowInTaskbar = showInTaskbar;

        // 2. Apply ToolWindow Style (Win32 API) - Forces "Background Process" status
        SetToolWindowStyle(this, enable);
        if (_transcriptWindow != null) SetToolWindowStyle(_transcriptWindow, enable);
        if (_outputWindow != null) SetToolWindowStyle(_outputWindow, enable);
        if (_settingsWindow != null) SetToolWindowStyle(_settingsWindow, enable);

        // 3. Resolve Decoy Assets
        string targetTitle = enable ? _decoyTitle : "Kuroko Toolbar";
        ImageSource? targetIcon = null;

        if (enable && !string.IsNullOrEmpty(_decoyIconPath) && File.Exists(_decoyIconPath))
        {
            try
            {
                Uri iconUri = new Uri(_decoyIconPath, UriKind.Absolute);
                targetIcon = new BitmapImage(iconUri);
            }
            catch { }
        }

        // 4. Apply Title to ALL windows
        this.Title = targetTitle;
        if (_transcriptWindow != null) _transcriptWindow.Title = enable ? _decoyTitle : "Live Log";
        if (_outputWindow != null) _outputWindow.Title = enable ? _decoyTitle : "AI Output";
        if (_settingsWindow != null) _settingsWindow.Title = enable ? _decoyTitle : "Settings";

        // 5. Apply Icon to ALL windows
        this.Icon = targetIcon;
        if (_transcriptWindow != null) _transcriptWindow.Icon = targetIcon;
        if (_outputWindow != null) _outputWindow.Icon = targetIcon;
        if (_settingsWindow != null) _settingsWindow.Icon = targetIcon;
    }

    private void ResetWindowPositions()
    {
        var area = SystemParameters.WorkArea;

        PositionToolbar();

        if (_transcriptWindow != null)
        {
            _transcriptWindow.Left = area.Left + 20;
            _transcriptWindow.Top = area.Top + 20;
            _transcriptPositioned = true;
        }

        if (_outputWindow != null)
        {
            _outputWindow.Left = area.Left + 20 + 350 + 10;
            _outputWindow.Top = area.Top + 20;
            _outputPositioned = true;
        }

        if (_settingsWindow != null)
        {
            _settingsWindow.Left = area.Right - 400 - 20;
            _settingsWindow.Top = area.Top + 20;
            _settingsPositioned = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableStealthMode();
        InitializeGlobalHotkeys();
        PositionToolbar();

        string path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(path))
        {
            string content = File.ReadAllText(path);

            if (content.Contains("WINDOW_TOPMOST=False"))
            {
                this.Topmost = false;
                _transcriptWindow!.Topmost = false;
                _outputWindow!.Topmost = false;
            }
            else
            {
                this.Topmost = true;
                _transcriptWindow!.Topmost = true;
                _outputWindow!.Topmost = true;
            }

            if (content.Contains("DEEP_STEALTH=True"))
            {
                ApplyDeepStealth(true);
            }
        }
        else
        {
            this.Topmost = true;
            _transcriptWindow!.Topmost = true;
            _outputWindow!.Topmost = true;
        }
    }

    private void PositionToolbar()
    {
        var workArea = SystemParameters.WorkArea;
        this.Left = workArea.Left + 20;
        this.Top = workArea.Bottom - this.Height - 20;
    }

    private void EnableStealthMode()
    {
        var helper = new WindowInteropHelper(this);
        SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    private void InitializeGlobalHotkeys()
    {
        _hotkeyService = new HotkeyService();
        var helper = new WindowInteropHelper(this);
        _hotkeyService.Initialize(helper.Handle);
        _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;

        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        _hotkeyService?.UnregisterAll();

        var (mod1, key1) = ParseHotkey(_hotkeyTriggerRaw, HotkeyService.MOD_ALT, HotkeyService.VK_S);
        _hotkeyService?.Register(1, mod1, key1);

        var (mod2, key2) = ParseHotkey(_hotkeyPanicRaw, HotkeyService.MOD_ALT, HotkeyService.VK_Q);
        _hotkeyService?.Register(2, mod2, key2);
    }

    private (uint mod, uint key) ParseHotkey(string raw, uint defaultMod, uint defaultKey)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (defaultMod, defaultKey);

        uint mod = 0;
        uint key = 0;

        var parts = raw.Split('+');
        foreach (var part in parts)
        {
            string p = part.Trim().ToUpper();
            if (p == "CTRL" || p == "CONTROL") mod |= HotkeyService.MOD_CONTROL;
            else if (p == "ALT") mod |= HotkeyService.MOD_ALT;
            else if (p == "SHIFT") mod |= HotkeyService.MOD_SHIFT;
            else if (p == "WIN") mod |= HotkeyService.MOD_WIN;
            else
            {
                try
                {
                    Key k = (Key)Enum.Parse(typeof(Key), p, true);
                    key = (uint)KeyInterop.VirtualKeyFromKey(k);
                }
                catch { }
            }
        }

        if (key == 0) return (defaultMod, defaultKey);
        return (mod, key);
    }

    private void ToggleWindow(Window? win)
    {
        if (win == null) return;
        if (win.Visibility == Visibility.Visible) win.Hide();
        else
        {
            win.Show();
            win.Activate();
            // Ensure style is reapplied when window is shown
            if (_deepStealthEnabled) SetToolWindowStyle(win, true);
        }
    }

    private void ToggleTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (!_transcriptPositioned && _transcriptWindow != null)
        {
            var area = SystemParameters.WorkArea;
            _transcriptWindow.Left = area.Left + 20;
            _transcriptWindow.Top = area.Top + 20;
            _transcriptPositioned = true;
        }
        ToggleWindow(_transcriptWindow);
    }

    private void ToggleOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!_outputPositioned && _outputWindow != null)
        {
            var area = SystemParameters.WorkArea;
            _outputWindow.Left = area.Left + 20 + 350 + 10;
            _outputWindow.Top = area.Top + 20;
            _outputPositioned = true;
        }
        ToggleWindow(_outputWindow);
    }

    private void ToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_settingsPositioned && _settingsWindow != null)
        {
            var area = SystemParameters.WorkArea;
            _settingsWindow.Left = area.Right - 400 - 20;
            _settingsWindow.Top = area.Top + 20;
            _settingsPositioned = true;
        }
        ToggleWindow(_settingsWindow);
    }

    private async void BtnInit_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            try
            {
                _audioService?.Dispose();
                _transcriptionService?.Dispose();
            }
            catch { }
            _audioService = null; _transcriptionService = null;
            _isListening = false;

            BtnInit.Content = "INITIALIZE";
            BtnInit.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            StatusText.Text = "OFFLINE";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102));
            StatusIndicator.Background = new SolidColorBrush(Color.FromRgb(68, 68, 68));
            ((DropShadowEffect)StatusIndicator.Effect).Color = Color.FromRgb(68, 68, 68);

            _transcriptWindow?.AppendLog("[SYSTEM STOPPED]");
        }
        else
        {
            BtnInit.Content = "...";
            StatusText.Text = "LOADING";
            try
            {
                _audioService = new AudioCaptureService();
                _transcriptionService = new TranscriptionService(_audioService);

                _transcriptionService.OnSegmentTranscribed += (s, text) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _fullTranscriptBuffer += text + " ";
                        _transcriptWindow?.AppendLog(text);
                    });
                };

                await _transcriptionService.InitializeAsync();
                _audioService.StartSystemAudioCapture();

                _transcriptWindow?.SetAudioService(_audioService);

                _isListening = true;
                BtnInit.Content = "STOP";
                BtnInit.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65));
                StatusText.Text = "ACTIVE";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65));
                StatusIndicator.Background = new SolidColorBrush(Color.FromRgb(0, 255, 65));
                ((DropShadowEffect)StatusIndicator.Effect).Color = Color.FromRgb(0, 255, 65);

                _transcriptWindow?.AppendLog("[SYSTEM ACTIVE - LISTENING]");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                BtnInit.Content = "INITIALIZE";
            }
        }
    }

    private async void OnGlobalHotkeyPressed(object? sender, int id)
    {
        if (id == 2)
        {
            Application.Current.Shutdown();
            return;
        }

        if (_outputWindow == null) return;

        if (!_outputPositioned)
        {
            var area = SystemParameters.WorkArea;
            _outputWindow.Left = area.Left + 20 + 350 + 10;
            _outputWindow.Top = area.Top + 20;
            _outputPositioned = true;
        }

        if (_outputWindow.Visibility != Visibility.Visible) _outputWindow.Show();
        _outputWindow.SetLoading(true);
        if (_deepStealthEnabled) SetToolWindowStyle(_outputWindow, true); // Ensure style applied on hotkey show

        if (string.IsNullOrEmpty(_apiKey))
        {
            _outputWindow.RenderResponse("**Error:** API Key missing.");
            return;
        }

        string context = _fullTranscriptBuffer.Length > 2000
            ? _fullTranscriptBuffer.Substring(_fullTranscriptBuffer.Length - 2000)
            : _fullTranscriptBuffer;

        string ragContext = "";
        try
        {
            if (File.Exists("kuroko_rag.db"))
            {
                _vectorDb ??= new VectorDbService();
                await _vectorDb.InitializeAsync();
                _embeddingService ??= new EmbeddingService(_apiKey);

                if (!string.IsNullOrEmpty(context))
                {
                    string query = context.Length > 200 ? context.Substring(context.Length - 200) : context;
                    var vec = await _embeddingService!.GenerateEmbeddingAsync(query);
                    if (vec.Length > 0)
                    {
                        var results = await _vectorDb.SearchAsync(vec);
                        ragContext = string.Join("\n", results);
                    }
                }
            }
        }
        catch { }

        if (_aiService == null) _aiService = new AiService(_apiKey, _modelId);

        string response = await _aiService.GetInterviewAssistanceAsync(context, ragContext);

        _outputWindow.RenderResponse(response);
        _fullTranscriptBuffer = "";
        _transcriptWindow?.AppendLog("\n[--- CONTEXT CLEARED (AUTO) ---]");
    }

    private void LoadSettingsFromEnv()
    {
        try
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var k = parts[0].Trim();
                    var v = parts[1].Trim();

                    if (k == "OPENROUTER_API_KEY") _apiKey = v;
                    if (k == "OPENROUTER_MODEL") _modelId = v;
                    if (k == "HOTKEY_TRIGGER_TXT") _hotkeyTriggerRaw = v;
                    if (k == "HOTKEY_PANIC_TXT") _hotkeyPanicRaw = v;

                    if (k == "DECOY_TITLE") _decoyTitle = v;
                    if (k == "DECOY_ICON") _decoyIconPath = v;
                }
            }
        }
        catch { }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _hotkeyService?.Dispose();
        _transcriptWindow?.Close();
        _outputWindow?.Close();
        _settingsWindow?.Close();
        Application.Current.Shutdown();
    }
}