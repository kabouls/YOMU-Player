using System.Runtime.InteropServices;

namespace YomuPlayer.Desktop;

public enum PlaybackShortcut
{
    PlayPause,
    Next,
    Previous,
    Stop,
    Mute,
    VolumeUp,
    VolumeDown
}

// Windows sends media keys to the shell/SMTC, and a lot of mice and macro pads send the same commands.
// This service makes those keys work even when focus is on a page control, and adds a few global combos
// for keyboards/macros that cannot send native media keys.
public sealed class GlobalShortcuts : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12;
    private const uint VK_SPACE = 0x20;
    private const uint VK_LEFT = 0x25;
    private const uint VK_UP = 0x26;
    private const uint VK_RIGHT = 0x27;
    private const uint VK_DOWN = 0x28;
    private const uint VK_M = 0x4D;
    private const uint VK_MEDIA_NEXT_TRACK = 0xB0;
    private const uint VK_MEDIA_PREV_TRACK = 0xB1;
    private const uint VK_MEDIA_STOP = 0xB2;
    private const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

    private readonly Func<bool> _hasTrack;
    private readonly Action<PlaybackShortcut> _dispatch;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly IntPtr _keyboardHook;
    private readonly IntPtr _mouseHook;

    public GlobalShortcuts(Func<bool> hasTrack, Action<PlaybackShortcut> dispatch)
    {
        _hasTrack = hasTrack;
        _dispatch = dispatch;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;

        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookExKeyboard(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookExMouse(WH_MOUSE_LL, _mouseProc, module, 0);

        if (_keyboardHook == IntPtr.Zero)
            Log.Write($"global keyboard hook failed: {Marshal.GetLastWin32Error()}");
        if (_mouseHook == IntPtr.Zero)
            Log.Write($"global mouse hook failed: {Marshal.GetLastWin32Error()}");
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var message = wParam.ToInt32();
            if (nCode >= 0 && (message == WM_KEYDOWN || message == WM_SYSKEYDOWN))
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var action = TranslateKeyboard(info.vkCode);
                if (action is not null && _hasTrack())
                {
                    _dispatch(action.Value);
                    return new IntPtr(1);
                }
            }
        }
        catch (Exception e)
        {
            Log.Write("global keyboard hook: " + e.Message);
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private PlaybackShortcut? TranslateKeyboard(uint vk)
    {
        // Keep these local to the YOMU window. SMTC still drives media keys when YOMU is in the background,
        // and this avoids stealing media keys from a game or another player while YOMU is not focused.
        if (!IsOwnWindowActive()) return null;

        // Hardware media keys and macro pads usually send these VKs.
        switch (vk)
        {
            case VK_MEDIA_PLAY_PAUSE: return PlaybackShortcut.PlayPause;
            case VK_MEDIA_NEXT_TRACK: return PlaybackShortcut.Next;
            case VK_MEDIA_PREV_TRACK: return PlaybackShortcut.Previous;
            case VK_MEDIA_STOP: return PlaybackShortcut.Stop;
        }

        // The fallback combos are deliberately less likely to collide with normal app/browser shortcuts.
        // They let keyboard/mouse macro software that cannot emit media keys drive playback too.
        if (!IsDown(VK_CONTROL) || !IsDown(VK_MENU)) return null;
        return vk switch
        {
            VK_SPACE => PlaybackShortcut.PlayPause,
            VK_RIGHT => PlaybackShortcut.Next,
            VK_LEFT => PlaybackShortcut.Previous,
            VK_UP => PlaybackShortcut.VolumeUp,
            VK_DOWN => PlaybackShortcut.VolumeDown,
            VK_M => PlaybackShortcut.Mute,
            _ => null
        };
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var message = wParam.ToInt32();
            if (nCode >= 0 && IsOwnWindowActive() && IsDown(VK_CONTROL) && _hasTrack())
            {
                if (message == WM_XBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var button = (int)((info.mouseData >> 16) & 0xFFFF);
                    _dispatch(button == 1 ? PlaybackShortcut.Previous : PlaybackShortcut.Next);
                    return new IntPtr(1);
                }

                if (message == WM_MBUTTONDOWN)
                {
                    _dispatch(PlaybackShortcut.PlayPause);
                    return new IntPtr(1);
                }
            }
        }
        catch (Exception e)
        {
            Log.Write("global mouse hook: " + e.Message);
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsOwnWindowActive()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookExKeyboard(int idHook, LowLevelKeyboardProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
}
