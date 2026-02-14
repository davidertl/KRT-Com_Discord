using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace CompanionApp.Services;

/// <summary>
/// Ducking target mode: determines which audio sources are ducked while transmitting.
/// </summary>
public enum DuckingTargetMode
{
    /// <summary>Only internal radio RX audio is ducked (handled in VoiceService).</summary>
    RadioOnly = 0,
    /// <summary>Internal radio + user-selected external applications.</summary>
    RadioAndSelectedApps = 1,
    /// <summary>Internal radio + all external applications.</summary>
    RadioAndAllApps = 2
}

/// <summary>
/// Information about an active audio session for display in the process selector UI.
/// </summary>
public class AudioSessionInfo
{
    public string ProcessName { get; set; } = "";
    public int ProcessId { get; set; }
    public bool IsSelected { get; set; }
}

/// <summary>
/// Service that ducks (reduces volume of) external application audio via the
/// Windows Audio Session API (WASAPI) while the user is transmitting.
/// </summary>
public sealed class AudioDuckingService : IDisposable
{
    private DuckingTargetMode _mode = DuckingTargetMode.RadioOnly;
    private readonly HashSet<string> _selectedProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, float> _savedVolumes = new(); // PID → original volume
    private readonly object _lock = new();
    private bool _isDucked;

    /// <summary>Optional log callback for diagnostic output.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Configure which external applications should be ducked.
    /// </summary>
    public void SetDuckingTargets(DuckingTargetMode mode, List<string>? selectedProcessNames)
    {
        lock (_lock)
        {
            _mode = mode;
            _selectedProcessNames.Clear();
            if (selectedProcessNames != null)
            {
                foreach (var name in selectedProcessNames)
                    _selectedProcessNames.Add(name);
            }
            Log?.Invoke($"[Ducking] Targets set: mode={_mode} selected=[{string.Join(", ", _selectedProcessNames)}]");
        }
    }

    /// <summary>
    /// Apply ducking to target audio sessions. Called when TX starts.
    /// </summary>
    public void ApplyDucking(float duckingMultiplier)
    {
        lock (_lock)
        {
            if (_mode == DuckingTargetMode.RadioOnly)
            {
                Log?.Invoke($"[Ducking] ApplyDucking skipped: mode=RadioOnly");
                return;
            }
            if (_isDucked)
            {
                Log?.Invoke($"[Ducking] ApplyDucking skipped: already ducked");
                return;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessionManager = device.AudioSessionManager;
                var sessions = sessionManager.Sessions;
                int duckedCount = 0;

                Log?.Invoke($"[Ducking] ApplyDucking: multiplier={duckingMultiplier:F2} mode={_mode} sessions={sessions.Count}");

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        var process = GetSessionProcess(session);
                        if (process == null) continue;

                        // Skip our own process
                        if (process.Id == Environment.ProcessId) continue;

                        string processName = process.ProcessName;

                        bool shouldDuck = _mode == DuckingTargetMode.RadioAndAllApps
                            || (_mode == DuckingTargetMode.RadioAndSelectedApps && _selectedProcessNames.Contains(processName));

                        if (!shouldDuck) continue;

                        // Save original volume and apply ducking
                        float originalVolume = session.SimpleAudioVolume.Volume;
                        _savedVolumes[process.Id] = originalVolume;
                        float newVolume = originalVolume * duckingMultiplier;
                        session.SimpleAudioVolume.Volume = newVolume;
                        duckedCount++;
                        Log?.Invoke($"[Ducking]   Ducked '{processName}' (PID {process.Id}): {originalVolume:F2} → {newVolume:F2}");
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"[Ducking]   Session {i} error: {ex.Message}");
                    }
                }

                _isDucked = true;
                Log?.Invoke($"[Ducking] ApplyDucking done: {duckedCount} sessions ducked");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[Ducking] ApplyDucking FAILED: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Restore original volumes on all ducked sessions. Called when TX stops.
    /// </summary>
    public void RestoreDucking()
    {
        lock (_lock)
        {
            if (!_isDucked)
            {
                Log?.Invoke($"[Ducking] RestoreDucking skipped: not currently ducked");
                return;
            }

            int restoredCount = 0;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessionManager = device.AudioSessionManager;
                var sessions = sessionManager.Sessions;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    try
                    {
                        var process = GetSessionProcess(session);
                        if (process == null) continue;

                        if (_savedVolumes.TryGetValue(process.Id, out float originalVolume))
                        {
                            session.SimpleAudioVolume.Volume = originalVolume;
                            restoredCount++;
                            Log?.Invoke($"[Ducking]   Restored '{process.ProcessName}' (PID {process.Id}): → {originalVolume:F2}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"[Ducking]   Restore session {i} error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[Ducking] RestoreDucking FAILED: {ex.Message}");
            }
            finally
            {
                _savedVolumes.Clear();
                _isDucked = false;
                Log?.Invoke($"[Ducking] RestoreDucking done: {restoredCount} sessions restored");
            }
        }
    }

    /// <summary>
    /// Enumerate current audio sessions for the process selector UI.
    /// Returns a list of active audio sessions with process names and PIDs.
    /// </summary>
    public List<AudioSessionInfo> GetAudioSessions()
    {
        var result = new List<AudioSessionInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;
            var sessions = sessionManager.Sessions;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    var process = GetSessionProcess(session);
                    if (process == null) continue;
                    if (process.Id == Environment.ProcessId) continue;

                    string name = process.ProcessName;
                    if (!seen.Add(name)) continue; // deduplicate by name

                    result.Add(new AudioSessionInfo
                    {
                        ProcessName = name,
                        ProcessId = process.Id,
                        IsSelected = _selectedProcessNames.Contains(name)
                    });
                }
                catch
                {
                    // Skip inaccessible sessions
                }
            }
        }
        catch
        {
            // Return empty list on error
        }

        return result.OrderBy(s => s.ProcessName).ToList();
    }

    private static Process? GetSessionProcess(AudioSessionControl session)
    {
        try
        {
            uint pid = session.GetProcessID;
            if (pid == 0) return null;
            return Process.GetProcessById((int)pid);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        RestoreDucking();
    }
}
