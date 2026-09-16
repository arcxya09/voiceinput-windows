using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RealtimeTranscription.Infrastructure;

/// <summary>
/// Bounded, best-effort JSONL diagnostics. Write never performs file IO; the single background
/// consumer owns all writes, rotation and export barriers. Fields must contain diagnostic metadata
/// (fixed status codes, counts, durations, formats or opaque IDs), never credentials, URLs, paths,
/// device display names, audio, transcripts, prompts or response bodies. Strings are bounded, not
/// automatically anonymized. Exceptions deliberately exclude Message, Data and source file names.
/// </summary>
public sealed class RuntimeLog : IAsyncDisposable
{
    public const string FilePrefix = "voiceinput-runtime-";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Channel<Command> _queue;
    private readonly Task _worker;
    private readonly int _maxFileBytes, _maxFiles;
    private readonly TimeSpan _retentionAge;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _started = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);
    private FileStream? _file;
    private string? _activePath;
    private long _sequence, _droppedCount;
    private int _accepting = 1, _available, _fileNumber;
    private string? _lastError;
    private DateTime _retryAfter, _fileDate;

    public RuntimeLog(string logDirectory, int maxFileBytes = 2 * 1024 * 1024,
        int maxFiles = 10, TimeSpan? retentionAge = null, int queueCapacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(logDirectory);
        if (maxFileBytes < 512) throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (maxFiles < 1) throw new ArgumentOutOfRangeException(nameof(maxFiles));
        if (queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (retentionAge is { } age && age <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retentionAge));
        DirectoryPath = logDirectory;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        _retentionAge = retentionAge ?? TimeSpan.FromDays(7);
        _queue = Channel.CreateBounded<Command>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
        });
        _worker = Task.Run(RunAsync);
    }

    public string DirectoryPath { get; }
    public bool IsAvailable => Volatile.Read(ref _available) != 0;
    /// <summary>The latest logging IO failure, containing only its type and HRESULT.</summary>
    public string? LastError => Volatile.Read(ref _lastError);
    /// <summary>Records rejected by the queue, written after disposal, or lost to a write failure.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool Write(string component, string eventName, string? correlationId = null,
        IReadOnlyDictionary<string, object?>? fields = null, Exception? exception = null)
    {
        try
        {
            if (Volatile.Read(ref _accepting) != 0)
            {
                var entry = new Entry(DateTimeOffset.UtcNow, Environment.CurrentManagedThreadId, Identifier(component, 48), Identifier(eventName, 80),
                    correlationId is null ? null : Identifier(correlationId, 80), Snapshot(fields),
                    exception is null ? null : Describe(exception));
                if (_queue.Writer.TryWrite(new WriteCommand(entry))) return true;
            }
        }
        catch
        {
            // Even malformed/mutating diagnostic fields must not break microphone or ASR work.
        }
        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    /// <summary>Processes preceding writes and flushes the file, or reports current IO failure. See DroppedCount for lost records.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        ControlAsync(new FlushCommand(cancellationToken), cancellationToken);

    /// <summary>
    /// Exports complete JSONL records from recent app instances and this instance's preceding writes.
    /// Cancellation/failure preserves an existing destination; publishing uses a temporary sibling file.
    /// Other live instances are sampled at their last complete line, without blocking their writers.
    /// </summary>
    public Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        ControlAsync(new ExportCommand(destinationPath, cancellationToken), cancellationToken);

    private async Task ControlAsync(ControlCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _accepting) == 0, this);
        try { await _queue.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(RuntimeLog)); }
        // Once queued, let the worker acknowledge cancellation after disposing source/output handles
        // and deleting its temporary file. Returning earlier could leave cleanup running after export.
        await command.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _accepting, 0) != 0) _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        try { EnsureFile(); }
        catch (Exception error) { FailWriter(error); }
        try
        {
            await foreach (var command in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (command is WriteCommand write)
                {
                    try { await AppendAsync(write.Value).ConfigureAwait(false); }
                    catch (Exception error) { Interlocked.Increment(ref _droppedCount); FailWriter(error); }
                    continue;
                }
                var control = (ControlCommand)command;
                try
                {
                    control.CancellationToken.ThrowIfCancellationRequested();
                    if (control is ExportCommand export)
                    {
                        string? writerError = null;
                        try { await FlushCoreAsync(control.CancellationToken).ConfigureAwait(false); }
                        catch (IOException) { writerError = LastError; }
                        await ExportCoreAsync(export.Destination, export.CancellationToken, writerError).ConfigureAwait(false);
                    }
                    else await FlushCoreAsync(control.CancellationToken).ConfigureAwait(false);
                    control.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (control.CancellationToken.IsCancellationRequested)
                { control.Completion.TrySetCanceled(control.CancellationToken); }
                catch (Exception error) { control.Completion.TrySetException(error); }
            }
        }
        finally
        {
            try { if (_file is not null) await _file.FlushAsync().ConfigureAwait(false); }
            catch (Exception error) { Volatile.Write(ref _lastError, FailureCode(error)); }
            CloseFile();
            Prune();
        }
    }

    private async Task AppendAsync(Entry entry)
    {
        if (_file is null && DateTime.UtcNow < _retryAfter)
        {
            Interlocked.Increment(ref _droppedCount);
            return;
        }
        EnsureFile();
        var bytes = Serialize(entry, ++_sequence);
        if (_file!.Length > 0 && _file.Length + bytes.Length > _maxFileBytes)
        {
            await _file.FlushAsync().ConfigureAwait(false);
            CloseFile();
            EnsureFile();
        }
        await _file!.WriteAsync(bytes).ConfigureAwait(false);
        Volatile.Write(ref _available, 1);
        Volatile.Write(ref _lastError, null);
    }

    private void EnsureFile()
    {
        DateTime today = DateTime.UtcNow.Date;
        if (_file is not null && _fileDate == today) return;
        if (_file is not null) CloseFile();
        Directory.CreateDirectory(DirectoryPath);
        string path = Path.Combine(DirectoryPath,
            $"{FilePrefix}{_started}-{Environment.ProcessId}-{_instanceId}-{_fileNumber++:D5}.log");
        // Unique immutable names avoid rename/rotation races across processes. Read sharing permits
        // exports while denying another writer or deletion of this active file on Windows.
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _activePath = path;
        _fileDate = today;
        _retryAfter = DateTime.MinValue;
        Volatile.Write(ref _available, 1);
        Volatile.Write(ref _lastError, null);
        Prune();
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureFile();
            await _file!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            FailWriter(error);
            throw new IOException("Runtime log is unavailable: " + FailureCode(error));
        }
    }

    private void FailWriter(Exception error)
    {
        CloseFile();
        Volatile.Write(ref _lastError, FailureCode(error));
        _retryAfter = DateTime.UtcNow.AddSeconds(5);
    }

    private void CloseFile()
    {
        try { _file?.Dispose(); }
        catch (Exception error) { Volatile.Write(ref _lastError, FailureCode(error)); }
        _file = null;
        _activePath = null;
        Volatile.Write(ref _available, 0);
    }

    private FileInfo[] Files() => new DirectoryInfo(DirectoryPath)
        .GetFiles(FilePrefix + "*.log", SearchOption.TopDirectoryOnly)
        .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
        .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();

    private void Prune()
    {
        try
        {
            var files = Files();
            int remaining = files.Length;
            DateTime cutoff = DateTime.UtcNow - _retentionAge;
            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc >= cutoff && remaining <= _maxFiles) continue;
                if (string.Equals(file.FullName, _activePath is null ? null : Path.GetFullPath(_activePath), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // A live writer/export holds a shared file handle. An exclusive probe also
                    // detects these handles on Unix, where unlink itself need not honor sharing.
                    using (new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    File.Delete(file.FullName);
                    remaining--;
                }
                catch (FileNotFoundException) { remaining--; }
                catch (IOException) { /* Another app instance or export may own this file. */ }
                catch (UnauthorizedAccessException error) { Volatile.Write(ref _lastError, FailureCode(error)); }
            }
        }
        catch (Exception error) { Volatile.Write(ref _lastError, FailureCode(error)); }
    }

    private async Task ExportCoreAsync(string destinationPath, CancellationToken cancellationToken, string? writerError)
    {
        string destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".log", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The export destination must have a .log extension.", nameof(destinationPath));
        if (string.Equals(Path.GetDirectoryName(destination), Path.TrimEndingDirectorySeparator(Path.GetFullPath(DirectoryPath)), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(destination).StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The export destination is reserved for active runtime logs.", nameof(destinationPath));

        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        var sources = new List<(FileStream Stream, long Length)>();
        try
        {
            // Hold each source open until completion so another instance's retention cannot delete
            // it mid-export. Capture lengths before copying to avoid chasing concurrent appends.
            DateTime cutoff = DateTime.UtcNow - _retentionAge;
            foreach (var file in Files().Where(f => f.LastWriteTimeUtc >= cutoff).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                sources.Add((source, source.Length));
            }
            if (sources.Count == 0 && writerError is not null)
                throw new IOException("No saved runtime logs are available: " + writerError);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var header = new Entry(DateTimeOffset.UtcNow, Environment.CurrentManagedThreadId, "runtime_log", "export_snapshot", null,
                    new() { ["sourceFiles"] = sources.Count, ["droppedCount"] = DroppedCount, ["formatVersion"] = 1,
                        ["incomplete"] = writerError is not null || DroppedCount > 0, ["writerError"] = writerError }, null);
                await output.WriteAsync(Serialize(header, 0), cancellationToken).ConfigureAwait(false);
                byte[] buffer = new byte[64 * 1024];
                foreach (var (source, length) in sources)
                {
                    long completeLength = await LastCompleteLineAsync(source, length, buffer, cancellationToken).ConfigureAwait(false);
                    source.Position = 0;
                    while (completeLength > 0)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, completeLength)), cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new IOException("A runtime log changed during export.");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        completeLength -= read;
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            foreach (var (source, _) in sources) source.Dispose();
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<long> LastCompleteLineAsync(FileStream source, long length, byte[] buffer, CancellationToken cancellationToken)
    {
        while (length > 0)
        {
            int count = (int)Math.Min(buffer.Length, length);
            long start = length - count;
            source.Position = start;
            await source.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            for (int i = count - 1; i >= 0; i--) if (buffer[i] == (byte)'\n') return start + i + 1;
            length = start;
        }
        return 0;
    }

    private byte[] Serialize(Entry entry, long sequence)
    {
        var record = new Dictionary<string, object?>
        {
            ["timestamp"] = entry.Timestamp, ["processId"] = Environment.ProcessId, ["instanceId"] = _instanceId,
            ["threadId"] = entry.ThreadId, ["sequence"] = sequence, ["component"] = entry.Component, ["event"] = entry.Event
        };
        if (entry.CorrelationId is not null) record["correlationId"] = entry.CorrelationId;
        if (entry.Fields is { Count: > 0 }) record["fields"] = entry.Fields;
        if (entry.Exception is not null) record["exception"] = entry.Exception;
        return Utf8.GetBytes(JsonSerializer.Serialize(record) + "\n");
    }

    private static Dictionary<string, object?>? Snapshot(IReadOnlyDictionary<string, object?>? fields)
    {
        if (fields is null || fields.Count == 0) return null;
        var snapshot = new Dictionary<string, object?>();
        foreach (var (name, value) in fields.Take(32)) snapshot[Identifier(name, 48)] = value switch
        {
            null => null,
            string text => OneLine(text, 256),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
            float number when float.IsFinite(number) => number,
            double number when double.IsFinite(number) => number,
            Enum choice => OneLine(choice.ToString(), 80),
            Guid id => id.ToString("N"),
            _ => "[unsupported]"
        };
        return snapshot;
    }

    private static Dictionary<string, object?> Describe(Exception exception, int depth = 0)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = OneLine(exception.GetType().FullName ?? exception.GetType().Name, 192),
            ["hresult"] = "0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
            ["stack"] = new StackTrace(exception, false).GetFrames()?.Take(24).Select(frame =>
            {
                var method = frame.GetMethod();
                return OneLine((method?.DeclaringType?.FullName ?? "unknown") + "." + (method?.Name ?? "unknown"), 192);
            }).ToArray() ?? []
        };
        if (exception is HttpRequestException { StatusCode: { } status }) result["httpStatusCode"] = (int)status;
        if (exception is System.Net.WebSockets.WebSocketException socket) result["webSocketErrorCode"] = socket.WebSocketErrorCode.ToString();
        if (exception is System.ComponentModel.Win32Exception native) result["nativeErrorCode"] = native.NativeErrorCode;
        if (depth < 3 && exception.InnerException is { } inner) result["inner"] = Describe(inner, depth + 1);
        return result;
    }

    private static string FailureCode(Exception error) => error.GetType().Name + " (0x" + error.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")";
    private static string Identifier(string? text, int limit) => new((text ?? "unknown").Take(limit)
        .Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
    private static string OneLine(string text, int limit)
    {
        int length = Math.Min(text.Length, limit);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return new string(text.Take(length).Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c).ToArray());
    }

    private sealed record Entry(DateTimeOffset Timestamp, int ThreadId, string Component, string Event, string? CorrelationId,
        Dictionary<string, object?>? Fields, Dictionary<string, object?>? Exception);
    private abstract record Command;
    private sealed record WriteCommand(Entry Value) : Command;
    private abstract record ControlCommand(CancellationToken CancellationToken) : Command
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record FlushCommand(CancellationToken Token) : ControlCommand(Token);
    private sealed record ExportCommand(string Destination, CancellationToken Token) : ControlCommand(Token);
}
