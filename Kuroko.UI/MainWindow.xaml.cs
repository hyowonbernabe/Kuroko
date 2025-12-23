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

namespace Kuroko.UI;

public partial class MainWindow : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    public static extern uint SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

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
            _aiService?.Dispose();
            _aiService = null;
        };

        // --- TOP MOST LOGIC ---
        _settingsWindow.TopMostChanged += (s, val) =>
        {
            this.Topmost = val;
            if (_transcriptWindow != null) _transcriptWindow.Topmost = val;
            if (_outputWindow != null) _outputWindow.Topmost = val;
            if (_settingsWindow != null) _settingsWindow.Topmost = val;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableStealthMode();
        InitializeGlobalHotkeys();
        PositionToolbar();

        string path = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(path) && File.ReadAllText(path).Contains("WINDOW_TOPMOST=False"))
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
        // Trigger
        _hotkeyService?.Register(1, HotkeyService.MOD_ALT, HotkeyService.VK_S);
        // Panic
        _hotkeyService?.Register(2, HotkeyService.MOD_ALT, HotkeyService.VK_Q);
    }

    private void ToggleWindow(Window? win)
    {
        if (win == null) return;
        if (win.Visibility == Visibility.Visible) win.Hide();
        else
        {
            win.Show();
            win.Activate();
        }
    }

    private void ToggleTranscript_Click(object sender, RoutedEventArgs e) => ToggleWindow(_transcriptWindow);
    private void ToggleOutput_Click(object sender, RoutedEventArgs e) => ToggleWindow(_outputWindow);
    private void ToggleSettings_Click(object sender, RoutedEventArgs e) => ToggleWindow(_settingsWindow);

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

        if (_outputWindow.Visibility != Visibility.Visible) _outputWindow.Show();
        _outputWindow.SetLoading(true);

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

                    if (parts[0].Trim() == "OPENROUTER_API_KEY") _apiKey = parts[1].Trim();
                    if (parts[0].Trim() == "OPENROUTER_MODEL") _modelId = parts[1].Trim();
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