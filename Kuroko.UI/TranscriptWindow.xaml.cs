using Kuroko.Audio;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kuroko.UI;

public partial class TranscriptWindow : Window
{
    private AudioCaptureService? _audioService;
    private bool _isDebugRecording = false;

    public TranscriptWindow()
    {
        InitializeComponent();
    }

    public void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Dispatcher.Invoke(() =>
        {
            if (text.StartsWith("[")) LogText.Text += $"\n\n{text}";
            else LogText.Text += $"\n> {text.Trim()}";

            LogScroller.ScrollToBottom();
        });
    }

    public void SetAudioService(AudioCaptureService service)
    {
        _audioService = service;
    }

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

    private void BtnClear_Click(object sender, RoutedEventArgs e) => LogText.Text = "";
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}