using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog.Exporter.Manager
{
    public class ClstWatcher : IDisposable
    {
        public delegate void InfoBaseAddedHandler(object sender, ClstEventArgs args);

        public delegate void InfoBaseDeletedHandler(object sender, ClstEventArgs args);

        private readonly string _folder;
        private readonly string _path;
        private readonly List<TemplateItem> _templates;
        private FileSystemWatcher _clstWatcher;
        private Dictionary<string, (string, string)> _infoBases;
        private readonly object _syncRoot = new object();
        private readonly Timer _reloadTimer;
        private bool _disposed;

        public ClstWatcher(string folder, List<TemplateItem> templates)
        {
            _folder = folder;
            _templates = templates;
            _path = Path.Combine(_folder, "1CV8Clst.lst");
            if (!File.Exists(_path))
                throw new Exception("Couldn't find LST \"1CV8Clst.lst\" file");

            _infoBases = ReadInfoBases();
            _reloadTimer = new Timer(_ => ReloadInfoBases(), null, Timeout.Infinite, Timeout.Infinite);
            InitializeWatcher();
        }

        public ReadOnlyDictionary<string, (string Name, string DataBaseName)> InfoBases => new(_infoBases);

        public void Dispose()
        {
            lock (_syncRoot)
            {
                _disposed = true;
                _reloadTimer?.Dispose();
                _clstWatcher?.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        public event InfoBaseAddedHandler InfoBasesAdded;
        public event InfoBaseDeletedHandler InfoBasesDeleted;
        public event Action<Exception> ReadFailed;

        private Dictionary<string, (string Name, string DataBaseName)> ReadInfoBases()
        {
            var items = new Dictionary<string, (string, string)>();

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var fileData = reader.ReadToEnd();
            var parsedData = BracketsParser.ParseBlock(fileData);

            var infoBasesNode = parsedData[2];
            int count = infoBasesNode[0];

            if (count > 0)
                for (var i = 1; i <= count; i++)
                {
                    var infoBaseNode = infoBasesNode[i];

                    var elPath = Path.Combine(_folder, infoBaseNode[0]);
                    string name = infoBaseNode[5];

                    foreach (var template in _templates)
                        if (Regex.IsMatch(name, template.Mask))
                        {
                            var dataBaseName = template.Template.Replace("[IBNAME]", name);
                            items.Add(elPath, (name, dataBaseName));

                            break;
                        }
                }

            return items;
        }

        private void ReadInfoBasesAndRaiseEvents()
        {
            var newInfoBases = ReadInfoBases();

            var deleted = _infoBases.Except(newInfoBases);
            foreach (var (key, (item1, item2)) in deleted)
                InfoBasesDeleted?.Invoke(this, new ClstEventArgs(key, item1, item2));

            var added = newInfoBases.Except(_infoBases);
            foreach (var (key, (item1, item2)) in added)
                InfoBasesAdded?.Invoke(this, new ClstEventArgs(key, item1, item2));

            _infoBases = newInfoBases;
        }

        private void InitializeWatcher()
        {
            _clstWatcher = new FileSystemWatcher(_folder, "1CV8Clst.lst")
            {
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _clstWatcher.Changed += ClstWatcher_Changed;
            _clstWatcher.Created += ClstWatcher_Changed;
            _clstWatcher.Renamed += ClstWatcher_Changed;
            _clstWatcher.EnableRaisingEvents = true;
        }

        private void ClstWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            lock (_syncRoot)
            {
                if (!_disposed) _reloadTimer.Change(200, Timeout.Infinite);
            }
        }

        private void ReloadInfoBases()
        {
            lock (_syncRoot)
            {
                if (_disposed) return;
                try
                {
                    ReadInfoBasesAndRaiseEvents();
                }
                catch (Exception ex)
                {
                    // 1C can still be rewriting the registry. Keep the last valid list and retry.
                    ReadFailed?.Invoke(ex);
                    _reloadTimer.Change(500, Timeout.Infinite);
                }
            }
        }
    }
}
