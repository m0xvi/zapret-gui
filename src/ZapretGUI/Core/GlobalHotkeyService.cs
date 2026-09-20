using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZapretGui.Core
{
    /// <summary>
    /// Сервис глобальных горячих клавиш Windows (Win32 RegisterHotKey).
    /// </summary>
    public sealed class GlobalHotkeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID_TOGGLE_BYPASS = 9001;
        private const int HOTKEY_ID_TOGGLE_GAMEMODE = 9002;
        private const int HOTKEY_ID_TOGGLE_MINI_OVERLAY = 9003;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly AppSettings _settings;
        private IntPtr _hWnd;
        private HwndSource? _source;
        private bool _isRegistered;

        public event Action? ToggleBypassRequested;
        public event Action? ToggleGameModeRequested;
        public event Action? ToggleMiniOverlayRequested;

        public GlobalHotkeyService(AppSettings settings)
        {
            _settings = settings;
        }

        public void Register(Window window)
        {
            if (_isRegistered || !_settings.GlobalHotkeysEnabled) return;

            try
            {
                var helper = new WindowInteropHelper(window);
                _hWnd = helper.EnsureHandle();
                _source = HwndSource.FromHwnd(_hWnd);
                _source?.AddHook(HwndHook);

                RegisterKey(HOTKEY_ID_TOGGLE_BYPASS, _settings.HotkeyToggleBypass);
                RegisterKey(HOTKEY_ID_TOGGLE_GAMEMODE, _settings.HotkeyToggleGameMode);
                RegisterKey(HOTKEY_ID_TOGGLE_MINI_OVERLAY, _settings.HotkeyToggleMiniOverlay);

                _isRegistered = true;
                AppLog.Info($"[Hotkeys] Глобальные клавиши зарегистрированы (Bypass: {_settings.HotkeyToggleBypass}, GameMode: {_settings.HotkeyToggleGameMode}, Overlay: {_settings.HotkeyToggleMiniOverlay})");
            }
            catch (Exception ex)
            {
                AppLog.Warn("[Hotkeys] Не удалось зарегистрировать горячие клавиши: " + ex.Message);
            }
        }

        public void Unregister()
        {
            if (!_isRegistered) return;

            try
            {
                if (_hWnd != IntPtr.Zero)
                {
                    UnregisterHotKey(_hWnd, HOTKEY_ID_TOGGLE_BYPASS);
                    UnregisterHotKey(_hWnd, HOTKEY_ID_TOGGLE_GAMEMODE);
                    UnregisterHotKey(_hWnd, HOTKEY_ID_TOGGLE_MINI_OVERLAY);
                }

                _source?.RemoveHook(HwndHook);
                _source = null;
                _isRegistered = false;
            }
            catch { }
        }

        private void RegisterKey(int id, string hotkeyString)
        {
            if (_hWnd == IntPtr.Zero || string.IsNullOrWhiteSpace(hotkeyString)) return;

            var (modifiers, vk) = ParseHotkey(hotkeyString);
            if (vk == 0) return;

            var success = RegisterHotKey(_hWnd, id, modifiers | MOD_NOREPEAT, vk);
            if (!success)
            {
                AppLog.Warn($"[Hotkeys] Не удалось зарегистрировать комбинацию «{hotkeyString}» (возможно, занята другой программой).");
            }
        }

        private static (uint Modifiers, uint VirtualKey) ParseHotkey(string str)
        {
            uint mod = 0;
            uint vk = 0;

            var parts = str.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    mod |= MOD_CONTROL;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    mod |= MOD_SHIFT;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    mod |= MOD_ALT;
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    mod |= MOD_WIN;
                else if (part.Length == 1)
                {
                    var ch = char.ToUpperInvariant(part[0]);
                    if (ch >= 'A' && ch <= 'Z')
                        vk = (uint)ch;
                    else if (ch >= '0' && ch <= '9')
                        vk = (uint)ch;
                }
                else if (part.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[1..], out var fNum) && fNum >= 1 && fNum <= 24)
                {
                    vk = (uint)(0x70 + (fNum - 1)); // VK_F1 = 0x70
                }
            }

            return (mod, vk);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                var id = wParam.ToInt32();
                switch (id)
                {
                    case HOTKEY_ID_TOGGLE_BYPASS:
                        ToggleBypassRequested?.Invoke();
                        handled = true;
                        break;
                    case HOTKEY_ID_TOGGLE_GAMEMODE:
                        ToggleGameModeRequested?.Invoke();
                        handled = true;
                        break;
                    case HOTKEY_ID_TOGGLE_MINI_OVERLAY:
                        ToggleMiniOverlayRequested?.Invoke();
                        handled = true;
                        break;
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
        }
    }
}
