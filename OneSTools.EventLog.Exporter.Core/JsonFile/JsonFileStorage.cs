using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace OneSTools.EventLog.Exporter.Core.JsonFile
{
    /// <summary>One JSON Lines file per source LGP, with an atomic directory checkpoint.</summary>
    public sealed class JsonFileStorage : IEventLogStorage
    {
        private readonly string _folder;
        private readonly string _checkpointPath;
        private readonly FileStream _directoryLock;
        private JsonLinesFileStorage _current;
        private string _currentFileName;
        private EventLogPosition _position;

        public JsonFileStorage(IConfiguration configuration)
            : this(configuration.GetValue("JsonFile:Folder", ""))
        {
        }

        public JsonFileStorage(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("JsonFile:Folder must specify an output directory; JsonFile:Path is no longer used", nameof(folder));

            _folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(_folder);
            _checkpointPath = Path.Combine(_folder, ".position.json");
            _directoryLock = new FileStream(Path.Combine(_folder, ".export.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                if (File.Exists(_checkpointPath))
                {
                    _position = JsonConvert.DeserializeObject<EventLogPosition>(File.ReadAllText(_checkpointPath));
                    if (_position == null || _position.EndPosition <= 0 || _position.Id <= 0)
                        throw new InvalidDataException("The output directory checkpoint is invalid");
                    OpenOutput(_position.FileName, mustExist: true);
                    var tail = _current.ReadEventLogPositionAsync().GetAwaiter().GetResult();
                    if (tail == null || tail.FileName != _position.FileName || tail.EndPosition < _position.EndPosition)
                        throw new InvalidDataException("The checkpoint refers to missing output events");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void OpenOutput(string sourceFileName, bool mustExist = false)
        {
            if (string.IsNullOrWhiteSpace(sourceFileName) || sourceFileName.Contains("/") ||
                sourceFileName.Contains("\\") || Path.GetFileName(sourceFileName) != sourceFileName ||
                !string.Equals(Path.GetExtension(sourceFileName), ".lgp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Event source must be an LGP file name without a directory");

            if (_current != null && _currentFileName == sourceFileName) return;
            var outputPath = Path.Combine(_folder, Path.ChangeExtension(sourceFileName, ".jsonl"));
            if (mustExist && !File.Exists(outputPath))
                throw new InvalidDataException("The checkpoint output file is missing: " + outputPath);
            _current?.Dispose();
            _current = null;
            _current = new JsonLinesFileStorage(outputPath);
            _currentFileName = sourceFileName;
        }

        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_position);
        }

        public async Task WriteEventLogDataAsync(List<EventLogItem> entities,
            CancellationToken cancellationToken = default)
        {
            var start = 0;
            while (start < entities.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = start + 1;
                while (end < entities.Count && entities[end].FileName == entities[start].FileName) end++;
                OpenOutput(entities[start].FileName);
                await _current.WriteEventLogDataAsync(entities.GetRange(start, end - start), cancellationToken);
                var position = await _current.ReadEventLogPositionAsync(cancellationToken);
                await SavePositionAsync(position, cancellationToken);
                _position = position;
                start = end;
            }
        }

        private async Task SavePositionAsync(EventLogPosition position, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(position));
            var temporaryPath = _checkpointPath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                stream.Flush(true);
            }
            // The data file has already been flushed. Replace the checkpoint only after that.
            if (File.Exists(_checkpointPath))
                File.Replace(temporaryPath, _checkpointPath, null);
            else
                File.Move(temporaryPath, _checkpointPath);
        }

        public void Dispose()
        {
            _current?.Dispose();
            _directoryLock?.Dispose();
        }
    }
}
