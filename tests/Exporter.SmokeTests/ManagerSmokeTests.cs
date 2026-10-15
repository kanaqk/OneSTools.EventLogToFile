using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneSTools.EventLog.Exporter.Manager;

static class ManagerSmokeTests
{
    public static async Task RunAsync(string root, string record)
    {
        var clusterA = Path.Combine(root, "cluster-a");
        var clusterB = Path.Combine(root, "cluster-b");
        CreateSource(clusterA, "base-1", record);
        CreateSource(clusterA, "base-2", record);
        CreateSource(clusterB, "base-1", record);
        File.WriteAllText(Path.Combine(clusterA, "1CV8Clst.lst"),
            "{0,0,{2,{\"base-1\",0,0,0,0,\"Бухгалтерия\"},{\"base-2\",0,0,0,0,\"Зарплата\"}}}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(clusterB, "1CV8Clst.lst"),
            "{0,0,{1,{\"base-1\",0,0,0,0,\"Бухгалтерия\"}}}", new UTF8Encoding(false));
        var output = Path.Combine(root, "manager-output");
        var settings = new Dictionary<string, string>
        {
            ["Exporter:StorageType"] = "JsonFile", ["Exporter:Portion"] = "100",
            ["Exporter:ReadingTimeout"] = "1", ["Exporter:TimeZone"] = "Asia/Yekaterinburg",
            ["JsonFile:Folder"] = output,
            ["Manager:ClstFolders:0:Folder"] = clusterA,
            ["Manager:ClstFolders:0:Templates:0:Mask"] = ".*",
            ["Manager:ClstFolders:0:Templates:0:Template"] = "prefix_[IBNAME]",
            ["Manager:ClstFolders:1:Folder"] = clusterB,
            ["Manager:ClstFolders:1:Templates:0:Mask"] = ".*",
            ["Manager:ClstFolders:1:Templates:0:Template"] = "prefix_[IBNAME]"
        };
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var messages = new MessageLogger();
        using (var manager = new ExportersManager(messages, services, configuration))
        {
            await manager.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => Directory.Exists(output) &&
                    Directory.GetFiles(output, ".position.json", SearchOption.AllDirectories).Length == 3);
                var registry = Path.Combine(clusterA, "1CV8Clst.lst");
                File.WriteAllText(registry, "", new UTF8Encoding(false));
                await WaitUntilAsync(() => messages.Messages.Any(message => message.Contains("Failed to read cluster registry")));
                File.WriteAllText(registry + ".new",
                    "{0,0,{2,{\"base-1\",0,0,0,0,\"НовоеИмя\"},{\"base-2\",0,0,0,0,\"Зарплата\"}}}", new UTF8Encoding(false));
                File.Move(registry + ".new", registry, true);
                File.SetLastWriteTimeUtc(registry, DateTime.UtcNow.AddSeconds(1));
                await WaitUntilAsync(() => messages.Messages.Any(message => message.Contains("\"НовоеИмя\" information base")));
                File.AppendAllText(Path.Combine(clusterA, "base-1", "1Cv8Log", "20261013000000.lgp"), record, new UTF8Encoding(false));
                var renamedCheckpoint = Path.Combine(OutputFolder(output, clusterA, "base-1"), ".position.json");
                await WaitUntilAsync(() =>
                {
                    using var stream = new FileStream(renamedCheckpoint, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream);
                    return document.RootElement.GetProperty("Id").GetInt64() == 3;
                });
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await manager.StopAsync(stop.Token);
            }
        }
        var expected = new[] { (clusterA, "base-1", "Бухгалтерия"),
            (clusterA, "base-2", "Зарплата"), (clusterB, "base-1", "Бухгалтерия") };
        foreach (var (cluster, id, name) in expected)
        {
            var files = Directory.GetFiles(OutputFolder(output, cluster, id), "*.jsonl");
            if (files.Length != 1) throw new Exception("Manager must keep each base in its own folder");
            var lines = File.ReadAllLines(files[0]);
            var renamed = cluster == clusterA && id == "base-1";
            if (lines.Length != (renamed ? 3 : 2)) throw new Exception("Manager did not export all events");
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var expectedName = renamed && document.RootElement.GetProperty("Id").GetInt64() == 3 ? "НовоеИмя" : name;
                if (document.RootElement.GetProperty("IBName").GetString() != expectedName)
                    throw new Exception("IBName must use the actual registered name, not the storage template");
            }
        }
        Console.WriteLine("PASS: manager reads all bases in multiple clusters and enriches every event with IBName");
        Console.WriteLine("PASS: identical base names in different clusters have independent output folders");
        Console.WriteLine("PASS: a renamed base resumes its reader and enriches new events with the new IBName");
        Console.WriteLine("PASS: temporary incomplete cluster registry preserves active exporters and is retried");
    }

    private static string OutputFolder(string output, string cluster, string id)
    {
        var path = Path.GetFullPath(Path.Combine(cluster, id));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        return Path.Combine(output, hash);
    }

    private static void CreateSource(string cluster, string id, string record)
    {
        var source = Path.Combine(cluster, id, "1Cv8Log");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "1Cv8.lgf"), "{4,\"_$Session$_.Start\",1}\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(source, "20261013000000.lgp"), record + record, new UTF8Encoding(false));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Manager did not export all configured bases");
            await Task.Delay(100);
        }
    }

    private sealed class MessageLogger : ILogger<ExportersManager>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
