using Kuroko.Audio; // For TranscriptionService constants
using Kuroko.Core;
using Kuroko.RAG;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Kuroko.UI;

public partial class SettingsWindow : Window
{
    // Events to notify Main Window
    public event EventHandler<bool>? TopMostChanged;
    public event EventHandler<bool>? DeepStealthChanged;
    public event EventHandler<bool>? ScreenShareProtectionChanged;
    public event EventHandler? SettingsUpdated;
    public event EventHandler<string>? ApiKeyUpdated;
    public event EventHandler? ResetLayoutRequested;
    public event EventHandler? DecoyUpdated;

    private string _envPath;

    public SettingsWindow()
    {
        InitializeComponent();
        _envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        LoadSettings();
        RefreshFileList();
    }

    private void LoadSettings()
    {
        try
        {
            // Defaults
            ChkTopMost.IsChecked = true;
            ChkDeepStealth.IsChecked = false;
            ChkScreenShareProtection.IsChecked = true;

            TxtDecoyTitle.Text = "Host Process";
            TxtDecoyIcon.Text = "";
            TxtSystemPrompt.Text = AiService.DefaultSystemPrompt;

            // Transcriber Defaults
            TxtTransSilence.Text = TranscriptionService.DefaultSilenceCutoffMs.ToString();
            TxtTransMax.Text = TranscriptionService.DefaultMaxChunkDurationSec.ToString();
            TxtTransVol.Text = TranscriptionService.DefaultVolumeThreshold.ToString();

            if (!File.Exists(_envPath)) return;

            var lines = File.ReadAllLines(_envPath);
            foreach (var line in lines)
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                var k = parts[0].Trim();
                var v = parts[1].Trim();

                if (k == "OPENROUTER_API_KEY") TxtApiKey.Password = v;
                if (k == "OPENROUTER_MODEL") CmbModel.Text = v;
                if (k == "WINDOW_TOPMOST") ChkTopMost.IsChecked = bool.Parse(v);
                if (k == "DEEP_STEALTH") ChkDeepStealth.IsChecked = bool.Parse(v);
                if (k == "SCREEN_SHARE_PROTECTION") ChkScreenShareProtection.IsChecked = bool.Parse(v);

                if (k == "HOTKEY_TRIGGER_TXT") TxtHotkeyTrigger.Text = v;
                if (k == "HOTKEY_PANIC_TXT") TxtHotkeyPanic.Text = v;
                if (k == "HOTKEY_CLEAR_TXT") TxtHotkeyClear.Text = v;

                if (k == "DECOY_TITLE") TxtDecoyTitle.Text = v;
                if (k == "DECOY_ICON") TxtDecoyIcon.Text = v;

                if (k == "SYSTEM_PROMPT") TxtSystemPrompt.Text = v.Replace("\\n", "\n");

                // Transcription
                if (k == "TRANS_SILENCE_MS") TxtTransSilence.Text = v;
                if (k == "TRANS_MAX_SEC") TxtTransMax.Text = v;
                if (k == "TRANS_VOL_THRES") TxtTransVol.Text = v;
            }

            if (string.IsNullOrEmpty(CmbModel.Text)) CmbModel.Text = "google/gemma-3-27b-it:free";
        }
        catch { }
    }

    private void SaveSettings()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OPENROUTER_API_KEY={TxtApiKey.Password}");
        sb.AppendLine($"OPENROUTER_MODEL={CmbModel.Text}");
        sb.AppendLine($"WINDOW_TOPMOST={ChkTopMost.IsChecked}");
        sb.AppendLine($"DEEP_STEALTH={ChkDeepStealth.IsChecked}");
        sb.AppendLine($"SCREEN_SHARE_PROTECTION={ChkScreenShareProtection.IsChecked}");

        sb.AppendLine($"HOTKEY_TRIGGER_TXT={TxtHotkeyTrigger.Text}");
        sb.AppendLine($"HOTKEY_PANIC_TXT={TxtHotkeyPanic.Text}");
        sb.AppendLine($"HOTKEY_CLEAR_TXT={TxtHotkeyClear.Text}");

        sb.AppendLine($"DECOY_TITLE={TxtDecoyTitle.Text}");
        sb.AppendLine($"DECOY_ICON={TxtDecoyIcon.Text}");

        string sanitizedPrompt = TxtSystemPrompt.Text.Replace("\r", "").Replace("\n", "\\n");
        sb.AppendLine($"SYSTEM_PROMPT={sanitizedPrompt}");

        // Transcription Settings
        sb.AppendLine($"TRANS_SILENCE_MS={TxtTransSilence.Text}");
        sb.AppendLine($"TRANS_MAX_SEC={TxtTransMax.Text}");
        sb.AppendLine($"TRANS_VOL_THRES={TxtTransVol.Text}");

        File.WriteAllText(_envPath, sb.ToString());

        SettingsUpdated?.Invoke(this, EventArgs.Empty);
        ApiKeyUpdated?.Invoke(this, TxtApiKey.Password);
        DecoyUpdated?.Invoke(this, EventArgs.Empty);
    }

    // --- EVENT HANDLERS ---
    private void BtnSaveAi_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        MessageBox.Show("Settings Saved.");
    }

    private void BtnResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        TxtSystemPrompt.Text = AiService.DefaultSystemPrompt;
    }

    private void BtnResetTrans_Click(object sender, RoutedEventArgs e)
    {
        TxtTransSilence.Text = TranscriptionService.DefaultSilenceCutoffMs.ToString();
        TxtTransMax.Text = TranscriptionService.DefaultMaxChunkDurationSec.ToString();
        TxtTransVol.Text = TranscriptionService.DefaultVolumeThreshold.ToString();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (sender == ChkTopMost) TopMostChanged?.Invoke(this, ChkTopMost.IsChecked == true);
        if (sender == ChkDeepStealth) DeepStealthChanged?.Invoke(this, ChkDeepStealth.IsChecked == true);
        if (sender == ChkScreenShareProtection) ScreenShareProtectionChanged?.Invoke(this, ChkScreenShareProtection.IsChecked == true);
    }

    private void Setting_Changed(object sender, TextChangedEventArgs e) { }

    private void BtnResetLayout_Click(object sender, RoutedEventArgs e) => ResetLayoutRequested?.Invoke(this, EventArgs.Empty);
    private void BtnApplyDecoy_Click(object sender, RoutedEventArgs e) { SaveSettings(); MessageBox.Show("Decoy Settings Applied."); }
    private void BtnResetDecoy_Click(object sender, RoutedEventArgs e) { TxtDecoyTitle.Text = "Host Process"; TxtDecoyIcon.Text = ""; SaveSettings(); MessageBox.Show("Decoy Reset."); }

    private void BtnBrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Icon Files|*.ico;*.png;*.jpg" };
        if (dlg.ShowDialog() == true) TxtDecoyIcon.Text = dlg.FileName;
    }

    private void Hotkeybox_KeyDown(object sender, KeyEventArgs e)
    {
        var textBox = sender as TextBox;
        if (textBox == null) return;
        e.Handled = true;
        var key = (e.Key == Key.System ? e.SystemKey : e.Key);
        if (key == Key.LeftShift || key == Key.RightShift || key == Key.LeftCtrl ||
            key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var hotkeyStr = "";
        if (modifiers.HasFlag(ModifierKeys.Control)) hotkeyStr += "Ctrl + ";
        if (modifiers.HasFlag(ModifierKeys.Shift)) hotkeyStr += "Shift + ";
        if (modifiers.HasFlag(ModifierKeys.Alt)) hotkeyStr += "Alt + ";
        hotkeyStr += key.ToString();
        textBox.Text = hotkeyStr;
        SaveSettings();
    }

    private async void RefreshFileList()
    {
        try
        {
            if (!File.Exists("kuroko_rag.db")) return;
            using var db = new VectorDbService();
            await db.InitializeAsync();
            LstFiles.ItemsSource = await db.GetSourcesAsync();
        }
        catch { }
    }

    private async void BtnIngest_Click(object sender, RoutedEventArgs e)
    {
        string apiKey = TxtApiKey.Password;
        if (string.IsNullOrEmpty(apiKey)) { MessageBox.Show("Save API Key first"); return; }
        var dialog = new OpenFileDialog { Filter = "PDF|*.pdf" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var pdfParser = new PdfParserService();
                using var vectorDb = new VectorDbService();
                var embeddingService = new EmbeddingService(apiKey);
                await vectorDb.InitializeAsync();
                string text = pdfParser.ExtractTextFromPdf(dialog.FileName);
                var chunks = pdfParser.ChunkText(text);
                string filename = Path.GetFileName(dialog.FileName);
                foreach (var chunk in chunks)
                {
                    var vec = await embeddingService.GenerateEmbeddingAsync(chunk);
                    if (vec.Length > 0) await vectorDb.InsertChunkAsync(chunk, vec, filename);
                }
                RefreshFileList();
                MessageBox.Show("Uploaded.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
    }

    private async void BtnDeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filename)
        {
            if (MessageBox.Show($"Delete {filename}?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                using var db = new VectorDbService();
                await db.InitializeAsync();
                await db.DeleteSourceAsync(filename);
                RefreshFileList();
            }
        }
    }

    private async void BtnWipe_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete ALL database data?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            using var db = new VectorDbService();
            await db.InitializeAsync();
            await db.ClearDatabaseAsync();
            RefreshFileList();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Hide();
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}