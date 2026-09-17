using System.Net.Http.Headers;
using System.Text.Json;
using RealtimeTranscription.Core;

namespace RealtimeTranscription.Infrastructure;

public sealed record AsrReviewResult(string Text, double? Duration);

public sealed class AsrReviewClient : IDisposable
{
    public const string Model = "qwen-audio-3.0-asr-flash";
    private readonly HttpClient http;
    public AsrReviewClient(HttpMessageHandler? handler = null)
    {
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) });
        http.Timeout = Timeout.InfiniteTimeSpan;
    }
    public static Uri Endpoint(AppSettings settings)
        => new($"https://{settings.AsrUri().Host}/api/v1/services/aigc/multimodal-generation/generation");
    public static byte[] Request(byte[] wav, IReadOnlyList<HotwordUsage> terms, bool context)
    {
        var messages = new List<object>();
        if (context && terms.Count > 0)
            messages.Add(new { role = "user", content = new[] { new { type = "input_text", text = JsonCodec.Take("领域词：" + string.Join("、", terms.Select(t => t.Text)), 400) } } });
        messages.Add(new { role = "user", content = new[] { new { type = "input_audio", input_audio = new { data = "data:audio/wav;base64," + Convert.ToBase64String(wav) } } } });
        var parameters = new Dictionary<string, object> { ["format"] = "wav", ["sample_rate"] = "16000", ["language_hints"] = new[] { "zh", "en" } };
        if (terms.Count > 0) parameters["vocabulary"] = terms.GroupBy(t => t.Text).ToDictionary(g => g.Key, g => Math.Clamp(g.Max(t => t.Weight), 1, 5));
        return JsonSerializer.SerializeToUtf8Bytes(new { model = Model, input = new { messages }, parameters });
    }
    public async Task<AsrReviewResult> RecognizeAsync(AppSettings settings, string key, byte[] wav, IReadOnlyList<HotwordUsage> terms, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        request.Headers.Add("X-DashScope-SSE", "disable");
        byte[] body = Request(wav, terms, settings.AsrContext);
        try
        {
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ProviderException($"整段语音复核失败（HTTP {(int)response.StatusCode}），使用实时结果。");
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > 1024 * 1024) throw new ProviderException("整段语音复核响应过长。");
                bytes.Write(buffer, 0, count);
            }
            using var doc = JsonDocument.Parse(bytes.ToArray()); var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(code.GetString()))
                throw new ProviderException("整段语音复核返回错误。");
            // output.text is the complete accumulated transcript; sentence.text can
            // contain only the last sentence and must never be used as a fallback.
            string text = root.GetProperty("output").GetProperty("text").GetString() ?? "";
            double? duration = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double seconds) && double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
            return new(text.Trim(), duration);
        }
        finally { Array.Clear(body); }
    }
    public void Dispose() => http.Dispose();
}
