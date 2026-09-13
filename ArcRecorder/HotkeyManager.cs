using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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
        const uint WM_QUIT = 0x0012;
        const uint VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

        /// <summary>
        /// «Клавиша, потом Alt» считается хоткеем, только если Alt нажат в течение этого окна после клавиши.
        /// Долго зажатая R (перезарядка) или Z + Alt (обзор/ходьба в играх) хоткей не дёргают.
        /// </summary>
        const int ReverseChordWindowMs = 350;

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")]
        static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; }

        readonly HwndSource _source;
        readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        int _nextId = 1;

        // Хук живёт в СВОЁМ потоке с message loop: если UI-поток чем-то занят, каждое нажатие
        // в системе ждало бы его (лаг ввода в игре), а Windows могла молча снять хук по таймауту.
        LowLevelKeyboardProc _hookProc; // держим ссылку, чтобы GC не собрал делегат
        IntPtr _hook = IntPtr.Zero;
        Thread _hookThread;
        uint _hookThreadId;
        volatile Dictionary<uint, Action> _altReversed = new Dictionary<uint, Action>(); // copy-on-write, читает поток хука
        // дальше — только поток хука
        readonly Dictionary<uint, long> _pressedAt = new Dictionary<uint, long>();
        bool _altDown;
        bool _swallowAlt;

        /// <summary>Журнал хоткеев (%APPDATA%\ArcRecorder\hotkeys.log). Пишем в фоне — поток хука нельзя тормозить.</summary>
        static void Log(string msg)
        {
            string stamped = msg;
            ThreadPool.QueueUserWorkItem(_ => AppLog.Write("hotkeys.log", stamped));
        }

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
            {
                Log($"RegisterHotKey mod=0x{modifiers:X} vk=0x{virtualKey:X2} НЕ удался, код {Marshal.GetLastWin32Error()}");
                return false;
            }
            Log($"RegisterHotKey mod=0x{modifiers:X} vk=0x{virtualKey:X2} → id {id}");
            _actions[id] = action;

            // Для Alt-хоткеев дополнительно ловим обратный порядок (клавиша, потом Alt)
            if (modifiers == MOD_ALT)
            {
                _altReversed = new Dictionary<uint, Action>(_altReversed) { [virtualKey] = action };
                EnsureHook();
            }
            return true;
        }

        void EnsureHook()
        {
            if (_hookThread != null) return;
            _hookProc = HookProc;
            var ready = new ManualResetEventSlim(false); // не dispose: поток может выставить его уже после таймаута Wait
            _hookThread = new Thread(() =>
            {
                _hookThreadId = GetCurrentThreadId();
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
                Log(_hook != IntPtr.Zero ? "хук клавиатуры установлен" : $"SetWindowsHookEx НЕ удался, код {Marshal.GetLastWin32Error()}");
                ready.Set();
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { } // колбэки хука приходят внутри GetMessage
                if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            })
            { IsBackground = true, Name = "ArcRecorderKeyboardHook" };
            _hookThread.Start();
            ready.Wait(2000);
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint vk = (uint)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode — первое поле
                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                bool isAlt = vk == VK_MENU || vk == VK_LMENU || vk == VK_RMENU;
                var reversed = _altReversed;

                if (isAlt)
                {
                    // Отпускание Alt могло пройти мимо хука (таймаут LL-хука, окно UAC). Если система считает Alt
                    // отпущенным, а мы — зажатым, сбрасываем состояние: иначе хук съедал бы каждое нажатие Alt
                    // и Alt+хоткеи переставали работать вовсе.
                    if (down && _altDown && (GetAsyncKeyState((int)VK_MENU) & 0x8000) == 0)
                    {
                        Log($"залипшее состояние Alt сброшено (swallow={_swallowAlt})");
                        _altDown = false;
                        _swallowAlt = false;
                    }
                    if (down && !_altDown)
                    {
                        _altDown = true;
                        long now = Environment.TickCount64;
                        foreach (var kv in reversed)
                        {
                            if (!_pressedAt.TryGetValue(kv.Key, out long pressed)) continue;
                            if ((GetAsyncKeyState((int)kv.Key) & 0x8000) == 0)
                            {
                                _pressedAt.Remove(kv.Key); // отпускание не увидели (например, было в админском окне)
                                continue;
                            }
                            // Alt нажали ВТОРЫМ, сразу после нашей клавиши — это хоткей
                            if (now - pressed <= ReverseChordWindowMs)
                            {
                                _pressedAt.Remove(kv.Key);
                                _swallowAlt = true;
                                Log($"аккорд «клавиша 0x{kv.Key:X2}, потом Alt» через {now - pressed} мс — Alt съеден");
                                System.Windows.Application.Current?.Dispatcher.BeginInvoke(kv.Value);
                                return new IntPtr(1); // съедаем Alt, чтобы не дёргалось меню
                            }
                        }
                    }
                    else if (up)
                    {
                        _altDown = false;
                        // Отпускание тоже съедаем: одиночный «отпуск Alt» без нажатия как раз открывает меню окна
                        if (_swallowAlt) { _swallowAlt = false; return new IntPtr(1); }
                    }
                    else if (down && _swallowAlt)
                    {
                        return new IntPtr(1); // автоповтор съеденного Alt
                    }
                }
                else if (reversed.ContainsKey(vk))
                {
                    if (down) { if (!_pressedAt.ContainsKey(vk)) _pressedAt[vk] = Environment.TickCount64; } // автоповтор не сдвигает время
                    else if (up) _pressedAt.Remove(vk);
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                Log($"WM_HOTKEY id {wParam.ToInt32()}");
                action();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_hookThread != null)
            {
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _hookThread.Join(1000);
                _hookThread = null;
            }
            foreach (var id in _actions.Keys)
                UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
            _altReversed = new Dictionary<uint, Action>();
            _source.Dispose();
        }
    }
}
