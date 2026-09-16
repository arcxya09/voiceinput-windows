using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RealtimeTranscription.Infrastructure;

internal static class RuntimeLogRegression
{
    private const string Secret = "sk-regression-secret-不要写入的转写正文";

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static async Task Reject<T>(Func<Task> operation) where T : Exception
    {
        try { await operation().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (T) { return; }
        throw new Exception("应抛出 " + typeof(T).Name);
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "RuntimeLogRegression-" + Guid.NewGuid().ToString("N"));

        public Folder() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, true);
    }

    private static string[] LogFiles(string directory) =>
        Directory.GetFiles(directory, RuntimeLog.FilePrefix + "*")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private static byte[] ReadBytes(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        return copy.ToArray();
    }

    private static JsonElement[] Records(string path)
    {
        byte[] bytes = ReadBytes(path);
        // Throw on invalid UTF-8 instead of accepting a split multibyte character.
        string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        if (text.Length == 0) return [];
        Check(text.EndsWith('\n'), "日志或导出文件必须以完整换行结束");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(row => row.GetProperty("event").GetString() != "export_snapshot").ToArray();
    }

    private static void OrderedPerInstance(IEnumerable<JsonElement> records)
    {
        foreach (var group in records.GroupBy(row => row.GetProperty("instanceId").GetString()))
        {
            long[] sequence = group.Select(row => row.GetProperty("sequence").GetInt64()).ToArray();
            Check(sequence.All(value => value > 0), "日志序号必须为正数");
            Check(sequence.Zip(sequence.Skip(1), (left, right) => left < right).All(value => value),
                "同一实例的导出序号必须严格递增且不能重复");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception PrivateFailure()
    {
        try { throw new InvalidOperationException(Secret, new IOException(Secret)); }
        catch (InvalidOperationException exception) { return exception; }
    }

    private sealed class UnsafeDiagnosticField
    {
        public override string ToString() => throw new InvalidOperationException("不得调用任意对象的ToString: " + Secret);
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("运行日志保存结构化事件，异常只包含类型、HRESULT和无路径方法栈", async () =>
        {
            using var folder = new Folder();
            await using var log = new RuntimeLog(folder.File("logs"));
            var metadata = new Dictionary<string, object?>
            {
                ["count"] = 3, ["enabled"] = true,
                ["label"] = string.Concat(Enumerable.Repeat("诊断\t字段\r\n", 90)),
                ["object"] = new UnsafeDiagnosticField()
            };
            int writeThreadId = Environment.CurrentManagedThreadId;
            Check(log.Write("regression", "started", "session-1", metadata), "正常事件应进入日志队列");
            metadata["count"] = 99;
            metadata["label"] = Secret;
            Check(log.Write("regression", "failed", "session-1", exception: PrivateFailure()),
                "异常事件应进入日志队列");
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(log.IsAvailable && log.LastError == null && log.DroppedCount == 0,
                "成功刷新后日志应可用且没有失败或丢弃计数");
            Check(System.IO.Path.GetFullPath(log.DirectoryPath) == System.IO.Path.GetFullPath(folder.File("logs")),
                "日志目录应可通过公开属性取得");

            var files = LogFiles(log.DirectoryPath);
            var rows = files.SelectMany(Records).ToArray();
            Check(rows.Length == 2, "刷新必须保存此前接受的所有事件");
            OrderedPerInstance(rows);
            foreach (var row in rows)
            {
                Check(row.GetProperty("timestamp").TryGetDateTimeOffset(out _), "时间戳应为标准日期时间");
                Check(row.GetProperty("processId").GetInt32() == Environment.ProcessId, "进程ID应正确");
                Check(row.GetProperty("threadId").GetInt32() == writeThreadId,
                    "线程ID应捕获调用Write的线程，不能记录后台落盘线程");
                Check(!string.IsNullOrWhiteSpace(row.GetProperty("instanceId").GetString()), "实例ID不能为空");
                Check(row.GetProperty("component").GetString() == "regression", "组件名应保存");
                Check(row.GetProperty("correlationId").GetString() == "session-1", "关联ID应保存");
            }

            var fields = rows.Single(row => row.GetProperty("event").GetString() == "started").GetProperty("fields");
            Check(fields.GetProperty("count").GetInt32() == 3 && fields.GetProperty("enabled").GetBoolean(),
                "结构化字段应保留类型，且调用方后续修改不能改变已入队快照");
            string label = fields.GetProperty("label").GetString()!;
            Check(label.Length <= 256 && !label.Any(char.IsControl), "字符串诊断字段应截断且移除控制字符");
            Check(fields.GetProperty("object").ValueKind == JsonValueKind.String,
                "不支持的对象应使用安全占位符，不能调用ToString或递归序列化");
            var failure = rows.Single(row => row.GetProperty("event").GetString() == "failed").GetProperty("exception");
            Check(failure.GetProperty("type").GetString()!.EndsWith(nameof(InvalidOperationException), StringComparison.Ordinal),
                "异常应记录类型");
            Check(failure.TryGetProperty("hresult", out var hresult) &&
                (hresult.ValueKind == JsonValueKind.Number || !string.IsNullOrEmpty(hresult.GetString())),
                "异常应记录HRESULT");
            var stack = failure.GetProperty("stack");
            Check(stack.ValueKind == JsonValueKind.Array && stack.GetArrayLength() > 0,
                "已抛出异常应保留方法栈以便诊断");
            foreach (var frame in stack.EnumerateArray())
            {
                string method = frame.GetString()!;
                Check(!method.Contains('/') && !method.Contains('\\') && !method.Contains(":line ", StringComparison.Ordinal),
                    "方法栈不得包含源文件路径或行号");
            }
            Check(!failure.TryGetProperty("message", out _) && !failure.TryGetProperty("innerException", out _),
                "不得序列化异常消息或完整内部异常");
            string export = folder.File("diagnostics.log");
            await log.ExportAsync(export).WaitAsync(TimeSpan.FromSeconds(10));
            Check(Records(export).Length == 2, "导出应包含已刷新的事件");
            foreach (string path in files.Append(export))
                Check(!Encoding.UTF8.GetString(ReadBytes(path)).Contains("sk-regression-secret", StringComparison.Ordinal),
                    "日志及导出不得泄漏异常消息中的密钥和正文");
        });

        await test("运行日志按大小轮转并按时间和数量清理，仅处理规定前缀", async () =>
        {
            using var folder = new Folder();
            string logDirectory = folder.File("logs");
            Directory.CreateDirectory(logDirectory);
            string expired = System.IO.Path.Combine(logDirectory, RuntimeLog.FilePrefix + "20990101-expired.log");
            string recent = System.IO.Path.Combine(logDirectory, RuntimeLog.FilePrefix + "20000101-recent.log");
            string unrelated = System.IO.Path.Combine(logDirectory, "history-private.log");
            System.IO.File.WriteAllText(expired, "expired\n");
            System.IO.File.WriteAllText(recent,
                "{\"instanceId\":\"previous-instance\",\"sequence\":1,\"event\":\"recent-history\",\"correlationId\":\"previous\"}\n");
            System.IO.File.WriteAllText(unrelated, "必须保留的其他文件");
            System.IO.File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-30));
            System.IO.File.SetLastWriteTimeUtc(recent, DateTime.UtcNow);
            System.IO.File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));

            await using var log = new RuntimeLog(logDirectory, maxFileBytes: 512, maxFiles: 3,
                retentionAge: TimeSpan.FromDays(7));
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(!System.IO.File.Exists(expired) && System.IO.File.Exists(recent),
                "过期判断必须依据最后写入时间，不能依据文件名中的日期");
            for (int i = 0; i < 80; i++)
                Check(log.Write("regression", "rotation", "entry-" + i), "轮转测试事件不应被丢弃");
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(!System.IO.File.Exists(expired), "过期的本应用日志应清理");
            Check(System.IO.File.ReadAllText(unrelated) == "必须保留的其他文件", "清理不得删除其他文件");
            string[] files = LogFiles(logDirectory);
            Check(files.Length > 1 && files.Length <= 3, "应发生轮转且文件数不超过保留上限");
            Check(files.All(path => new FileInfo(path).Length <= 512), "普通短事件应遵守单文件大小上限");
            var retained = files.SelectMany(Records).ToArray();
            Check(retained.Any(row => row.GetProperty("correlationId").GetString() == "entry-79"),
                "清理必须保留最新活动文件中的事件");
            OrderedPerInstance(retained);
        });

        await test("两个运行日志实例互不覆盖，另一实例轮转不能删除活动文件", async () =>
        {
            using var folder = new Folder();
            string logDirectory = folder.File("logs");
            await using var first = new RuntimeLog(logDirectory, maxFileBytes: 512, maxFiles: 1);
            Check(first.Write("regression", "first-alive", "first-before"), "第一个实例应接受事件");
            await first.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string firstActiveFile = LogFiles(logDirectory).Single();

            await using var second = new RuntimeLog(logDirectory, maxFileBytes: 512, maxFiles: 1);
            for (int i = 0; i < 40; i++)
                Check(second.Write("regression", "second-rotation", "second-" + i), "第二个实例应接受事件");
            await second.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(System.IO.File.Exists(firstActiveFile), "全局保留限制不能删除其他实例仍在写入的文件");
            Check(Records(firstActiveFile).Any(row => row.GetProperty("event").GetString() == "first-alive"),
                "另一个实例的轮转不得覆盖原文件");

            string export = folder.File("all-instances.log");
            await first.ExportAsync(export).WaitAsync(TimeSpan.FromSeconds(10));
            var rows = Records(export);
            Check(rows.Any(row => row.GetProperty("correlationId").GetString() == "first-before") &&
                rows.Any(row => row.GetProperty("correlationId").GetString() == "second-39"),
                "导出应收集目录内所有实例的近期日志");
            Check(rows.Select(row => row.GetProperty("instanceId").GetString()).Distinct().Count() == 2,
                "同一进程创建的多个日志实例也应具有不同实例ID");
            OrderedPerInstance(rows);
            Check(first.Write("regression", "first-still-alive"), "另一实例轮转后原实例仍应可写");
            await first.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        });

        await test("并发写入和导出保持完整JSON行、实例内顺序和此前事件不遗漏", async () =>
        {
            using var folder = new Folder();
            await using var log = new RuntimeLog(folder.File("logs"), queueCapacity: 4096);
            const int beforeCount = 24, writers = 4, perWriter = 120;
            for (int i = 0; i < beforeCount; i++)
                Check(log.Write("regression", "before-export", "before-" + i), "导出前事件应接受");
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = new ConcurrentBag<string>();
            var producers = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
            {
                await go.Task;
                for (int i = 0; i < perWriter; i++)
                {
                    string id = "writer-" + writer + "-" + i;
                    if (log.Write("regression", "concurrent", id)) accepted.Add(id);
                }
            })).ToArray();
            var snapshots = Enumerable.Range(0, 3).Select(index => Task.Run(async () =>
            {
                await go.Task;
                string export = folder.File("snapshot-" + index + ".log");
                await log.ExportAsync(export);
                var rows = Records(export);
                var before = rows.Where(row => row.GetProperty("event").GetString() == "before-export").ToArray();
                Check(before.Length == beforeCount && before.Select(row => row.GetProperty("correlationId").GetString()).Distinct().Count() == beforeCount,
                    "每次导出必须完整包含调用前接受的事件，且不能重复");
                OrderedPerInstance(rows);
            })).ToArray();
            go.SetResult();
            await Task.WhenAll(producers.Concat(snapshots)).WaitAsync(TimeSpan.FromSeconds(20));
            Check(accepted.Count == writers * perWriter && log.DroppedCount == 0,
                "未达到队列容量的并发写入不应丢失");

            string finalExport = folder.File("complete.log");
            await log.ExportAsync(finalExport).WaitAsync(TimeSpan.FromSeconds(10));
            var finalRows = Records(finalExport);
            string[] actual = finalRows.Where(row => row.GetProperty("event").GetString() == "concurrent")
                .Select(row => row.GetProperty("correlationId").GetString()!).ToArray();
            Check(actual.Length == accepted.Count && actual.Distinct().Count() == accepted.Count &&
                actual.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(accepted.OrderBy(id => id, StringComparer.Ordinal)),
                "最终导出必须包含每一条成功入队的并发事件且恰好一次");
            OrderedPerInstance(finalRows);
            for (int writer = 0; writer < writers; writer++)
            {
                string prefix = "writer-" + writer + "-";
                int[] writerOrder = actual.Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(id => int.Parse(id[prefix.Length..])).ToArray();
                Check(writerOrder.SequenceEqual(Enumerable.Range(0, perWriter)), "每个调用者的事件顺序应保持");
            }
        });

        await test("日志目录IO失败不向同步业务写入抛异常，刷新和状态准确报告故障", async () =>
        {
            using var folder = new Folder();
            string blocked = folder.File(Secret);
            System.IO.File.WriteAllText(blocked, "existing file");
            await using var log = new RuntimeLog(System.IO.Path.Combine(blocked, "logs"));
            for (int i = 0; i < 20; i++) log.Write("regression", "business-continues");
            await Reject<IOException>(() => log.FlushAsync());
            Check(!log.IsAvailable && !string.IsNullOrWhiteSpace(log.LastError), "日志写入失败必须在公开状态可见");
            Check(!log.LastError!.Contains(Secret, StringComparison.Ordinal), "错误状态不得泄漏路径中携带的敏感值");
            Check(log.LastError.Contains("Exception", StringComparison.Ordinal) && log.LastError.Any(char.IsDigit),
                "错误状态应保留异常类型和HRESULT诊断信息");
            log.Write("regression", "business-still-continues");
            await Reject<IOException>(() => log.FlushAsync());

            string destination = folder.File("existing-export.log");
            System.IO.File.WriteAllText(destination, "previous successful export\n");
            await Reject<IOException>(() => log.ExportAsync(destination));
            Check(System.IO.File.ReadAllText(destination) == "previous successful export\n",
                "源日志刷新失败不得替换已有导出文件");
        });

        await test("取消导出保留原目标且不留下临时文件，之后仍可正常导出", async () =>
        {
            using var folder = new Folder();
            await using var log = new RuntimeLog(folder.File("logs"));
            Check(log.Write("regression", "cancel-export"), "事件应接受");
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string destination = folder.File("diagnostics.log");
            byte[] previous = Encoding.UTF8.GetBytes("原来的诊断导出\n");
            System.IO.File.WriteAllBytes(destination, previous);
            string[] before = Directory.GetFiles(folder.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Reject<OperationCanceledException>(() => log.ExportAsync(destination, canceled.Token));
            Check(System.IO.File.ReadAllBytes(destination).SequenceEqual(previous), "取消不得替换已有导出");
            Check(before.SequenceEqual(Directory.GetFiles(folder.Path).OrderBy(path => path, StringComparer.Ordinal)),
                "取消后不得遗留导出临时文件");
            await log.ExportAsync(destination).WaitAsync(TimeSpan.FromSeconds(10));
            Check(Records(destination).Any(row => row.GetProperty("event").GetString() == "cancel-export"),
                "取消一次导出不应损坏后续导出能力");
        });

        await test("导出目标无法替换时保留原目录内容并清理临时文件", async () =>
        {
            using var folder = new Folder();
            await using var log = new RuntimeLog(folder.File("logs"));
            Check(log.Write("regression", "export-failure"), "事件应接受");
            await log.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string destination = folder.File("existing-destination.log");
            Directory.CreateDirectory(destination);
            string sentinel = System.IO.Path.Combine(destination, "keep.txt");
            System.IO.File.WriteAllText(sentinel, "keep");
            string[] before = Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            bool failed = false;
            try { await log.ExportAsync(destination).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (IOException) { failed = true; }
            catch (UnauthorizedAccessException) { failed = true; }
            Check(failed, "文件导出不能覆盖现有目录");
            Check(System.IO.File.ReadAllText(sentinel) == "keep", "导出失败不得破坏原目标内容");
            Check(before.SequenceEqual(Directory.GetFiles(folder.Path, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)), "导出失败必须清理创建的临时文件");
        });
    }
}
