using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace OneSTools.EventLog.Exporter.Core.JsonFile
{
    /// <summary>Append-only UTF-8 JSON Lines output. The last complete event is the checkpoint.</summary>
    internal sealed class JsonLinesFileStorage : IEventLogStorage
    {
        private readonly FileStream _stream;
        private EventLogPosition _position;

        public JsonLinesFileStorage(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Output path must specify a JSON Lines file", nameof(path));

            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Filebeat can read while we append; another exporter must not write this file.
            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                65536, FileOptions.Asynchronous);
            try
            {
                RecoverPosition();
            }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        private void RecoverPosition()
        {
            var end = FindPreviousNewline(_stream.Length) + 1;
            if (end > 0)
            {
                var start = FindPreviousNewline(end - 1) + 1;
                _stream.Position = start;
                using var reader = new StreamReader(_stream, Encoding.UTF8, false, 4096, true);
                var item = JsonConvert.DeserializeObject<EventLogItem>(reader.ReadLine());
                if (item == null || string.IsNullOrEmpty(item.FileName) || item.EndPosition <= 0)
                    throw new InvalidDataException("The last JSON event has no valid source position");
                _position = ToPosition(item);
            }

            // An interrupted write may leave an incomplete final line. Replay it from the source.
            _stream.SetLength(end);
            _stream.Position = end;
        }

        private long FindPreviousNewline(long before)
        {
            var buffer = new byte[4096];
            while (before > 0)
            {
                var start = Math.Max(0, before - buffer.Length);
                _stream.Position = start;
                var length = (int)(before - start);
                var read = 0;
                while (read < length)
                {
                    var count = _stream.Read(buffer, read, length - read);
                    if (count == 0) throw new EndOfStreamException();
                    read += count;
                }
                for (var i = length - 1; i >= 0; i--)
                    if (buffer[i] == (byte)'\n') return start + i;
                before = start;
            }
            return -1;
        }

        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_position);
        }

        public async Task WriteEventLogDataAsync(List<EventLogItem> entities,
            CancellationToken cancellationToken = default)
        {
            if (entities.Count == 0) return;
            foreach (var item in entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(item, Formatting.None));
                await _stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
                _stream.WriteByte((byte)'\n');
            }
            // Persist output before exposing the new reader position.
            _stream.Flush(true);
            _position = ToPosition(entities[entities.Count - 1]);
        }

        private static EventLogPosition ToPosition(EventLogItem item) =>
            new EventLogPosition(item.FileName, item.EndPosition, item.LgfEndPosition, item.Id);

        public void Dispose() => _stream.Dispose();
    }
}
