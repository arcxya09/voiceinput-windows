using System.Runtime.InteropServices;
using System.Text;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop.Input;

// Runs only in the killable UIA worker. Eager CF_UNICODETEXT survives the worker
// exiting; there is no delayed-rendering object and no timer restoring old data.
internal static class NativeClipboard
{
    private const uint UnicodeText = 13;

    internal static uint? Prepare(string text, NativeTarget target, uint expectedSequence, out string diagnostic)
    {
        diagnostic = "ClipboardTextUnsupported";
        if (!ClipboardPaste.IsSupportedText(text) || text.Length == 0) return null;
        // CF_UNICODETEXT uses CR-LF; only normalize line endings, preserving
        // every other character (including supplementary Unicode characters).
        string windowsText = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] bytes = Encoding.Unicode.GetBytes(windowsText + '\0');
        if (!Write(bytes, target, expectedSequence, out diagnostic)) return null;

        // Closing the write and destroying its temporary owner can finalize
        // native clipboard bookkeeping. Reopen read-only and verify our exact
        // payload before taking the final sequence; never blindly adopt a newer
        // sequence that might belong to somebody else's copy.
        diagnostic = "ClipboardReadBusy";
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!Safe(target)) { diagnostic = "ClipboardTargetChangedAfterWrite"; return null; }
            if (!OpenClipboard(IntPtr.Zero)) { if (attempt < 5) Thread.Sleep(20); continue; }
            try
            {
                diagnostic = "ClipboardPayloadChanged";
                IntPtr data = GetClipboardData(UnicodeText);
                if (data == IntPtr.Zero || GlobalSize(data).ToUInt64() < (ulong)bytes.Length) return null;
                IntPtr pointer = GlobalLock(data);
                if (pointer == IntPtr.Zero) return null;
                bool matches;
                try
                {
                    // Bound reads/allocation by our own text, never by a foreign
                    // clipboard allocation size. The terminator is part of the comparison.
                    byte[] actual = new byte[bytes.Length];
                    Marshal.Copy(pointer, actual, 0, actual.Length);
                    matches = actual.AsSpan().SequenceEqual(bytes);
                }
                finally { GlobalUnlock(data); }
                if (!matches) return null;
                diagnostic = "ClipboardPrepared";
                return Win32.GetClipboardSequenceNumber();
            }
            finally { CloseClipboard(); }
        }
        return null;
    }

    private static bool Write(byte[] bytes, NativeTarget target, uint expectedSequence, out string diagnostic)
    {
        diagnostic = "ClipboardAllocationFailed";
        IntPtr memory = GlobalAlloc(2, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) return false;
        IntPtr owner = IntPtr.Zero;
        try
        {
            IntPtr buffer = GlobalLock(memory);
            if (buffer == IntPtr.Zero) return false;
            try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
            finally { GlobalUnlock(memory); }

            owner = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);
            diagnostic = "ClipboardOwnerUnavailable";
            if (owner == IntPtr.Zero) return false;
            // Clipboard contention may clear briefly. Do not overwrite a newer
            // copy or continue when focus/key state changes during these waits.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                diagnostic = "ClipboardChangedBeforeWrite";
                if (!Safe(target) || Win32.GetClipboardSequenceNumber() != expectedSequence) return false;
                diagnostic = "ClipboardWriteBusy";
                if (!OpenClipboard(owner)) { if (attempt < 5) Thread.Sleep(20); continue; }
                try
                {
                    diagnostic = "ClipboardChangedBeforeWrite";
                    if (!Safe(target) || Win32.GetClipboardSequenceNumber() != expectedSequence) return false;
                    diagnostic = "ClipboardEmptyFailed";
                    if (!EmptyClipboard()) return false;
                    diagnostic = "ClipboardSetFailed";
                    if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero) return false;
                    memory = IntPtr.Zero; // Ownership transferred to Windows.
                    return true;
                }
                finally { CloseClipboard(); }
            }
            return false;
        }
        finally
        {
            // The worker blocks on its private pipe between requests. Do not
            // leave an unpumped clipboard-owner window behind: a later copy in
            // another app would otherwise wait for WM_DESTROYCLIPBOARD.
            if (owner != IntPtr.Zero) DestroyWindow(owner);
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    private static bool Safe(NativeTarget target) => Win32.Observe(target) == FocusObservation.Stable
        && !Win32.Composing(target.Focus) && !new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(Win32.Down);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
}
