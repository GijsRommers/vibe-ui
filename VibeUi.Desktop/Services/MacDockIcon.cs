using System.Runtime.InteropServices;

namespace VibeUi.Desktop.Services;

/// <summary>
/// Sets the Dock icon when the app runs unbundled on macOS (dotnet run, make install). A bundled .app
/// takes its icon from Info.plist instead, and other platforms use the window icon.
/// </summary>
internal static class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static void Apply(string pngPath)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(pngPath))
            return;
        try
        {
            var path = Send(Class("NSString"), Selector("stringWithUTF8String:"), pngPath);
            var image = Send(Send(Class("NSImage"), Selector("alloc")), Selector("initWithContentsOfFile:"), path);
            if (image == IntPtr.Zero)
                return;
            var app = Send(Class("NSApplication"), Selector("sharedApplication"));
            Send(app, Selector("setApplicationIconImage:"), image);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            // Cosmetic only.
        }
    }

    [DllImport(ObjC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr Class(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr Selector(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, string argument);
}
