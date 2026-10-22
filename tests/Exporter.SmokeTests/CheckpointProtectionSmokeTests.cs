using System.Diagnostics;
using System.Reflection;
using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using OneSTools.EventLog;
using OneSTools.EventLog.Exporter.Core.JsonFile;

static class CheckpointProtectionSmokeTests
{
    public static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var unsupported = new JsonFileStorage(Path.Combine(root, "unsupported-acl"), null, true);
                throw new Exception("NTFS protection must fail explicitly outside Windows");
            }
            catch (PlatformNotSupportedException) { }
            Console.WriteLine("PASS: NTFS protection rejects unsupported platforms; Windows ACL tests skipped");
            return;
        }

        var folder = Path.Combine(root, "protected-output");
        var early = Path.Combine(folder, "20261013000000.jsonl");
        var late = Path.Combine(folder, "20261014000000.jsonl");
        try
        {
            using (var storage = new JsonFileStorage(folder, "Accounting", true))
                await Write(storage, "20261013000000.lgp", 1);
            await MustBeProtected(early); // No writer remains: this must be the ACL, not file sharing.
            using (var reader = File.OpenRead(early))
                if (reader.ReadByte() != '{') throw new Exception("Protected file must stay readable");
            Console.WriteLine("PASS: same-account external process cannot delete checkpoint JSONL after shutdown; reading works");

            using (var storage = new JsonFileStorage(folder, "Accounting", true))
            {
                await Write(storage, "20261013000000.lgp", 2);
                Directory.CreateDirectory(Path.Combine(folder, ".position.json.tmp"));
                try
                {
                    await Write(storage, "20261014000000.lgp", 3);
                    throw new Exception("Expected checkpoint failure");
                }
                catch (UnauthorizedAccessException) { }
                if ((await storage.ReadEventLogPositionAsync()).Id != 2)
                    throw new Exception("Failed handover advanced checkpoint");
            }
            await MustBeProtected(early);
            await MustBeProtected(late);
            Directory.Delete(Path.Combine(folder, ".position.json.tmp"));
            Console.WriteLine("PASS: failed checkpoint handover keeps both JSONL files protected");

            using (var storage = new JsonFileStorage(folder, "Accounting", true))
            {
                if ((await storage.ReadEventLogPositionAsync()).Id != 2)
                    throw new Exception("Restart must retain old checkpoint");
                await MustDelete(late); // Startup recovers a protection left by the failed handover.
                await MustBeProtected(early);
                await Write(storage, "20261014000000.lgp", 3);
                if ((await storage.ReadEventLogPositionAsync()).Id != 3)
                    throw new Exception("Successful handover did not commit checkpoint");
                await MustDelete(early);
            }
            await MustBeProtected(late);
            using (var storage = new JsonFileStorage(folder, "Accounting", true))
            {
                if ((await storage.ReadEventLogPositionAsync()).Id != 3)
                    throw new Exception("Deleted old JSONL must not break resume");
                await Write(storage, "20261014000000.lgp", 4);
            }
            await MustBeProtected(late);
            Console.WriteLine("PASS: committed handover releases old JSONL, retains new protection, and resumes after old-file deletion");
            var beat = Environment.GetEnvironmentVariable("FILEBEAT_TEST_EXE");
            if (!string.IsNullOrWhiteSpace(beat)) await TestFilebeat(beat, folder, late);
            else Console.WriteLine("SKIP: real Filebeat integration (set FILEBEAT_TEST_EXE to enable)");
        }
        finally
        {
            // Only remove ACL entries installed in this isolated test directory.
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            foreach (var path in Directory.GetFiles(folder, "*.jsonl"))
            {
                var file = new FileInfo(path);
                var acl = file.GetAccessControl();
                acl.RemoveAccessRuleSpecific(new FileSystemAccessRule(everyone, FileSystemRights.Delete, AccessControlType.Deny));
                file.SetAccessControl(acl);
            }
            var directory = new DirectoryInfo(folder);
            var directoryAcl = directory.GetAccessControl();
            directoryAcl.RemoveAccessRuleSpecific(new FileSystemAccessRule(everyone,
                FileSystemRights.DeleteSubdirectoriesAndFiles, InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(directoryAcl);
        }
    }

    private static async Task TestFilebeat(string executable, string folder, string last)
    {
        var config = Path.Combine(folder, "filebeat-test.yml");
        var beatOutput = Path.Combine(folder, "beat-events").Replace('\\', '/');
        File.WriteAllText(config, $"""
filebeat.inputs:
  - type: filestream
    id: checkpoint-protection-test
    paths: ['{folder.Replace('\\', '/')}/*.jsonl']
    file_identity.path: ~
    prospector.scanner.fingerprint.enabled: false
    prospector.scanner.check_interval: 1s
    close.reader.on_eof: true
    delete.enabled: true
    delete.grace_period: 1s
output.file:
  path: '{beatOutput}'
  filename: events
logging.level: error
logging.to_files: false
logging.to_stderr: true
""");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = folder
        };
        foreach (var argument in new[] { "-e", "-c", config, "--path.data", Path.Combine(folder, "beat-data"),
                     "--path.logs", Path.Combine(folder, "beat-logs"), "--strict.perms=false" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var messages = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) messages.Enqueue(e.Data); };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) messages.Enqueue(e.Data); };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        async Task WaitUntil(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!ready())
            {
                if (process.HasExited || DateTime.UtcNow > deadline)
                    throw new Exception("Filebeat integration failed: " + string.Join(Environment.NewLine, messages));
                await Task.Delay(200);
            }
        }
        try
        {
            // An actual failed delete proves the file was ingested, acknowledged, and considered for removal.
            await WaitUntil(() => messages.Any(line => line.Contains("could not remove")));
            if (!File.Exists(last)) throw new Exception("Filebeat removed the protected checkpoint file");
            var next = Path.Combine(folder, "20261015000000.jsonl");
            using (var storage = new JsonFileStorage(folder, "Accounting", true))
                await Write(storage, "20261015000000.lgp", 5);
            await WaitUntil(() => !File.Exists(last));
            await MustBeProtected(next);
            Console.WriteLine("PASS: real Windows Filebeat ingests protected JSONL and deletes it after Manager releases protection");
            Console.WriteLine("PASS: Filebeat remains the deleter; newest checkpoint JSONL survives manager shutdown under the same account");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
        }
    }

    private static Task Write(JsonFileStorage storage, string source, long id) =>
        storage.WriteEventLogDataAsync(new List<EventLogItem>
        {
            new EventLogItem { Id = id, FileName = source, EndPosition = id * 100, LgfEndPosition = 50 }
        });

    private static async Task<int> DeleteFromOtherProcess(string path)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--try-delete");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return process.ExitCode;
    }

    private static async Task MustBeProtected(string path)
    {
        if (await DeleteFromOtherProcess(path) != 2 || !File.Exists(path))
            throw new Exception("Expected ACL delete denial after writer shutdown: " + path);
    }

    private static async Task MustDelete(string path)
    {
        if (await DeleteFromOtherProcess(path) != 0 || File.Exists(path))
            throw new Exception("Old JSONL must be deletable under the same account: " + path);
    }
}
