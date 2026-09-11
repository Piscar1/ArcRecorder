using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ArcRecorder
{
    /// <summary>Глобальные хоткеи через WinAPI RegisterHotKey (работают из любой игры/проги).</summary>
    public class HotkeyManager : IDisposable
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;
        const int WM_HOTKEY = 0x0312;
        static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // --- низкоуровневый хук для обратного порядка (клавиша, потом Alt) ---
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const uint VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        readonly HwndSource _source;
        readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        int _nextId = 1;

        // Хук: срабатывание при нажатии Alt, когда клавиша уже зажата (Z+Alt вместо Alt+Z)
        LowLevelKeyboardProc _hookProc; // держим ссылку, чтобы GC не собрал делегат
        IntPtr _hook = IntPtr.Zero;
        readonly Dictionary<uint, Action> _altReversed = new Dictionary<uint, Action>();
        bool _altDown;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("ArcRecorderHotkeyWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = HWND_MESSAGE
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        public bool Register(uint modifiers, uint virtualKey, Action action)
        {
            int id = _nextId++;
            if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, virtualKey))
                return false;
            _actions[id] = action;

            // Для Alt-хоткеев дополнительно ловим обратный порядок (клавиша, потом Alt)
            if (modifiers == MOD_ALT)
            {
                _altReversed[virtualKey] = action;
                EnsureHook();
            }
            return true;
        }

        void EnsureHook()
        {
            if (_hook != IntPtr.Zero) return;
            _hookProc = HookProc;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint vk = (uint)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode — первое поле
                int msg = wParam.ToInt32();
                bool isAlt = vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (isAlt && !_altDown)
                    {
                        _altDown = true;
                        // Alt нажали ВТОРЫМ: проверяем, не зажата ли уже какая-то из наших клавиш
                        foreach (var kv in _altReversed)
                        {
                            if ((GetAsyncKeyState((int)kv.Key) & 0x8000) != 0)
                            {
                                var action = kv.Value;
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
                                return new IntPtr(1); // съедаем Alt, чтобы не дёргалось меню
                            }
                        }
                    }
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    if (isAlt) _altDown = false;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                action();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            foreach (var id in _actions.Keys)
                UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
            _altReversed.Clear();
            _source.Dispose();
        }
    }
}
