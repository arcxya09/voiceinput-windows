using System.Runtime.InteropServices;

namespace RealtimeTranscription.Desktop;

internal sealed class AudioEndpointUnavailableException()
    : InvalidOperationException("麦克风设备已断开或禁用，请连接设备或选择可用麦克风。") { }

/// <summary>Falls back only when an explicitly selected endpoint has disappeared.</summary>
internal static class AudioEndpointSelection
{
    internal static T Open<T>(string selectedId, Func<string, T> openSelected,
        Func<T> openDefault, out bool usedDefaultFallback)
    {
        usedDefaultFallback = false;
        if (string.IsNullOrEmpty(selectedId)) return openDefault();
        try { return openSelected(selectedId); }
        catch (Exception error) when (IsUnavailable(error))
        {
            // Never repeat the default attempt, nor substitute another microphone
            // for a permission, busy-device, format, or other initialization error.
            usedDefaultFallback = true;
            return openDefault();
        }
    }

    private static bool IsUnavailable(Exception error) =>
        error is AudioEndpointUnavailableException ||
        error is COMException && error.HResult is
            unchecked((int)0x80070490) or // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
            unchecked((int)0x88890004);  // AUDCLNT_E_DEVICE_INVALIDATED
}
