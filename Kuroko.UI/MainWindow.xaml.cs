using Kuroko.Audio;
using System.IO;
using System.Runtime.InteropServices;
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
    private bool _isDebugRecording = false;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnableStealthMode();
    }

    private void EnableStealthMode()
    {
        var helper = new WindowInteropHelper(this);
        SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            this.DragMove();
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        BtnStart.IsEnabled = false;
        BtnStart.Content = "Loading Model...";
        StatusText.Text = "Downloading/Loading Whisper Model...";

        try
        {
            _audioService = new AudioCaptureService();
            _transcriptionService = new TranscriptionService(_audioService);

            _transcriptionService.OnSegmentTranscribed += (s, text) =>
            {
                if (text.Contains("[BLANK_AUDIO]")) return;

                Dispatcher.Invoke(() =>
                {
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
            TranscriptText.Text = "--- Listening to System Audio (En/Buffered) ---";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
            BtnStart.IsEnabled = true;
            BtnStart.Content = "Retry";
            StatusText.Text = "Error";
        }
    }

    private void BtnDebug_Click(object sender, RoutedEventArgs e)
    {
        if (_audioService == null) return;

        if (!_isDebugRecording)
        {
            // Start Recording
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "kuroko_debug.wav");
            _audioService.StartDebugRecording(path);
            BtnDebug.Content = "Stop Rec";
            BtnDebug.Background = System.Windows.Media.Brushes.Red;
            _isDebugRecording = true;
            TranscriptText.Text += $"\n[DEBUG] Recording to: {path}";
        }
        else
        {
            // Stop Recording
            _audioService.StopDebugRecording();
            BtnDebug.Content = "Rec Debug";
            BtnDebug.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 68, 68)); // #444444
            _isDebugRecording = false;
            TranscriptText.Text += "\n[DEBUG] Recording stopped.";
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _audioService?.Dispose();
        _transcriptionService?.Dispose();
        Application.Current.Shutdown();
    }
}