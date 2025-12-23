using Kuroko.Audio;
using Kuroko.Core;
using Kuroko.RAG;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

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

    // RAG Services
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

    private string LoadApiKeyFromEnv()
    {
        try
        {
            string path = ".env";
            if (!File.Exists(path))
            {
                var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
                for (int i = 0; i < 4; i++)
                {
                    if (directory?.Parent == null) break;
                    directory = directory.Parent;
                    var check = Path.Combine(directory.FullName, ".env");
                    if (File.Exists(check))
                    {
                        path = check;
                        break;
                    }
                }
            }

            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    if (parts[0].Trim() == "OPENROUTER_API_KEY")
                    {
                        return parts[1].Trim();
                    }
                }
            }
        }
        catch { }
        return "";
    }

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
        bool success = _hotkeyService.Register(helper.Handle, HotkeyService.MOD_ALT, HotkeyService.VK_S);

        if (!success)
        {
            StatusText.Text = "Warning: Hotkey Failed to Register";
        }
    }

    // --- RAG INGESTION LOGIC ---
    private async void BtnIngest_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            MessageBox.Show("Please set OPENROUTER_API_KEY in .env first.");
            return;
        }

        var openFileDialog = new OpenFileDialog
        {
            Filter = "PDF Files (*.pdf)|*.pdf",
            Title = "Select Resume or LinkedIn Profile"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            BtnIngest.IsEnabled = false;
            StatusText.Text = "Parsing PDF...";

            try
            {
                // 1. Initialize RAG Components
                _pdfParser ??= new PdfParserService();
                _vectorDb ??= new VectorDbService();
                _embeddingService ??= new EmbeddingService(_apiKey);

                await _vectorDb.InitializeAsync();

                // Optional: Clear DB for this MVP so we don't mix old resumes
                await _vectorDb.ClearDatabaseAsync();

                // 2. Parse Text
                string fullText = _pdfParser.ExtractTextFromPdf(openFileDialog.FileName);
                if (string.IsNullOrWhiteSpace(fullText))
                {
                    StatusText.Text = "Error: Could not extract text.";
                    BtnIngest.IsEnabled = true;
                    return;
                }

                // 3. Chunk and Embed
                var chunks = _pdfParser.ChunkText(fullText).ToList();
                int count = 0;

                StatusText.Text = $"Embedding {chunks.Count} chunks...";

                foreach (var chunk in chunks)
                {
                    float[] embedding = await _embeddingService.GenerateEmbeddingAsync(chunk);
                    if (embedding.Length > 0)
                    {
                        await _vectorDb.InsertChunkAsync(chunk, embedding);
                        count++;
                    }
                }

                StatusText.Text = $"Ingestion Complete! ({count} chunks)";
                MessageBox.Show($"Successfully indexed {count} chunks from PDF.\nKuroko now knows your resume.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ingestion Error: {ex.Message}");
                StatusText.Text = "Ingestion Failed";
            }
            finally
            {
                BtnIngest.IsEnabled = true;
            }
        }
    }

    // --- MAIN TRIGGER LOGIC ---
    private async void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            AiResponseOverlay.Visibility = Visibility.Visible;
            AiResponseText.Text = "Error: OPENROUTER_API_KEY not found in .env file.";
            return;
        }

        AiResponseOverlay.Visibility = Visibility.Visible;
        AiResponseText.Text = "Kuroko is thinking...";

        // 1. Get Live Context
        string recentTranscript = _fullTranscriptBuffer.Length > 2000
            ? _fullTranscriptBuffer.Substring(_fullTranscriptBuffer.Length - 2000)
            : _fullTranscriptBuffer;

        if (string.IsNullOrWhiteSpace(recentTranscript))
        {
            AiResponseText.Text = "No transcript data available yet.";
            return;
        }

        // 2. Retrieve Relevant Resume Info (RAG)
        string ragContext = "";
        try
        {
            if (_embeddingService == null) _embeddingService = new EmbeddingService(_apiKey);
            if (_vectorDb == null)
            {
                _vectorDb = new VectorDbService();
                await _vectorDb.InitializeAsync();
            }

            // We use the last ~500 chars of the transcript as the "Search Query"
            // This represents the immediate topic/question being asked.
            string query = recentTranscript.Length > 500
                ? recentTranscript.Substring(recentTranscript.Length - 500)
                : recentTranscript;

            float[] queryVec = await _embeddingService.GenerateEmbeddingAsync(query);
            if (queryVec.Length > 0)
            {
                var relevantChunks = await _vectorDb.SearchAsync(queryVec, limit: 3);
                if (relevantChunks.Any())
                {
                    ragContext = string.Join("\n\n", relevantChunks);
                    // Debug indicator to show RAG was used
                    StatusText.Text = "RAG: Context Retrieved";
                }
            }
        }
        catch (Exception)
        {
            // If RAG fails, proceed with just transcript
            StatusText.Text = "RAG: Retrieval Failed";
        }

        // 3. Call AI with Hybrid Context
        if (_aiService == null) _aiService = new AiService(_apiKey);

        // Pass both Transcript AND the Retrieved Resume Chunks
        string response = await _aiService.GetInterviewAssistanceAsync(recentTranscript, ragContext);

        AiResponseText.Text = response;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            this.DragMove();
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        BtnStart.IsEnabled = false;
        BtnStart.Content = "Loading...";
        StatusText.Text = "Loading Whisper Model...";

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

            _audioService.OnAudioDataAvailable += (s, buffer) =>
            {
                float max = 0;
                for (int i = 0; i < buffer.Length; i += 4)
                {
                    if (i + 4 > buffer.Length) break;
                    float sample = BitConverter.ToSingle(buffer, i);
                    float val = Math.Abs(sample);
                    if (val > max) max = val;
                }

                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"Listening | Vol: {(max * 100):0}%";
                });
            };

            await _transcriptionService.InitializeAsync();

            _audioService.StartSystemAudioCapture();

            StatusText.Text = "Listening (System Audio)";
            BtnStart.Content = "Active";
            TranscriptText.Text = "--- Listening to System Audio ---";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
            BtnStart.IsEnabled = true;
            BtnStart.Content = "Retry";
            StatusText.Text = "Error";
        }
    }

    private void DismissAi_Click(object sender, RoutedEventArgs e)
    {
        AiResponseOverlay.Visibility = Visibility.Collapsed;
        AiResponseText.Text = "";
    }

    private void BtnDebug_Click(object sender, RoutedEventArgs e)
    {
        if (_audioService == null) return;

        if (!_isDebugRecording)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "kuroko_debug.wav");
            _audioService.StartDebugRecording(path);
            BtnDebug.Content = "Stop Rec";
            BtnDebug.Background = System.Windows.Media.Brushes.Red;
            _isDebugRecording = true;
            TranscriptText.Text += $"\n[DEBUG] Recording to: {path}";
        }
        else
        {
            _audioService.StopDebugRecording();
            BtnDebug.Content = "Rec Debug";
            BtnDebug.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 68, 68));
            _isDebugRecording = false;
            TranscriptText.Text += "\n[DEBUG] Recording stopped.";
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _hotkeyService?.Dispose();
        _audioService?.Dispose();
        _transcriptionService?.Dispose();
        _vectorDb?.Dispose();
        Application.Current.Shutdown();
    }
}