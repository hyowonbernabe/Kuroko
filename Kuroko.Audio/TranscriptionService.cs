using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace Kuroko.Audio;

public class TranscriptionService : IDisposable
{
    private readonly AudioCaptureService _captureService;
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;
    private readonly List<byte> _buffer = new();
    private string _lastSegmentTail = string.Empty; // Store end of last segment for de-duplication

    public event EventHandler<string>? OnSegmentTranscribed;

    public TranscriptionService(AudioCaptureService captureService)
    {
        _captureService = captureService;
        _captureService.OnAudioDataAvailable += OnAudioDataReceived;
    }

    public async Task InitializeAsync()
    {
        string modelName = "ggml-base.bin";
        if (!File.Exists(modelName))
        {
            using var stream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base);
            using var file = File.OpenWrite(modelName);
            await stream.CopyToAsync(file);
        }
        _factory = WhisperFactory.FromPath(modelName);
        _processor = _factory.CreateBuilder().WithLanguage("en").Build();
    }

    private void OnAudioDataReceived(object? sender, byte[] data)
    {
        var format = _captureService.CurrentFormat;
        if (format == null) return;

        lock (_buffer)
        {
            _buffer.AddRange(data);

            // Sliding Window Logic:
            // 1. Window Size: 3.0 seconds (Context for the AI)
            // 2. Step Size:   2.5 seconds (How often we fire)
            // 3. Overlap:     0.5 seconds (Prevents cut-off words at boundaries)

            int bytesPerSec = format.AverageBytesPerSecond;
            int windowBytes = bytesPerSec * 3;       // 3.0s
            int stepBytes = (int)(bytesPerSec * 2.5); // 2.5s

            if (_buffer.Count >= windowBytes)
            {
                // Take the full 3.0s window
                var chunk = _buffer.GetRange(0, windowBytes).ToArray();

                // Remove the "Step" (2.5s), keeping the last 0.5s for the next run
                _buffer.RemoveRange(0, stepBytes);

                _ = TranscribeChunk(chunk, format);
            }
        }
    }

    private async Task TranscribeChunk(byte[] chunk, WaveFormat format)
    {
        if (_processor == null) return;
        try
        {
            using var stream = new MemoryStream(chunk);
            using var reader = new RawSourceWaveStream(stream, format);

            ISampleProvider provider = reader.ToSampleProvider();

            if (provider.WaveFormat.SampleRate != 16000)
                provider = new WdlResamplingSampleProvider(provider, 16000);

            if (provider.WaveFormat.Channels > 1)
                provider = new StereoToMonoSampleProvider(provider);

            // 16kHz * 3 seconds = 48,000 samples
            // We use a slightly larger buffer just in case of resampling variations
            var floatBuffer = new float[50000];
            int read = provider.Read(floatBuffer, 0, floatBuffer.Length);

            await foreach (var segment in _processor.ProcessAsync(floatBuffer.AsMemory(0, read)))
            {
                string text = segment.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                // --- Deduplication Logic ---
                // Because of the 0.5s overlap, Whisper often re-transcribes the same word.
                // We check if the NEW text starts with the TAIL of the OLD text.

                string cleanText = RemoveOverlap(_lastSegmentTail, text);

                if (!string.IsNullOrWhiteSpace(cleanText))
                {
                    OnSegmentTranscribed?.Invoke(this, cleanText);

                    // Update tail for next time. We keep the last 20 chars approx.
                    _lastSegmentTail = text.Length > 20 ? text.Substring(text.Length - 20) : text;
                }
            }
        }
        catch { }
    }

    // Helper to strip duplicated words caused by sliding window
    private string RemoveOverlap(string prevTail, string newText)
    {
        if (string.IsNullOrEmpty(prevTail)) return newText;

        // Check for overlaps from length of tail down to 3 characters
        for (int i = Math.Min(prevTail.Length, newText.Length); i >= 3; i--)
        {
            string tail = prevTail.Substring(prevTail.Length - i);
            string head = newText.Substring(0, i);

            if (tail.Equals(head, StringComparison.OrdinalIgnoreCase))
            {
                return newText.Substring(i).TrimStart();
            }
        }
        return newText;
    }

    public void Dispose()
    {
        _captureService.OnAudioDataAvailable -= OnAudioDataReceived;
        _processor?.Dispose();
        _factory?.Dispose();
    }
}