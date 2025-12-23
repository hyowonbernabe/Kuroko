using NAudio.Wave;
using System;
using System.Diagnostics;

namespace Kuroko.Audio;

public class AudioCaptureService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _debugWriter;

    public event EventHandler<byte[]>? OnAudioDataAvailable;

    // Helper to access the format (e.g. 48kHz, 44.1kHz)
    public WaveFormat? CurrentFormat => _capture?.WaveFormat;

    public bool IsRecording { get; private set; }

    public void StartSystemAudioCapture()
    {
        if (IsRecording) return;

        try
        {
            _capture = new WasapiLoopbackCapture();

            _capture.DataAvailable += (s, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    byte[] buffer = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, buffer, e.BytesRecorded);

                    OnAudioDataAvailable?.Invoke(this, buffer);

                    // Debug: Write to file if recording is active
                    _debugWriter?.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            _capture.RecordingStopped += (s, e) =>
            {
                IsRecording = false;
                Debug.WriteLine("Audio capture stopped.");
            };

            _capture.StartRecording();
            IsRecording = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting audio capture: {ex.Message}");
            IsRecording = false;
        }
    }

    // New Debug Method
    public void StartDebugRecording(string filePath)
    {
        if (_capture?.WaveFormat != null)
        {
            _debugWriter = new WaveFileWriter(filePath, _capture.WaveFormat);
        }
    }

    public void StopDebugRecording()
    {
        _debugWriter?.Dispose();
        _debugWriter = null;
    }

    public void Stop()
    {
        StopDebugRecording();
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        IsRecording = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}