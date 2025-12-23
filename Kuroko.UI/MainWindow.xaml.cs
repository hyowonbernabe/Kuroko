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
    private bool _isListening = false;
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
        CheckRagStatus();
    }

    private void CheckRagStatus()
    {
        // Persistence check: If DB exists, we assume RAG is ready
        if (File.Exists("kuroko_rag.db"))
        {
            StatusText.Text = "RAG READY";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 150, 255));
        }
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
        _hotkeyService.Register(helper.Handle, HotkeyService.MOD_ALT, HotkeyService.VK_S);
    }

    // --- MARKDOWN RENDERER ---
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
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };

            string cleanLine = line.Trim();

            if (cleanLine.StartsWith("- ") || cleanLine.StartsWith("* "))
            {
                cleanLine = cleanLine.Substring(2);
                textBlock.Text = "• ";
                textBlock.Foreground = Brushes.White;
            }

            var parts = Regex.Split(cleanLine, @"(\*\*.*?\*\*)");

            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**"))
                {
                    string content = part.Substring(2, part.Length - 4);
                    textBlock.Inlines.Add(new Run(content) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65)) });
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

        AiResponseOverlay.Visibility = Visibility.Visible;
        MarkdownContainer.Children.Clear();
        var loadingText = new TextBlock { Text = "Thinking...", Foreground = Brushes.Gray, FontFamily = new FontFamily("Consolas") };
        MarkdownContainer.Children.Add(loadingText);

        // Get Context
        string context = _fullTranscriptBuffer.Length > 2000
            ? _fullTranscriptBuffer.Substring(_fullTranscriptBuffer.Length - 2000)
            : _fullTranscriptBuffer;

        string ragContext = "";
        try
        {
            if (File.Exists("kuroko_rag.db"))
            {
                if (_vectorDb == null)
                {
                    _vectorDb = new VectorDbService();
                    await _vectorDb.InitializeAsync();
                }
                if (_embeddingService == null) _embeddingService = new EmbeddingService(_apiKey);

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

        if (_aiService == null) _aiService = new AiService(_apiKey);
        string response = await _aiService.GetInterviewAssistanceAsync(context, ragContext);

        RenderMarkdown(response);

        // --- AUTO CLEAR CONTEXT AFTER RESPONSE ---
        // This ensures the next request doesn't include the old conversation
        ClearContext("AUTO");
    }

    private void ClearContext(string reason)
    {
        _fullTranscriptBuffer = "";

        Dispatcher.Invoke(() =>
        {
            TranscriptText.Text += $"\n\n[--- CONTEXT CLEARED ({reason}) ---]\n";
            TranscriptScroller.ScrollToBottom();
        });
    }

    // --- BUTTON EVENTS ---

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            // STOP Logic
            try
            {
                _audioService?.Dispose();
                // Swallow race condition exceptions if processor is busy
                _transcriptionService?.Dispose();
            }
            catch { /* Ignore disposal errors during stop */ }

            _audioService = null;
            _transcriptionService = null;

            _isListening = false;
            BtnStart.Content = "INITIALIZE";
            BtnStart.Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)); // #666
            StatusText.Text = "STOPPED";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(68, 68, 68)); // Dark Gray
            TranscriptText.Text += "\n[SYSTEM STOPPED]";
        }
        else
        {
            // START Logic
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

                _isListening = true;
                BtnStart.IsEnabled = true;
                StatusText.Text = "SYSTEM ACTIVE";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65)); // Neon Green
                BtnStart.Content = "STOP";
                BtnStart.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65));
                TranscriptText.Text += "\n[SYSTEM STARTED - LISTENING...]";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                BtnStart.IsEnabled = true;
                BtnStart.Content = "RETRY";
            }
        }
    }

    private void BtnClearContext_Click(object sender, RoutedEventArgs e)
    {
        ClearContext("MANUAL");
    }

    private async void BtnResetRag_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Are you sure you want to wipe the RAG database? You will need to re-upload your PDF.", "Confirm Reset", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            try
            {
                _vectorDb ??= new VectorDbService();
                await _vectorDb.InitializeAsync();
                await _vectorDb.ClearDatabaseAsync();
                StatusText.Text = "DB CLEARED";
                StatusText.Foreground = Brushes.Gray;
                MessageBox.Show("Database cleared.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error clearing DB: {ex.Message}");
            }
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
                // We do NOT auto-clear here anymore, allowing append if desired, 
                // but user can use RESET DB button to clear first.

                string text = _pdfParser.ExtractTextFromPdf(dialog.FileName);
                var chunks = _pdfParser.ChunkText(text);

                foreach (var chunk in chunks)
                {
                    var vec = await _embeddingService.GenerateEmbeddingAsync(chunk);
                    if (vec.Length > 0) await _vectorDb.InsertChunkAsync(chunk, vec);
                }
                StatusText.Text = "INGESTION COMPLETE";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 150, 255));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void DismissAi_Click(object sender, RoutedEventArgs e) { AiResponseOverlay.Visibility = Visibility.Collapsed; }
    private void ExitButton_Click(object sender, RoutedEventArgs e) { Application.Current.Shutdown(); }

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