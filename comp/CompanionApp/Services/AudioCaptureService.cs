using System;
using NAudio.Wave;

namespace CompanionApp.Services;

public sealed class AudioCaptureService : IDisposable
{
    private WasapiCapture? _capture;

    public WaveFormat? WaveFormat => _capture?.WaveFormat;

    public event Action<byte[]>? AudioFrame;

    public void Start()
    {
        if (_capture != null)
        {
            return;
        }

        _capture = new WasapiCapture();
        _capture.DataAvailable += CaptureOnDataAvailable;
        _capture.RecordingStopped += CaptureOnRecordingStopped;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture == null)
        {
            return;
        }

        _capture.StopRecording();
        _capture.Dispose();
        _capture = null;
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var buffer = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, buffer, e.BytesRecorded);
        AudioFrame?.Invoke(buffer);
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
    {
    }

    public void Dispose()
    {
        Stop();
    }
}
