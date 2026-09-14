using System.Runtime.InteropServices;

namespace MonitorToneFix.Core.X11;

/// <summary>
/// P/Invoke bindings for the subset of libX11 needed by MonitorToneFix.
/// XID (Window, Atom, etc.) is 8 bytes (unsigned long) on 64-bit Linux, matched here by nuint.
/// </summary>
internal static class Xlib
{
    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)]
    public static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport(LibX11)]
    public static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    public static extern nuint XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    public static extern int XDefaultScreen(IntPtr display);

    [DllImport(LibX11)]
    public static extern nuint XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport(LibX11)]
    public static extern int XFree(IntPtr data);

    [DllImport(LibX11)]
    public static extern int XSync(IntPtr display, bool discard);
}
