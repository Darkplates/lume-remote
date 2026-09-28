using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace LumeRemote
{
    public sealed class RemoteFileEntry
    {
        public string Name;
        public bool Directory;
        public long Length;
    }
    public sealed class RemoteFileList
    {
        public string Path;
        public int Page;
        public bool More;
        public readonly List<RemoteFileEntry> Entries = new List<RemoteFileEntry>();
    }

    // Service file operations must use the granting user's token, never SYSTEM.
    public sealed class RemoteFileAccess
    {
        readonly string ownerSid;
        internal readonly string ResumeKey;
        readonly Func<string[]> sharedRoots;
        public RemoteFileAccess(string ownerSid = null, string resumeKey = null, Func<string[]> sharedRoots = null) { this.ownerSid = ownerSid; ResumeKey = resumeKey; this.sharedRoots = sharedRoots; if (resumeKey != null && Security.Unbase64(resumeKey).Length != 32) throw new InvalidDataException("Invalid file resume key."); }
        internal bool HasSharedRoots { get { return sharedRoots != null && sharedRoots().Length > 0; } }
        public T Run<T>(Func<T> action)
        {
            using (WindowsIdentity current = WindowsIdentity.GetCurrent())
            {
                if (!current.IsSystem)
                {
                    if (ownerSid != null && current.User.Value != ownerSid) throw new UnauthorizedAccessException("Sign in as the owner of permanent access to use files.");
                    return action();
                }
            }
            if (ownerSid == null) throw new UnauthorizedAccessException("File access requires a signed-in owner.");
            uint session = WTSGetActiveConsoleSessionId(); IntPtr token;
            if (session == UInt32.MaxValue || session != (uint)Process.GetCurrentProcess().SessionId || !WTSQueryUserToken(session, out token))
                throw new UnauthorizedAccessException("Sign in to Windows on the remote PC to use files.");
            try
            {
                using (WindowsIdentity owner = new WindowsIdentity(token))
                {
                    if (owner.User.Value != ownerSid || owner.IsSystem) throw new UnauthorizedAccessException("The signed-in Windows user does not own this Lume host.");
                    using (WindowsImpersonationContext context = owner.Impersonate()) return action();
                }
            }
            finally { CloseHandle(token); }
        }
        public void Run(Action action) { Run(delegate { action(); return true; }); }
        public static void CheckName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 255 || name == "." || name == ".." || name.TrimEnd(' ', '.') != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || HostService.HasControlChars(name))
                throw new InvalidDataException("Choose a normal Windows file name without paths or special characters.");
            string device = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL" || device == "CLOCK$" || device == "CONIN$" || device == "CONOUT$" ||
                ((device.StartsWith("COM") || device.StartsWith("LPT")) && device.Length == 4 && "123456789\u00b9\u00b2\u00b3".IndexOf(device[3]) >= 0))
                throw new InvalidDataException("Windows reserves that file name.");
        }
        public static string CheckPath(string path)
        {
            if (path == null || path.Length < 3 || path.Length > 240 || !Char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf('/') >= 0)
                throw new InvalidDataException("Choose a local drive or folder. Network and device paths are not supported.");
            foreach (string part in path.Substring(3).Split('\\')) if (part.Length != 0) CheckName(part);
            string full = Path.GetFullPath(path);
            DriveInfo drive = new DriveInfo(full.Substring(0, 3));
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) throw new UnauthorizedAccessException("Choose a local fixed or removable drive.");
            string current = full;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Linked folders and files are not supported by file transfer.");
                current = Path.GetDirectoryName(current.TrimEnd('\\'));
            }
            return full;
        }
        public static string CheckShareRoot(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 240 || !path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf('/') >= 0) throw new InvalidDataException("Use a network folder such as \\\\server\\share\\folder.");
            string[] parts = path.Substring(2).TrimEnd('\\').Split('\\'); if (parts.Length < 2) throw new InvalidDataException("Choose a share, not just a network server.");
            foreach (string part in parts) CheckName(part);
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        public static string CheckRemotePath(string path, bool shares)
        {
            if (shares && path.StartsWith("\\\\", StringComparison.Ordinal)) return CheckShareRoot(path);
            if (path.Length < 3 || path.Length > 240 || !Char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf('/') >= 0) throw new InvalidDataException("Invalid remote folder.");
            foreach (string part in path.Substring(3).Split('\\')) if (part.Length != 0) CheckName(part);
            return path;
        }
        internal string AuthorizedSharePath(string path)
        {
            string full = CheckShareRoot(path); bool allowed = false;
            if (sharedRoots != null) foreach (string entry in sharedRoots())
            { string root = CheckShareRoot(entry); if (String.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) { allowed = true; break; } }
            if (!allowed) throw new UnauthorizedAccessException("The owner has not enabled this network folder in Lume settings.");
            return full;
        }
        internal string ResolvePath(string path)
        {
            if (path == null || !path.StartsWith("\\\\", StringComparison.Ordinal)) return CheckPath(path);
            string full = AuthorizedSharePath(path);
            string current = full, volume = Path.GetPathRoot(full).TrimEnd('\\');
            while (current.Length >= volume.Length)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Linked network folders are not supported.");
                if (String.Equals(current, volume, StringComparison.OrdinalIgnoreCase)) break; current = Path.GetDirectoryName(current.TrimEnd('\\')); if (String.IsNullOrEmpty(current)) break;
            }
            return full;
        }
        public RemoteFileList List(string path, int page, bool includeShares = false)
        {
            return Run(delegate
            {
                if (page < 0 || page > 10000) throw new InvalidDataException("Invalid folder page.");
                RemoteFileList result = new RemoteFileList { Path = path, Page = page };
                if (path.Length == 0)
                {
                    foreach (DriveInfo drive in DriveInfo.GetDrives()) if ((drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable) && drive.IsReady)
                        result.Entries.Add(new RemoteFileEntry { Name = drive.Name, Directory = true });
                    if (includeShares && sharedRoots != null) foreach (string root in sharedRoots()) result.Entries.Add(new RemoteFileEntry { Name = CheckShareRoot(root), Directory = true });
                    return result;
                }
                path = ResolvePath(path); result.Path = path; int skipped = 0;
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                {
                    RemoteFileEntry item;
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry); if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        string name = Path.GetFileName(entry); CheckName(name); if (ResumeJournal.InternalName(name)) continue;
                        item = new RemoteFileEntry { Name = name, Directory = (attributes & FileAttributes.Directory) != 0 };
                        if (!item.Directory) item.Length = new FileInfo(entry).Length;
                    }
                    catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; } catch (ArgumentException) { continue; }
                    if (skipped++ < page * 200) continue;
                    if (result.Entries.Count == 200) { result.More = true; break; }
                    result.Entries.Add(item);
                }
                return result;
            });
        }
        public FileStream OpenRead(string path)
        { return Run(delegate { return new FileStream(ResolvePath(path), FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan); }); }
        public string CreateFolder(string parent, string name, bool unique)
        {
            CheckName(name);
            return Run(delegate
            {
                string folder = ResolvePath(parent); if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("The destination folder no longer exists.");
                for (int i = 0; i < (unique ? 10000 : 1); i++)
                {
                    string candidate = ResolvePath(Path.Combine(folder, i == 0 ? name : name + " (" + i + ")"));
                    if (CreateDirectory(candidate, IntPtr.Zero)) return candidate;
                    int code = Marshal.GetLastWin32Error(); if (code != 183) throw new Win32Exception(code);
                    if (!unique) throw new IOException("A folder or file with that name already exists.");
                }
                throw new IOException("Choose a different destination folder name.");
            });
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateDirectory(string name, IntPtr security);
        [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSQueryUserToken(uint session, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }

    // A received file becomes visible under its final name only after SHA-256 verification.
    internal sealed class IncomingFile : IDisposable
    {
        readonly RemoteFileAccess access;
        readonly string temporary, folder, name;
        readonly byte[] expectedDigest;
        string journalPath;
        FileStream journal;
        bool keepPartial;
        FileStream stream;
        System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create();
        public readonly long Length;
        public long Position { get; private set; }
        public byte[] PrefixHash { get; private set; }
        public bool Resumable { get { return expectedDigest != null; } }
        public IncomingFile(RemoteFileAccess access, string folder, string name, long length, byte[] expectedDigest = null, Action progress = null)
        {
            if (length < 0) throw new InvalidDataException("Invalid file size."); RemoteFileAccess.CheckName(name);
            this.access = access; this.folder = folder; this.name = name; Length = length; this.expectedDigest = expectedDigest;
            try { temporary = access.Run(delegate
            {
                string directory = access.ResolvePath(folder);
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The destination folder no longer exists.");
                byte[] header = expectedDigest == null ? null : ResumeJournal.Header(access.ResumeKey, directory, name, length, expectedDigest);
                string part = Path.Combine(directory, header == null ? ".lume-" + Guid.NewGuid().ToString("N") + ".part" : ResumeJournal.Name(header));
                access.ResolvePath(part);
                if (header != null)
                {
                    journalPath = Path.ChangeExtension(part, ".state"); access.ResolvePath(journalPath);
                    bool exists = File.Exists(journalPath);
                    if (!exists && File.Exists(part)) throw new InvalidDataException("The partial transfer has no authenticated journal. Remove it before retrying.");
                    journal = new FileStream(journalPath, exists ? FileMode.Open : FileMode.CreateNew, System.IO.FileAccess.ReadWrite, FileShare.None);
                    if (exists) { if (journal.Length != ResumeJournal.HeaderLength || !ResumeJournal.Equal(Wire.ReadExact(journal, ResumeJournal.HeaderLength), header)) throw new InvalidDataException("The saved transfer journal is invalid. Remove its partial files before retrying."); }
                    else { journal.Write(header, 0, header.Length); journal.Flush(true); }
                }
                if (header != null && File.Exists(part))
                {
                    stream = new FileStream(part, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.SequentialScan);
                    if (stream.Length > length) throw new InvalidDataException("The saved transfer exceeds the source length.");
                    Position = stream.Length;
                    using (var prefix = System.Security.Cryptography.SHA256.Create())
                    {
                        byte[] bytes = new byte[65536]; long remaining = Position;
                        while (remaining > 0) { if (progress != null) progress(); int count = stream.Read(bytes, 0, (int)Math.Min(bytes.Length, remaining)); if (count == 0) throw new EndOfStreamException(); hash.TransformBlock(bytes, 0, count, null, 0); prefix.TransformBlock(bytes, 0, count, null, 0); remaining -= count; }
                        prefix.TransformFinalBlock(new byte[0], 0, 0); PrefixHash = prefix.Hash;
                    }
                }
                else
                {
                    stream = new FileStream(part, FileMode.CreateNew, System.IO.FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.SequentialScan);
                    using (var prefix = System.Security.Cryptography.SHA256.Create()) PrefixHash = prefix.ComputeHash(new byte[0]);
                }
                return part;
            }); }
            catch { if (stream != null) stream.Dispose(); if (journal != null) journal.Dispose(); hash.Dispose(); throw; }
        }
        public void Append(long offset, byte[] bytes)
        {
            if (stream == null || offset != Position || bytes.Length == 0 || bytes.Length > 65536 || bytes.Length > Length - Position) throw new InvalidDataException("Invalid file chunk or offset.");
            access.Run(delegate { stream.Write(bytes, 0, bytes.Length); }); hash.TransformBlock(bytes, 0, bytes.Length, null, 0); Position += bytes.Length;
        }
        public string Complete(byte[] expected)
        {
            if (Position != Length || expected.Length != 32) throw new InvalidDataException("Incomplete file.");
            hash.TransformFinalBlock(new byte[0], 0, 0);
            if (!ResumeJournal.Equal(expected, hash.Hash) || (expectedDigest != null && !ResumeJournal.Equal(expected, expectedDigest))) throw new InvalidDataException("File verification failed. Nothing was saved under the final name.");
            return access.Run(delegate
            {
                stream.Flush(true); stream.Dispose(); stream = null; access.ResolvePath(folder);
                for (int i = 0; i < 10000; i++)
                {
                    string candidate = Path.Combine(folder, i == 0 ? name : Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
                    access.ResolvePath(candidate);
                    try { File.Move(temporary, candidate); return candidate; }
                    catch (IOException) { if (!File.Exists(candidate) && !Directory.Exists(candidate)) throw; }
                }
                throw new IOException("Choose a different destination file name.");
            });
        }
        public void Suspend()
        { if (Resumable && Position > 0 && stream != null) { keepPartial = true; try { access.Run(delegate { stream.Flush(true); }); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        public void Dispose()
        {
            if (stream != null) { stream.Dispose(); stream = null; }
            if (journal != null) { journal.Dispose(); journal = null; }
            if (hash != null) { hash.Dispose(); hash = null; }
            if (!keepPartial) try { access.Run(delegate { if (File.Exists(temporary)) File.Delete(temporary); if (journalPath != null && File.Exists(journalPath)) File.Delete(journalPath); }); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
