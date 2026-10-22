using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Newtonsoft.Json;

namespace OneSTools.EventLog.Exporter.Core.JsonFile
{
    // ACLs persist after the process exits. The journal lets startup finish an interrupted handover.
    internal sealed class CheckpointFileProtection
    {
        private readonly string _folder;
        private readonly string _journal;
        private readonly HashSet<string> _protected;
        private static SecurityIdentifier Everyone => new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        private static FileSystemAccessRule FileRule =>
            new FileSystemAccessRule(Everyone, FileSystemRights.Delete, AccessControlType.Deny);

        public CheckpointFileProtection(string folder)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("JsonFile:ProtectCheckpointFile requires Windows and NTFS ACLs");
            _folder = folder;
            _journal = Path.Combine(folder, ".jsonl-protection.json");
            _protected = new HashSet<string>(File.Exists(_journal)
                ? JsonConvert.DeserializeObject<string[]>(File.ReadAllText(_journal))
                    ?? throw new InvalidDataException("Invalid JSONL protection journal")
                : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var name in _protected)
                if (string.IsNullOrEmpty(name) || name.Contains("/") || name.Contains("\\") ||
                    Path.GetFileName(name) != name || !string.Equals(Path.GetExtension(name), ".jsonl", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid file name in JSONL protection journal");

            // DELETE_CHILD on the parent otherwise bypasses the file's DELETE denial.
            // This ACE applies to this directory only, not to its children.
            var directory = new DirectoryInfo(folder);
            var acl = directory.GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(Everyone,
                FileSystemRights.DeleteSubdirectoriesAndFiles, InheritanceFlags.None,
                PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(acl);
        }

        public void Protect(string path)
        {
            var file = new FileInfo(path);
            var acl = file.GetAccessControl(AccessControlSections.Access);
            var name = file.Name;
            if (!_protected.Contains(name))
            {
                // Do not claim ownership of a pre-existing administrator's identical rule.
                if (acl.GetAccessRules(true, false, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Equals(Everyone) &&
                        rule.AccessControlType == AccessControlType.Deny &&
                        (rule.FileSystemRights & FileSystemRights.Delete) != 0))
                    throw new InvalidOperationException("An existing delete denial must be resolved before protecting " + path);
                _protected.Add(name);
                SaveJournal(); // Record intent before changing the ACL.
            }
            acl.AddAccessRule(FileRule);
            file.SetAccessControl(acl);
        }

        public void ReleaseExcept(string currentPath)
        {
            var current = Path.GetFileName(currentPath);
            foreach (var name in _protected.Where(name => !string.Equals(name, current, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var file = new FileInfo(Path.Combine(_folder, name));
                if (file.Exists)
                {
                    var acl = file.GetAccessControl(AccessControlSections.Access);
                    acl.RemoveAccessRuleSpecific(FileRule);
                    file.SetAccessControl(acl);
                }
                _protected.Remove(name);
                SaveJournal();
            }
        }

        private void SaveJournal()
        {
            var temporary = _journal + ".tmp";
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_protected));
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(_journal)) File.Replace(temporary, _journal, null);
            else File.Move(temporary, _journal);
        }
    }
}
