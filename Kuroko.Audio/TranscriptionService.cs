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
        lock (_buffer) _buffer.AddRange(data);

        var format = _captureService.CurrentFormat;
        if (format == null) return;

        // Process every 2.5 seconds of audio
        if (_buffer.Count >= format.AverageBytesPerSecond * 2.5)
        {
            var chunk = _buffer.ToArray();
            _buffer.Clear();
            _ = TranscribeChunk(chunk, format);
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

            var floatBuffer = new float[48000]; // 16k * 3 seconds
            int read = provider.Read(floatBuffer, 0, floatBuffer.Length);

            await foreach (var segment in _processor.ProcessAsync(floatBuffer.AsMemory(0, read)))
            {
                OnSegmentTranscribed?.Invoke(this, segment.Text);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _captureService.OnAudioDataAvailable -= OnAudioDataReceived;
        _processor?.Dispose();
        _factory?.Dispose();
    }
}