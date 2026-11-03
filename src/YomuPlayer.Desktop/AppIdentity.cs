using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace YomuPlayer.Desktop;

// Gives the unpackaged app a Windows identity so the media overlay shows its name and icon instead of
// "Unknown app", and registers the yomu:// link scheme used by "open in app".
public static class AppIdentity
{
    public static string AppId => AppPaths.AppId;
    public static string DisplayName => AppPaths.ProductName;
    public static string Scheme => AppPaths.Scheme;

    // Runs as soon as the assembly loads, before any window or media session exists: Windows only honors the
    // process AppUserModelID if it is set before the first UI is shown.
    [ModuleInitializer]
    internal static void SetProcessIdEarly()
    {
        try
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(AppId);
            Log.Write($"AppUserModelID set (hr=0x{hr:X8})");
        }
        catch (Exception e)
        {
            Log.Write("AppUserModelID: " + e);
        }
    }

    public static void Register()
    {
        // Portable builds leave the shell, Start Menu and protocol handler untouched.
        if (AppPaths.IsPortable) return;

        Try("start menu shortcut", CreateStartMenuShortcut);
        Try("link scheme", RegisterProtocol);
        Try("app id registry entry", RegisterAppIdEntry);
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Write($"{what}: {e}"); }
    }

    // A Start Menu shortcut that carries the AppUserModelID is what Windows uses to resolve the app's name and icon.
    private static void CreateStartMenuShortcut()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk");
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        var marker = Path.Combine(AppPaths.DataRoot, "shortcut.target");

        // A tiny marker avoids opening/saving the .lnk (COM + shell) on every boot. If the exe moved, the marker
        // no longer matches and the shortcut is rewritten once.
        try
        {
            if (File.Exists(shortcut) && File.Exists(marker)
                && string.Equals(File.ReadAllText(marker).Trim(), exe, StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch
        {
            // fall through and rewrite
        }

        var link = (IShellLinkW)new ShellLinkCom();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
        link.SetDescription(DisplayName);
        link.SetIconLocation(File.Exists(icon) ? icon : exe, 0);

        var store = (IPropertyStore)link;
        var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

        // VT_LPWSTR. InitPropVariantFromString is not exported by propsys.dll on every Windows build, so build the
        // variant directly; PropVariantClear releases the CoTaskMem string.
        var value = new PropVariant { vt = 31, p1 = Marshal.StringToCoTaskMemUni(AppId) };
        try { store.SetValue(ref key, ref value); }
        finally { PropVariantClear(ref value); }

        store.Commit();
        ((IPersistFile)link).Save(shortcut, true);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, exe);
        }
        catch
        {
            // marker is only a boot-time optimization
        }
        Log.Write("start menu shortcut written: " + shortcut + " exists=" + File.Exists(shortcut));
    }

    private static void RegisterAppIdEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        key.SetValue("DisplayName", DisplayName);
        var logo = Path.Combine(AppContext.BaseDirectory, "Assets", "app_logo.png");
        if (File.Exists(logo)) key.SetValue("IconUri", logo);
    }

    private static void RegisterProtocol()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;

        using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
        root.SetValue("", "URL:" + DisplayName);
        root.SetValue("URL Protocol", "");
        using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue("", exe + ",0");
        using (var cmd = root.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + exe + "\" \"%1\"");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr p1;
        public IntPtr p2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCom
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
