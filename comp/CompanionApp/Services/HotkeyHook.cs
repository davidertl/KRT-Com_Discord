using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using CompanionApp.Models;
using CompanionApp.Utilities;

namespace CompanionApp.Services;

public sealed class HotkeyHook : IDisposable
{
    private readonly List<HotkeyEntry> _entries;
    private readonly Action<HotkeyBinding> _onPressed;
    private readonly Action<HotkeyBinding> _onReleased;
    private IntPtr _hookId = IntPtr.Zero;
    private HotkeyBinding? _activeBinding;

    private readonly LowLevelKeyboardProc _proc;

    public HotkeyHook(IEnumerable<HotkeyBinding> bindings, Action<HotkeyBinding> onPressed, Action<HotkeyBinding> onReleased)
    {
        _entries = bindings
            .Where(b => b.IsEnabled)
            .Select(b => new HotkeyEntry(b))
            .Where(e => e.IsValid)
            .ToList();

        _onPressed = onPressed;
        _onReleased = onReleased;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        _hookId = SetHook(_proc);
    }

    public void Stop()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (info.vkCode != 0)
            {
                var key = KeyInterop.KeyFromVirtualKey(info.vkCode);
                var modifiers = ReadModifiers();

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (_activeBinding == null)
                    {
                        var entry = _entries.FirstOrDefault(e => e.IsMatch(key, modifiers));
                        if (entry != null)
                        {
                            _activeBinding = entry.Binding;
                            _onPressed(entry.Binding);
                        }
                    }
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    if (_activeBinding != null && key == entryKey(_activeBinding))
                    {
                        var binding = _activeBinding;
                        _activeBinding = null;
                        _onReleased(binding);
                    }
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private Key entryKey(HotkeyBinding binding)
    {
        var entry = _entries.FirstOrDefault(e => e.Binding == binding);
        return entry?.Key ?? Key.None;
    }

    private static HotkeyModifiers ReadModifiers()
    {
        var mods = HotkeyModifiers.None;
        if (IsKeyDown(VK_CONTROL)) mods |= HotkeyModifiers.Control;
        if (IsKeyDown(VK_MENU)) mods |= HotkeyModifiers.Alt;
        if (IsKeyDown(VK_SHIFT)) mods |= HotkeyModifiers.Shift;
        if (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    private static bool IsKeyDown(int vk)
    {
        return (GetKeyState(vk) & 0x8000) != 0;
    }

    private sealed class HotkeyEntry
    {
        public HotkeyEntry(HotkeyBinding binding)
        {
            Binding = binding;
            IsValid = HotkeyParser.TryParse(binding.Hotkey, out var combo);
            if (IsValid)
            {
                Key = combo.Key;
                Modifiers = combo.Modifiers;
            }
        }

        public HotkeyBinding Binding { get; }
        public bool IsValid { get; }
        public Key Key { get; }
        public HotkeyModifiers Modifiers { get; }

        public bool IsMatch(Key key, HotkeyModifiers modifiers)
        {
            return key == Key && modifiers == Modifiers;
        }
    }

    private static IntPtr SetHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        var moduleHandle = GetModuleHandle(curModule.ModuleName);
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, moduleHandle, 0);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SHIFT = 0x10;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
