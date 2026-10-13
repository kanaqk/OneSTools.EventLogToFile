using System.Text;
using System.Text.Json;
using NodaTime;
using OneSTools.EventLog;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.Core.JsonFile;

var root = Path.Combine(Path.GetTempPath(), "eventlog-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "output", "events.log");
    var first = new EventLogItem
    {
        Id = 1, FileName = "20261013000000.lgp", EndPosition = 100, LgfEndPosition = 50,
        DateTime = new DateTime(2026, 10, 13, 7, 15, 0, DateTimeKind.Utc),
        User = "Иван", Comment = "Первая строка\nВторая строка\r\n\"Кавычки\" \\ путь",
        Data = new string('я', 6000)
    };
    using (var storage = new JsonFileStorage(path))
    {
        Check(await storage.ReadEventLogPositionAsync() == null, "empty output has no checkpoint");
        await storage.WriteEventLogDataAsync(new List<EventLogItem> { first });
        using var filebeatReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Check(filebeatReader.Length > 0, "output is flushed and readable while exporter runs");
        Check(ReadLines(path).Length == 1, "multiline comments stay on one JSON line");
        var bytes = ReadBytes(path);
        Check(bytes[0] == '{' && bytes[^1] == '\n', "UTF-8 without BOM with final newline");
        var roundtrip = JsonSerializer.Deserialize<EventLogItem>(ReadLines(path)[0]);
        Check(roundtrip.Comment == first.Comment && roundtrip.Data == first.Data && roundtrip.User == first.User,
            "Cyrillic, quotes, backslashes and newlines roundtrip");
        Check(roundtrip.DateTime == first.DateTime && roundtrip.DateTime.Kind == DateTimeKind.Utc,
            "UTC event timestamp survives JSON conversion");
    }
    File.AppendAllText(path, "{\"Id\":2,\"Comment\":\"unfinished", new UTF8Encoding(false));
    using (var storage = new JsonFileStorage(path))
    {
        var position = await storage.ReadEventLogPositionAsync();
        Check(position.Id == 1 && position.EndPosition == 100 && position.LgfEndPosition == 50,
            "restart recovers last complete event across buffer boundaries");
        Check(ReadLines(path).Length == 1 && ReadBytes(path)[^1] == '\n',
            "restart removes incomplete tail");
        await storage.WriteEventLogDataAsync(new List<EventLogItem>
        {
            new EventLogItem { Id = 2, FileName = first.FileName, EndPosition = 200, LgfEndPosition = 60 }
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectThrows<OperationCanceledException>(async () =>
            await storage.WriteEventLogDataAsync(new List<EventLogItem> { first }, cancellation.Token));
    }
    using (var storage = new JsonFileStorage(path))
        Check((await storage.ReadEventLogPositionAsync()).Id == 2 && ReadLines(path).Length == 2,
            "append preserves prior events and cancellation adds none");

    var broken = Path.Combine(root, "broken.jsonl");
    File.WriteAllText(broken, "invalid complete JSON\n");
    await ExpectThrows<Newtonsoft.Json.JsonReaderException>(() => { using var storage = new JsonFileStorage(broken); return Task.CompletedTask; });
    Check(File.ReadAllText(broken) == "invalid complete JSON\n", "complete corrupt output is not silently discarded");

    var source = Path.Combine(root, "1Cv8Log");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "1Cv8.lgf"), "{4,\"_$Session$_.Start\",1}\n", new UTF8Encoding(false));
    var lgp = Path.Combine(source, "20261013000000.lgp");
    const string record = "{20261013121500,N,{0,0},0,0,0,1,1,I,\"Тест\",0,{U},\"\",0,0,0,1}\n";
    File.WriteAllText(lgp, record + record, new UTF8Encoding(false));
    var archivePath = Path.Combine(root, "archive.jsonl");
    var settings = new EventLogExporterSettings
    {
        LogFolder = source, LoadArchive = true, Portion = 100,
        TimeZone = DateTimeZoneProviders.Tzdb["Asia/Yekaterinburg"]
    };
    using (var storage = new JsonFileStorage(archivePath))
    using (var exporter = new EventLogExporter(settings, storage))
        await exporter.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(ReadLines(archivePath).Length == 2, "archive completion drains an incomplete batch");
    var archived = JsonSerializer.Deserialize<EventLogItem>(ReadLines(archivePath)[0]);
    Check(archived.Comment == "Тест" && archived.Event == "Сеанс.Начало" && archived.DateTime == first.DateTime,
        "LGF/LGP conversion resolves event names and converts timezone to UTC");

    // Continue a live reader from the archive output, add one new source event and stop before timeout flush.
    File.AppendAllText(lgp, record, new UTF8Encoding(false));
    settings.LoadArchive = false;
    settings.ReadingTimeout = 30;
    using (var storage = new JsonFileStorage(archivePath))
    using (var exporter = new EventLogExporter(settings, storage))
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        await Task.Run(() => exporter.StartAsync(cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(10));
    var lines = ReadLines(archivePath);
    Check(lines.Length == 3 && JsonSerializer.Deserialize<EventLogItem>(lines[2]).Id == 3,
        "live restart skips exported events and shutdown drains the final batch");

    settings.WritingMaxDop = 2;
    using (var storage = new JsonFileStorage(Path.Combine(root, "parallel.jsonl")))
        await ExpectThrows<Exception>(() => { using var exporter = new EventLogExporter(settings, storage); return Task.CompletedTask; });

    settings.WritingMaxDop = 1;
    settings.LoadArchive = true;
    settings.Portion = 1;
    settings.CollectedFactor = 1;
    File.WriteAllText(lgp, string.Concat(Enumerable.Repeat(record, 100)), new UTF8Encoding(false));
    using (var storage = new FailingStorage())
    using (var exporter = new EventLogExporter(settings, storage))
        await ExpectThrows<IOException>(() => exporter.StartAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    Console.WriteLine("PASS: storage failure propagates without pipeline deadlock");

    var emptySource = Path.Combine(root, "empty-source");
    Directory.CreateDirectory(emptySource);
    File.WriteAllText(Path.Combine(emptySource, "1Cv8.lgf"), "");
    settings.LogFolder = emptySource;
    var emptyOutput = Path.Combine(root, "empty.jsonl");
    using (var storage = new JsonFileStorage(emptyOutput))
    using (var exporter = new EventLogExporter(settings, storage))
        await exporter.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(new FileInfo(emptyOutput).Length == 0, "empty archive completes without errors");
    Console.WriteLine("All exporter smoke tests passed.");
}
finally
{
    Directory.Delete(root, true);
}

// Windows readers must allow the exporter's existing write handle, just as Filebeat does.
static byte[] ReadBytes(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var output = new MemoryStream();
    stream.CopyTo(output);
    return output.ToArray();
}

static string[] ReadLines(string path) =>
    Encoding.UTF8.GetString(ReadBytes(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

static async Task ExpectThrows<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

sealed class FailingStorage : IEventLogStorage
{
    public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<EventLogPosition>(null);
    public Task WriteEventLogDataAsync(List<EventLogItem> entities, CancellationToken cancellationToken = default) =>
        Task.FromException(new IOException("Simulated write failure"));
    public void Dispose() { }
}
