using System;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CompanionApp.Services;

/// <summary>
/// Service for playing beep sounds on TX/RX start/end
/// </summary>
public class BeepService : IDisposable
{
    private WasapiOut? _waveOut;
    private string _outputDeviceName = "Default";
    private bool _enabled = true;
    private float _masterVolume = 1.0f;

    // Beep frequencies and durations
    private const int TxStartFreq = 800;      // Hz
    private const int TxEndFreq = 600;        // Hz
    private const int RxStartFreq = 1000;     // Hz
    private const int RxEndFreq = 700;        // Hz
    private const int BeepDurationMs = 100;   // milliseconds

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void SetOutputDevice(string deviceName)
    {
        _outputDeviceName = deviceName;
    }

    public void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, 1.25f);
    }

    public void PlayTxStartBeep()
    {
        if (!_enabled) return;
        PlayBeep(TxStartFreq, BeepDurationMs);
    }

    public void PlayTxEndBeep()
    {
        if (!_enabled) return;
        PlayBeep(TxEndFreq, BeepDurationMs);
    }

    public void PlayRxStartBeep()
    {
        if (!_enabled) return;
        PlayBeep(RxStartFreq, BeepDurationMs);
    }

    public void PlayRxEndBeep()
    {
        if (!_enabled) return;
        PlayBeep(RxEndFreq, BeepDurationMs);
    }

    /// <summary>
    /// Double-beep for Talk to All start
    /// </summary>
    public async void PlayTalkToAllBeep()
    {
        if (!_enabled) return;
        PlayBeep(TxStartFreq, 60);
        await System.Threading.Tasks.Task.Delay(80);
        PlayBeep(TxStartFreq, 60);
    }

    /// <summary>
    /// Emergency TX start - urgent ascending siren (3 tones, louder)
    /// </summary>
    public async void PlayEmergencyTxBeep()
    {
        if (!_enabled) return;
        PlayBeep(1200, 80, 0.55f);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(1500, 80, 0.55f);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(1800, 120, 0.6f);
    }

    /// <summary>
    /// Emergency TX end - descending three-tone
    /// </summary>
    public async void PlayEmergencyTxEndBeep()
    {
        if (!_enabled) return;
        PlayBeep(1500, 80, 0.55f);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(1200, 80, 0.55f);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(900, 120, 0.55f);
    }

    /// <summary>
    /// Emergency RX start - rapid triple-pulse alert (louder, higher pitch)
    /// </summary>
    public async void PlayEmergencyRxBeep()
    {
        if (!_enabled) return;
        PlayBeep(1600, 60, 0.55f);
        await System.Threading.Tasks.Task.Delay(30);
        PlayBeep(1600, 60, 0.55f);
        await System.Threading.Tasks.Task.Delay(30);
        PlayBeep(1800, 80, 0.6f);
    }

    private void PlayBeep(int frequency, int durationMs, float volumeMultiplier = 0.3f)
    {
        try
        {
            // Create a simple sine wave beep
            var sampleRate = 44100;
            var sampleCount = sampleRate * durationMs / 1000;
            var samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                // Sine wave with fade in/out envelope
                var time = (double)i / sampleRate;
                var envelope = 1.0;
                
                // Fade in first 10%
                if (i < sampleCount * 0.1)
                    envelope = i / (sampleCount * 0.1);
                // Fade out last 20%
                else if (i > sampleCount * 0.8)
                    envelope = (sampleCount - i) / (sampleCount * 0.2);

                samples[i] = (float)(Math.Sin(2 * Math.PI * frequency * time) * volumeMultiplier * envelope);
            }

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
            var provider = new RawSourceWaveStream(
                new System.IO.MemoryStream(samples.SelectMany(BitConverter.GetBytes).ToArray()),
                waveFormat);

            var volumeProvider = new VolumeSampleProvider(provider.ToSampleProvider())
            {
                Volume = 0.5f * _masterVolume
            };

            // Find the output device
            MMDevice? device = null;
            if (_outputDeviceName != "Default")
            {
                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    device = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .FirstOrDefault(d => d.FriendlyName == _outputDeviceName);
                }
                catch
                {
                    // Use default if device not found
                }
            }

            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = device != null 
                ? new WasapiOut(device, AudioClientShareMode.Shared, false, 50)
                : new WasapiOut(AudioClientShareMode.Shared, 50);

            _waveOut.Init(volumeProvider);
            _waveOut.Play();

            // Fire and forget - the sound plays briefly
        }
        catch
        {
            // Ignore beep errors - not critical
        }
    }

    public void Dispose()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
    }
}
