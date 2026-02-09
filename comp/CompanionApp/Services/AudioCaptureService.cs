using System;
using System.Linq;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace CompanionApp.Services;

public sealed class AudioCaptureService : IDisposable
{
    private WasapiCapture? _capture;
    private string _deviceName = "Default";

    public WaveFormat? WaveFormat => _capture?.WaveFormat;

    public event Action<byte[]>? AudioFrame;

    public AudioCaptureService(string deviceName = "Default")
    {
        _deviceName = deviceName;
    }

    public void Start()
    {
        if (_capture != null)
        {
            return;
        }

        // Find the selected device
        MMDevice? device = null;
        if (_deviceName != "Default")
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                device = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(d => d.FriendlyName == _deviceName);
            }
            catch
            {
                // Use default if device not found
            }
        }

        _capture = device != null ? new WasapiCapture(device) : new WasapiCapture();
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
