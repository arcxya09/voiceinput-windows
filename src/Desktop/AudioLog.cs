using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using RealtimeTranscription.Infrastructure;

namespace RealtimeTranscription.Desktop;

internal static class AudioLog
{
    // Correlate the chosen endpoint with inventory/notifications without exposing
    // device names, persistent endpoint IDs or user-assigned Bluetooth names.
    internal static string DeviceKey(string? id) => string.IsNullOrEmpty(id) ? "default"
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).Substring(0, 16);

    internal static void Write(RuntimeLog? log, string name, string? turn = null,
        Exception? error = null, params (string Key, object? Value)[] fields)
    {
        if (log == null) return;
        var values = new Dictionary<string, object?> { ["apartment"] = Thread.CurrentThread.GetApartmentState(),["nativeThreadId"]=GetCurrentThreadId() };
        foreach (var field in fields) values[field.Key] = field.Value;
        log.Write("Audio", name, turn, values, error);
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
