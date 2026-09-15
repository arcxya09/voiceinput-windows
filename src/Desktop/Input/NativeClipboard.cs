using System.Runtime.InteropServices;
using System.Text;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Desktop.Input;

// Runs only in the killable UIA worker. Eager text survives worker exit.
// Restore only after verified insertion (or before any paste was submitted).
internal static class NativeClipboard
{
    private const uint UnicodeText = 13;
    private sealed record SavedFormat(uint Format, IntPtr Handle);
    private static readonly List<SavedFormat> saved = [];
    private static bool haveSnapshot;
    private static uint? ownedSequence;

    internal static void Forget()
    {
        foreach(var item in saved)
        {
            if(item.Format == 2) DeleteObject(item.Handle);
            else GlobalFree(item.Handle);
        }
        saved.Clear(); haveSnapshot=false; ownedSequence=null;
    }

    // Freeze every enumerated format before EmptyClipboard, under the same lock.
    // Never keep lazy IDataObject references backed by the clipboard being replaced.
    private static bool Snapshot()
    {
        Forget();
        ulong total=0; uint format=0;
        while(true)
        {
            Marshal.SetLastPInvokeError(0);
            format=EnumClipboardFormats(format);
            if(format==0)
            {
                if(Marshal.GetLastPInvokeError()!=0){Forget();return false;}
                haveSnapshot=true; return true;
            }
            // These are OLE transport bookkeeping, not transferable payloads.
            // Replaying the old marshalled IDataObject identity makes OleGetClipboard
            // contact a disconnected owner instead of reconstructing our frozen formats.
            if(format>=0xc000)
            {
                var name=new StringBuilder(256);
                if(GetClipboardFormatName(format,name,name.Capacity)==0){Forget();return false;}
                if(name.ToString() is "DataObject" or "Ole Private Data" or "Ole Source Window")continue;
            }
            // Owner-display, private handles, palettes and metafiles cannot be
            // safely frozen by this implementation. Leave their clipboard intact.
            if(saved.Count>=128 || format is 3 or 9 or 14 || (format>=0x80&&format<0xc000))
            {Forget();return false;}
            IntPtr source=GetClipboardData(format);
            if(source==IntPtr.Zero){Forget();return false;}
            ulong size;
            if(format==2)
            {
                if(GetObject(source,Marshal.SizeOf<BitmapInfo>(),out var bitmap)==0){Forget();return false;}
                size=(ulong)Math.Abs((long)bitmap.Height)*(ulong)Math.Abs((long)bitmap.WidthBytes);
            }
            else size=GlobalSize(source).ToUInt64();
            total+=size;
            if(size==0||total>32*1024*1024){Forget();return false;}
            var copy=OleDuplicateData(source,(ushort)format,2);
            if(copy==IntPtr.Zero){Forget();return false;}
            saved.Add(new(format,copy));
        }
    }

    internal static string Restore()
    {
        if(!haveSnapshot||ownedSequence is not {} sequence)return "ClipboardNotOwned";
        IntPtr owner=CreateWindowEx(0,"STATIC","",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,Win32.GetModuleHandle(null),IntPtr.Zero);
        if(owner==IntPtr.Zero)return "ClipboardRestoreUnavailable";
        try
        {
            // OpenClipboard also ensures the paste consumer has released its lock.
            if(!OpenClipboard(owner))return "ClipboardRestoreBusy";
            try
            {
                if(Win32.GetClipboardSequenceNumber()!=sequence)return "ClipboardNewCopyPreserved";
                if(!EmptyClipboard())return "ClipboardRestoreUnavailable";
                bool complete=true;
                for(int i=0;i<saved.Count;)
                {
                    var item=saved[i];
                    if(SetClipboardData(item.Format,item.Handle)!=IntPtr.Zero)saved.RemoveAt(i);
                    else {complete=false;i++;}
                }
                return complete?"ClipboardRestored":"ClipboardRestoreIncomplete";
            }
            finally{CloseClipboard();}
        }
        finally{DestroyWindow(owner);}
    }


    internal static uint? Prepare(string text, NativeTarget target, uint expectedSequence, out string diagnostic)
    {
        Forget();
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
                ownedSequence=Win32.GetClipboardSequenceNumber();
                return ownedSequence;
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
                    diagnostic = "ClipboardBackupUnsupported";
                    if(!Snapshot())return false;
                    if(!Safe(target)){diagnostic="ClipboardTargetChangedDuringBackup";return false;}
                    diagnostic = "ClipboardEmptyFailed";
                    if (!EmptyClipboard()) return false;
                    diagnostic = "ClipboardSetFailed";
                    if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero)
                    {
                        // No paste has been submitted. Roll back while still holding the lock.
                        for(int i=saved.Count-1;i>=0;i--)
                            if(SetClipboardData(saved[i].Format,saved[i].Handle)!=IntPtr.Zero)saved.RemoveAt(i);
                        return false;
                    }
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

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public int Type,Width,Height,WidthBytes; public ushort Planes,BitsPixel; public IntPtr Bits; }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClipboardFormatName(uint format,StringBuilder name,int count);
    [DllImport("ole32.dll")] private static extern IntPtr OleDuplicateData(IntPtr data,ushort format,uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")] private static extern int GetObject(IntPtr value,int size,out BitmapInfo bitmap);
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
