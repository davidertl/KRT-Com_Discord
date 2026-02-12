using System;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CompanionApp.Services;

/// <summary>
/// Service for playing beep sounds on TX/RX start/end.
/// All public Play* methods accept optional per-radio volume (0-1.25)
/// and pan (0=left, 0.5=center, 1=right) so the beep/hiss matches
/// the radio's audio settings.
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

    public void PlayTxStartBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayBeep(TxStartFreq, BeepDurationMs, 0.3f, radioVolume, radioPan);
    }

    public void PlayTxEndBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayHiss(durationMs: 250, volumeMultiplier: 0.4f, radioVolume: radioVolume, radioPan: radioPan);
    }

    public void PlayRxStartBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayBeep(RxStartFreq, BeepDurationMs, 0.3f, radioVolume, radioPan);
    }

    public void PlayRxEndBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayBeep(RxEndFreq, BeepDurationMs, 0.3f, radioVolume, radioPan);
    }

    /// <summary>
    /// Double-beep for Talk to All start
    /// </summary>
    public async void PlayTalkToAllBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayBeep(TxStartFreq, 60, 0.3f, radioVolume, radioPan);
        await System.Threading.Tasks.Task.Delay(80);
        PlayBeep(TxStartFreq, 60, 0.3f, radioVolume, radioPan);
    }

    /// <summary>
    /// Emergency TX start - urgent ascending siren (3 tones, louder)
    /// </summary>
    public async void PlayEmergencyTxBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayBeep(1200, 80, 0.55f, radioVolume, radioPan);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(1500, 80, 0.55f, radioVolume, radioPan);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(1800, 120, 0.6f, radioVolume, radioPan);
    }

    /// <summary>
    /// Emergency TX end - descending three-tone
    /// </summary>
    public async void PlayEmergencyTxEndBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayBeep(1500, 80, 0.55f, radioVolume, radioPan);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(1200, 80, 0.55f, radioVolume, radioPan);
        await System.Threading.Tasks.Task.Delay(40);
        PlayBeep(900, 120, 0.55f, radioVolume, radioPan);
    }

    /// <summary>
    /// Emergency RX start - rapid triple-pulse alert (louder, higher pitch)
    /// </summary>
    public async void PlayEmergencyRxBeep(float radioVolume = 1f, float radioPan = 0.5f)
    {
        if (!_enabled) return;
        PlayBeep(1600, 60, 0.55f, radioVolume, radioPan);
        await System.Threading.Tasks.Task.Delay(30);
        PlayBeep(1600, 60, 0.55f, radioVolume, radioPan);
        await System.Threading.Tasks.Task.Delay(30);
        PlayBeep(1800, 80, 0.6f, radioVolume, radioPan);
    }

    /// <summary>
    /// Play a walkie-talkie squelch-tail / static burst.
    /// Combines band-pass filtered noise with a short downward-chirp
    /// to emulate the characteristic analog radio "krshhh" when PTT releases.
    /// </summary>
    private void PlayHiss(int durationMs = 250, float volumeMultiplier = 0.4f,
                          float radioVolume = 1f, float radioPan = 0.5f)
    {
        try
        {
            var sampleRate = 44100;
            var sampleCount = sampleRate * durationMs / 1000;
            var samples = new float[sampleCount];
            var rng = new Random();

            // Two-pole band-pass state (centre ≈ 2.5 kHz, BW ≈ 3 kHz)
            // gives a "radio static" colour rather than pure white noise.
            float lp1 = 0f, lp2 = 0f;
            float alphaLo = (float)(2 * Math.PI * 800 / (2 * Math.PI * 800 + sampleRate));   // HP ≈ 800 Hz
            float alphaHi = (float)(2 * Math.PI * 5500 / (2 * Math.PI * 5500 + sampleRate));  // LP ≈ 5.5 kHz

            for (int i = 0; i < sampleCount; i++)
            {
                double pos = (double)i / sampleCount;

                // Envelope: instant attack, sharp exponential decay
                // Starts at full level, dies to near-zero by the end.
                double envelope = Math.Exp(-4.5 * pos);

                // Add a short squelch chirp in the first 15 % (descending tone mixed in)
                double chirp = 0;
                if (pos < 0.15)
                {
                    double chirpFreq = 3200 - 2000 * (pos / 0.15);   // 3200 → 1200 Hz sweep
                    double chirpEnv  = 1.0 - pos / 0.15;              // fades out over chirp
                    chirp = Math.Sin(2 * Math.PI * chirpFreq * i / sampleRate) * chirpEnv * 0.25;
                }

                // White noise → band-pass (HP then LP)
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp2 += alphaHi * (noise - lp2);      // low-pass at 5.5 kHz
                lp1 += alphaLo * (lp2 - lp1);        // low-pass at 800 Hz (used for HP)
                float bandPassed = lp2 - lp1;         // subtract = high-pass at 800 Hz

                samples[i] = (float)((bandPassed + chirp) * volumeMultiplier * envelope);
            }

            PlaySamples(samples, sampleRate, radioVolume, radioPan);
        }
        catch
        {
            // Ignore hiss errors — not critical
        }
    }

    private void PlayBeep(int frequency, int durationMs, float volumeMultiplier = 0.3f,
                          float radioVolume = 1f, float radioPan = 0.5f)
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

            PlaySamples(samples, sampleRate, radioVolume, radioPan);
        }
        catch
        {
            // Ignore beep errors - not critical
        }
    }

    /// <summary>
    /// Shared helper: takes a mono float[] PCM buffer, applies per-radio
    /// volume and pan, converts to stereo, and plays via WasapiOut.
    /// </summary>
    private void PlaySamples(float[] monoSamples, int sampleRate,
                             float radioVolume = 1f, float radioPan = 0.5f)
    {
        // Calculate stereo gains from pan (0=left, 0.5=center, 1=right)
        float leftGain  = Math.Min(1.0f, 2.0f * (1.0f - radioPan)) * radioVolume;
        float rightGain = Math.Min(1.0f, 2.0f * radioPan) * radioVolume;

        // Build interleaved stereo buffer
        var stereoSamples = new float[monoSamples.Length * 2];
        for (int i = 0; i < monoSamples.Length; i++)
        {
            stereoSamples[i * 2]     = monoSamples[i] * leftGain;
            stereoSamples[i * 2 + 1] = monoSamples[i] * rightGain;
        }

        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2); // stereo
        var provider = new RawSourceWaveStream(
            new System.IO.MemoryStream(stereoSamples.SelectMany(BitConverter.GetBytes).ToArray()),
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
    }

    public void Dispose()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
    }
}
