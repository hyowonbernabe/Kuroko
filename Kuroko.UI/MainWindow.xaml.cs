using Kuroko.Audio;
using Kuroko.Core;
using Kuroko.RAG;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
    private PdfParserService? _pdfParser;
    private VectorDbService? _vectorDb;
    private EmbeddingService? _embeddingService;

    private bool _isDebugRecording = false;
    private string _fullTranscriptBuffer = "";
    private string _apiKey = "";

    public MainWindow()
    {
        InitializeComponent();
        _apiKey = LoadApiKeyFromEnv();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableStealthMode();
        InitializeGlobalHotkeys();
    }

    // --- STEALTH & HOTKEY SETUP ---
    private void EnableStealthMode()
    {
        var helper = new WindowInteropHelper(this);
        SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    private void InitializeGlobalHotkeys()
    {
        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnGlobalHotkeyPressed;
        var helper = new WindowInteropHelper(this);
        // Default Alt+S
        _hotkeyService.Register(helper.Handle, HotkeyService.MOD_ALT, HotkeyService.VK_S);
    }

    // --- MARKDOWN RENDERER ---
    // Converts basic AI markdown (* bold, - list) into WPF TextBlocks
    private void RenderMarkdown(string markdown)
    {
        MarkdownContainer.Children.Clear();

        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)), // Off-white
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };

            string cleanLine = line.Trim();

            // 1. Check for Bullet Points
            if (cleanLine.StartsWith("- ") || cleanLine.StartsWith("* "))
            {
                cleanLine = cleanLine.Substring(2);
                textBlock.Text = "• ";
                textBlock.Foreground = Brushes.White;
            }

            // 2. Check for Bold (**text**)
            // Simple regex to split by **
            var parts = Regex.Split(cleanLine, @"(\*\*.*?\*\*)");

            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**"))
                {
                    // Remove asterisks and make bold
                    string content = part.Substring(2, part.Length - 4);
                    textBlock.Inlines.Add(new Run(content) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65)) }); // Neon Green for bold
                }
                else
                {
                    textBlock.Inlines.Add(new Run(part));
                }
            }

            MarkdownContainer.Children.Add(textBlock);
        }
    }

    // --- AI TRIGGER ---
    private async void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            AiResponseOverlay.Visibility = Visibility.Visible;
            RenderMarkdown("**Error:** API Key missing in .env");
            return;
        }

        // Show Loading
        AiResponseOverlay.Visibility = Visibility.Visible;
        MarkdownContainer.Children.Clear();
        var loadingText = new TextBlock { Text = "Thinking...", Foreground = Brushes.Gray, FontFamily = new FontFamily("Consolas") };
        MarkdownContainer.Children.Add(loadingText);

        // Get Context
        string context = _fullTranscriptBuffer.Length > 2000
            ? _fullTranscriptBuffer.Substring(_fullTranscriptBuffer.Length - 2000)
            : _fullTranscriptBuffer;

        // RAG Logic
        string ragContext = "";
        try
        {
            if (_vectorDb != null && _embeddingService == null) _embeddingService = new EmbeddingService(_apiKey);
            if (_vectorDb != null && !string.IsNullOrEmpty(context))
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
        catch { }

        if (_aiService == null) _aiService = new AiService(_apiKey);
        string response = await _aiService.GetInterviewAssistanceAsync(context, ragContext);

        RenderMarkdown(response);
    }

    // --- STANDARD EVENTS ---
    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        BtnStart.IsEnabled = false;
        BtnStart.Content = "LOADING...";
        StatusText.Text = "INITIALIZING";

        try
        {
            _audioService = new AudioCaptureService();
            _transcriptionService = new TranscriptionService(_audioService);

            _transcriptionService.OnSegmentTranscribed += (s, text) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _fullTranscriptBuffer += text + " ";
                    TranscriptText.Text += $"\n> {text.Trim()}";
                    TranscriptScroller.ScrollToBottom();
                });
            };

            await _transcriptionService.InitializeAsync();
            _audioService.StartSystemAudioCapture();

            StatusText.Text = "SYSTEM ACTIVE";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65)); // Neon Green
            BtnStart.Content = "RUNNING";
            TranscriptText.Text = "> System Initialized. Listening...";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
            BtnStart.IsEnabled = true;
            BtnStart.Content = "RETRY";
        }
    }

    private async void BtnIngest_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_apiKey)) { MessageBox.Show("Missing API Key"); return; }

        var dialog = new OpenFileDialog { Filter = "PDF|*.pdf" };
        if (dialog.ShowDialog() == true)
        {
            StatusText.Text = "PARSING PDF...";
            try
            {
                _pdfParser ??= new PdfParserService();
                _vectorDb ??= new VectorDbService();
                _embeddingService ??= new EmbeddingService(_apiKey);

                await _vectorDb.InitializeAsync();
                string text = _pdfParser.ExtractTextFromPdf(dialog.FileName);
                var chunks = _pdfParser.ChunkText(text);

                foreach (var chunk in chunks)
                {
                    var vec = await _embeddingService.GenerateEmbeddingAsync(chunk);
                    if (vec.Length > 0) await _vectorDb.InsertChunkAsync(chunk, vec);
                }
                StatusText.Text = "INGESTION COMPLETE";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void DismissAi_Click(object sender, RoutedEventArgs e) { AiResponseOverlay.Visibility = Visibility.Collapsed; }
    private void ExitButton_Click(object sender, RoutedEventArgs e) { Application.Current.Shutdown(); }

    // Debug & Helpers
    private void BtnDebug_Click(object sender, RoutedEventArgs e)
    {
        if (_audioService == null) return;
        if (!_isDebugRecording)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "kuroko_debug.wav");
            _audioService.StartDebugRecording(path);
            _isDebugRecording = true;
            BtnDebug.Content = "STOP REC";
            BtnDebug.Foreground = Brushes.Red;
        }
        else
        {
            _audioService.StopDebugRecording();
            _isDebugRecording = false;
            BtnDebug.Content = "REC DEBUG";
            BtnDebug.Foreground = new SolidColorBrush(Color.FromRgb(136, 68, 68));
        }
    }

    private string LoadApiKeyFromEnv()
    {
        try
        {
            string path = ".env";
            if (!File.Exists(path))
            {
                var d = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (d != null && !File.Exists(Path.Combine(d.FullName, ".env"))) d = d.Parent;
                if (d != null) path = Path.Combine(d.FullName, ".env");
            }
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("OPENROUTER_API_KEY=")) return line.Split('=', 2)[1].Trim();
                }
            }
        }
        catch { }
        return "";
    }
}