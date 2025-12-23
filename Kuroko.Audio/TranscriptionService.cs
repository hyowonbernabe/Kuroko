using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace Kuroko.Audio;

public class TranscriptionService : IDisposable
{
    private readonly AudioCaptureService _captureService;
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;

    // THE ARCHITECTURE: Producer (Listener) -> Queue -> Consumer (Composer)
    private readonly Channel<byte[]> _audioQueue;
    private readonly List<byte> _listenerBuffer = new();
    private Task? _composerTask;
    private readonly CancellationTokenSource _cts = new();

    // VAD (Voice Activity Detection) Settings
    private const int SampleRate = 16000;
    private const double VolumeThreshold = 0.005; // RMS Threshold for "Silence"
    // OPTIMIZATION: Increased to 400ms to prevent cutting mid-sentence words while maintaining speed
    private readonly TimeSpan _silenceCutoff = TimeSpan.FromMilliseconds(400);
    private readonly TimeSpan _maxChunkDuration = TimeSpan.FromSeconds(5);

    // Listener State
    private DateTime _lastSpeechDetected = DateTime.Now;
    private bool _isSpeechActive = false;

    public event EventHandler<string>? OnSegmentTranscribed;

    public TranscriptionService(AudioCaptureService captureService)
    {
        _captureService = captureService;
        _captureService.OnAudioDataAvailable += OnAudioDataReceived;

        // Create an unbounded queue (FIFO)
        _audioQueue = Channel.CreateUnbounded<byte[]>();
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

        // OPTIMIZATION: Use LongRunning to give the Composer a dedicated thread, avoiding ThreadPool starvation
        _composerTask = Task.Factory.StartNew(ComposerLoop, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // --- THE LISTENER (Producer) ---
    // Runs on the audio thread. Its ONLY job is to slice audio and push to queue.
    private void OnAudioDataReceived(object? sender, byte[] data)
    {
        var format = _captureService.CurrentFormat;
        if (format == null) return;

        bool isLoud = CheckVolume(data, format);

        lock (_listenerBuffer)
        {
            _listenerBuffer.AddRange(data);

            if (isLoud)
            {
                _isSpeechActive = true;
                _lastSpeechDetected = DateTime.Now;
            }

            // Calculate current buffer duration
            double bytesPerSecond = format.AverageBytesPerSecond;
            double currentDuration = _listenerBuffer.Count / bytesPerSecond;

            // Logic: Cut the tape if...
            // 1. We HAD speech, but now have silence > 400ms (Natural pause)
            // 2. OR Buffer is > 5 seconds (Safety valve to prevent huge latency)

            bool silenceTimeout = _isSpeechActive && (DateTime.Now - _lastSpeechDetected) > _silenceCutoff;
            bool safetyTimeout = currentDuration > _maxChunkDuration.TotalSeconds;

            // OPTIMIZATION: Lowered min threshold to 0.2s to capture short "Yes/No" answers
            if ((silenceTimeout || safetyTimeout) && currentDuration > 0.2)
            {
                // Create the package
                byte[] chunk = _listenerBuffer.ToArray();

                // Clear the buffer completely (No overlap = No duplication)
                _listenerBuffer.Clear();
                _isSpeechActive = false; // Reset speech state waiting for next word

                // Hand off to The Composer
                _audioQueue.Writer.TryWrite(chunk);
            }
        }
    }

    // --- THE COMPOSER (Consumer) ---
    // Runs on a separate thread. Processes the queue sequentially.
    private async Task ComposerLoop()
    {
        try
        {
            var format = _captureService.CurrentFormat;

            // Wait until the listener provides audio format (on first packet)
            while (format == null && !_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, _cts.Token);
                format = _captureService.CurrentFormat;
            }

            // Process loop
            await foreach (var chunk in _audioQueue.Reader.ReadAllAsync(_cts.Token))
            {
                if (chunk == null || chunk.Length == 0) continue;
                await TranscribeChunk(chunk, format!);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Log error in production */ }
    }

    private async Task TranscribeChunk(byte[] chunk, WaveFormat format)
    {
        if (_processor == null) return;
        try
        {
            // Standard NAudio Resampling
            using var stream = new MemoryStream(chunk);
            using var reader = new RawSourceWaveStream(stream, format);

            ISampleProvider provider = reader.ToSampleProvider();

            if (provider.WaveFormat.SampleRate != SampleRate)
                provider = new WdlResamplingSampleProvider(provider, SampleRate);

            if (provider.WaveFormat.Channels > 1)
                provider = new StereoToMonoSampleProvider(provider);

            var floatBuffer = new float[chunk.Length];
            int samplesRead = provider.Read(floatBuffer, 0, floatBuffer.Length);

            // Volume Boost (1.5x)
            for (int i = 0; i < samplesRead; i++)
            {
                floatBuffer[i] = Math.Clamp(floatBuffer[i] * 1.5f, -1.0f, 1.0f);
            }

            // Feed to Whisper
            // Since chunks are strictly sequential and non-overlapping, we blindly output whatever we get.
            await foreach (var segment in _processor.ProcessAsync(floatBuffer.AsMemory(0, samplesRead)))
            {
                string text = segment.Text.Trim();

                // Basic Hallucination Filter
                if (string.IsNullOrWhiteSpace(text) ||
                    text.StartsWith("[") ||
                    text.Contains("BLANK_AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                OnSegmentTranscribed?.Invoke(this, text);
            }
        }
        catch { }
    }

    private bool CheckVolume(byte[] data, WaveFormat format)
    {
        double sumSquares = 0;
        int sampleCount = 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                if (i + 4 > data.Length) break;
                float sample = BitConverter.ToSingle(data, i);
                sumSquares += sample * sample;
                sampleCount++;
            }
        }
        else if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            for (int i = 0; i < data.Length; i += 2)
            {
                if (i + 2 > data.Length) break;
                short sample = BitConverter.ToInt16(data, i);
                float norm = sample / 32768f;
                sumSquares += norm * norm;
                sampleCount++;
            }
        }

        if (sampleCount == 0) return false;
        return Math.Sqrt(sumSquares / sampleCount) > VolumeThreshold;
    }

    public void Dispose()
    {
        _captureService.OnAudioDataAvailable -= OnAudioDataReceived;
        _cts.Cancel(); // Stop the composer loop
        _processor?.Dispose();
        _factory?.Dispose();
        _cts.Dispose();
    }
}