using System.Runtime.InteropServices;

namespace YomuPlayer.Desktop;

// Win32 glue for the main window: media buttons in the taskbar thumbnail, and minimize / restore notifications.
public sealed class NativeWindowHooks : IDisposable
{
    public const uint ButtonPrev = 100;
    public const uint ButtonPlayPause = 101;
    public const uint ButtonNext = 102;

    private const uint WM_COMMAND = 0x0111;
    private const uint THBN_CLICKED = 0x1800;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;
    private readonly uint _taskbarButtonCreated;
    private TaskbarInterop.ITaskbarList3? _taskbar;
    private IntPtr _iconPrev, _iconPlay, _iconPause, _iconNext;
    private bool _buttonsAdded;
    private bool _playing;

    public event Action<uint>? ThumbButtonClicked;

    public NativeWindowHooks(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _taskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, (UIntPtr)1, UIntPtr.Zero);

        _iconPrev = LoadIcon("thumb_prev.png");
        _iconPlay = LoadIcon("thumb_play.png");
        _iconPause = LoadIcon("thumb_pause.png");
        _iconNext = LoadIcon("thumb_next.png");
        TryAddButtons();
    }

    // The taskbar only accepts thumbnail buttons once its button exists; we try now and again on TaskbarButtonCreated.
    public void Retry() => TryAddButtons();

    private void TryAddButtons()
    {
        if (_buttonsAdded) return;
        try
        {
            _taskbar ??= (TaskbarInterop.ITaskbarList3)new TaskbarInterop.TaskbarList();
            var init = _taskbar.HrInit();
            if (init != 0)
            {
                Log.Write($"taskbar HrInit failed: 0x{init:X8}");
                return;
            }
            var buttons = new[]
            {
                Button(ButtonPrev, _iconPrev, "Previous"),
                Button(ButtonPlayPause, _playing ? _iconPause : _iconPlay, _playing ? "Pause" : "Play"),
                Button(ButtonNext, _iconNext, "Next")
            };
            var hr = _taskbar.ThumbBarAddButtons(_hwnd, (uint)buttons.Length, buttons);
            _buttonsAdded = hr >= 0;
            Log.Write(_buttonsAdded ? "taskbar thumbnail buttons added" : $"taskbar ThumbBarAddButtons failed: 0x{hr:X8}");
        }
        catch (Exception e)
        {
            Log.Write("taskbar buttons: " + e);
        }
    }

    public void SetPlaying(bool playing)
    {
        if (playing == _playing) return;
        _playing = playing;
        if (!_buttonsAdded || _taskbar is null) return;
        try
        {
            var update = new[] { Button(ButtonPlayPause, playing ? _iconPause : _iconPlay, playing ? "Pause" : "Play") };
            _taskbar.ThumbBarUpdateButtons(_hwnd, 1, update);
        }
        catch
        {
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        try
        {
            if (msg == _taskbarButtonCreated)
            {
                TryAddButtons();
            }
            else if (msg == WM_COMMAND)
            {
                var w = (uint)wParam.ToInt64();
                if ((w >> 16) == THBN_CLICKED)
                {
                    ThumbButtonClicked?.Invoke(w & 0xFFFF);
                    return IntPtr.Zero;
                }
            }
        }
        catch (Exception e)
        {
            Log.Write("window proc: " + e);
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private static TaskbarInterop.THUMBBUTTON Button(uint id, IntPtr icon, string tip) => new()
    {
        dwMask = TaskbarInterop.THB_ICON | TaskbarInterop.THB_TOOLTIP | TaskbarInterop.THB_FLAGS,
        iId = id,
        hIcon = icon,
        szTip = tip,
        dwFlags = 0
    };

    // PNG-compressed icons are accepted by CreateIconFromResourceEx, so the glyphs ship as plain PNG files.
    private static IntPtr LoadIcon(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        if (!File.Exists(path)) return IntPtr.Zero;
        var bytes = File.ReadAllBytes(path);
        return CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x00030000, 32, 32, 0);
    }

    public void Dispose()
    {
        RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)1);
        foreach (var icon in new[] { _iconPrev, _iconPlay, _iconPause, _iconNext })
            if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

internal static class TaskbarInterop
{
    public const uint THB_ICON = 0x2;
    public const uint THB_TOOLTIP = 0x4;
    public const uint THB_FLAGS = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szTip;
        public uint dwFlags;
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    public class TaskbarList
    {
    }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
        [PreserveSig] int RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        [PreserveSig] int UnregisterTab(IntPtr hwndTab);
        [PreserveSig] int SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] int SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }
}
