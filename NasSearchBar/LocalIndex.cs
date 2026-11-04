using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NasSearchBar;

// Searches this PC's own drives, next to the NAS when one is set, with nas_search.py's query syntax.
// An NTFS drive is read from its master file table and then followed through its change journal, as Everything
// does: the whole drive in seconds, then every create, delete and rename as it happens. That needs admin rights;
// without them, or on FAT/exFAT, the drive is walked folder by folder and walked again on a timer.
// Hits come back as \\localhost\C$\folder, which Hit already opens as C:\folder.
public sealed class LocalIndex
{
    const int Limit = 200;

    // One drive (or folder) root. Slot 0 is the root itself; a slot whose name is null is free.
    // Guarded by lock(this): the journal thread changes it, searches read it.
    sealed class Volume(string root)
    {
        public readonly List<string?> Names = [null], Lows = [null];
        public readonly List<int> Parent = [-1];
        public readonly List<bool> IsDir = [true];
        readonly Dictionary<ulong, int> slot = [];  // journal only: NTFS file id -> its (first) slot
        readonly Dictionary<ulong, List<int>> links = [];  // file id -> all its slots, for files with hard links
        readonly Stack<int> free = new();
        readonly string rootPre = root.TrimEnd('\\').Replace('\\', '/') + "/";  // "C:/"
        string?[] pre = [], preLow = [];
        bool dirsMoved = true;  // a folder renamed or moved: every path under it changed, rebuild all prefixes
        readonly List<int> newDirs = [];  // folders added since the last Prefixes(): only these need one
        public int Live;  // slots in use

        public int Add(string name, int parent, bool dir, ulong id = 0)
        {
            var low = name.ToLowerInvariant();  // the same string when already lowercase: no copy
            if (free.TryPop(out var s))
            {
                (Names[s], Lows[s], Parent[s], IsDir[s]) = (name, low, parent, dir);
            }
            else
            {
                s = Names.Count;
                Names.Add(name); Lows.Add(low); Parent.Add(parent); IsDir.Add(dir);
            }
            if (id != 0 && !slot.TryAdd(id, s))
            {
                if (!links.TryGetValue(id, out var all)) links[id] = all = [slot[id]];
                all.Add(s);
            }
            if (s < pre.Length) pre[s] = preLow[s] = null;  // a reused slot: drop the old folder's prefix
            if (dir) newDirs.Add(s);
            Live++;
            return s;
        }

        // A deleted folder's prefix can stay: nothing live points at its slot, and Add clears it on reuse
        void Free(int s)
        {
            Names[s] = Lows[s] = null;
            free.Push(s);
            Live--;
        }

        IEnumerable<int> Slots(ulong id) => links.TryGetValue(id, out var all) ? all : slot.TryGetValue(id, out var s) ? [s] : [];

        // The file is gone: every name it had
        public void Remove(ulong id)
        {
            foreach (var s in Slots(id)) Free(s);
            slot.Remove(id);
            links.Remove(id);
        }

        void RemoveLink(ulong id, int s)
        {
            Free(s);
            if (!links.TryGetValue(id, out var all))
            {
                slot.Remove(id);
                return;
            }
            all.Remove(s);
            slot[id] = all[0];
            if (all.Count == 1) links.Remove(id);
        }

        // A journal record: a create, a rename's old or new name, or a hard link added or removed
        public void Apply(ulong id, ulong parentId, string name, bool dir)
        {
            var parent = ParentSlot(parentId);
            if (dir)  // folders have one name: renamed or moved in place, so their contents stay under them
            {
                if (!slot.TryGetValue(id, out var d)) Add(name, parent, true, id);
                else if (Names[d] != name || Parent[d] != parent)
                {
                    (Names[d], Lows[d], Parent[d]) = (name, name.ToLowerInvariant(), parent);
                    dirsMoved = true;
                }
                return;
            }
            // A file's record does not say whether this name was added or removed: the disk does
            var s = Slots(id).FirstOrDefault(k => Parent[k] == parent && string.Equals(Names[k], name, StringComparison.OrdinalIgnoreCase), -1);
            var exists = File.Exists(FullPath(parent, name));
            if (exists && s < 0) Add(name, parent, false, id);
            else if (exists) (Names[s], Lows[s]) = (name, name.ToLowerInvariant());  // e.g. a case-only rename
            else if (s >= 0) RemoveLink(id, s);
        }

        string FullPath(int dir, string name)
        {
            var parts = new List<string> { name };
            for (var depth = 0; dir > 0 && depth < 512; dir = Parent[dir], depth++) parts.Add(Names[dir] ?? "");
            parts.Add(rootPre.TrimEnd('/'));
            parts.Reverse();
            return string.Join('\\', parts);
        }

        // shortcut: an entry whose folder is unknown is listed under the drive root
        int ParentSlot(ulong parentId) => slot.TryGetValue(parentId, out var p) ? p : 0;

        public void ResolveParents(List<ulong> parentIds)
        {
            for (var s = 1; s < parentIds.Count; s++) Parent[s] = ParentSlot(parentIds[s]);
        }

        // "C:/Users/me/" per folder slot (null for files): path terms match the folder path with / for \,
        // as nas_search.py does. All rebuilt only after a folder was renamed or moved; a new folder gets just its own.
        // The arrays can be longer than Names.
        public (string?[] Pre, string?[] PreLow) Prefixes()
        {
            if (dirsMoved)
            {
                pre = new string?[Names.Capacity];
                preLow = new string?[Names.Capacity];
                for (var s = 0; s < Names.Count; s++)
                    if (IsDir[s] && (s == 0 || Names[s] is not null)) PreLowOf(s);
                dirsMoved = false;
                newDirs.Clear();
            }
            if (newDirs.Count > 0)
            {
                if (pre.Length < Names.Count)  // grown with the list, not by one slot per new folder
                {
                    Array.Resize(ref pre, Names.Capacity);
                    Array.Resize(ref preLow, Names.Capacity);
                }
                foreach (var s in newDirs)
                    if (Names[s] is not null && IsDir[s]) PreLowOf(s);
                newDirs.Clear();
            }
            return (pre, preLow);
        }

        string PreLowOf(int s)
        {
            if (pre[s] is null)
            {
                if (s == 0) (pre[0], preLow[0]) = (rootPre, rootPre.ToLowerInvariant());
                else
                {
                    var p = Parent[s];
                    if (p <= 0 || p == s || Names[p] is null || !IsDir[p]) p = 0;  // the root, or a stale parent
                    var up = PreLowOf(p);
                    (pre[s], preLow[s]) = (pre[p] + Names[s] + "/", up + Lows[s] + "/");
                }
            }
            return preLow[s]!;
        }

        // C:\Users\me, for opening a hit
        public string DirPath(int s) => (Prefixes().Pre[Parent[s]] ?? rootPre).TrimEnd('/').Replace('/', '\\');
    }

    readonly string[] roots;
    readonly Volume?[] volumes;
    readonly Volume?[] building;  // the index being read, per root, for Found
    readonly TaskCompletionSource[] firsts;
    readonly Task first;

    public bool Ready => first.IsCompleted;

    // Entries read so far, for the progress shown while the first index is built (a rough count read across threads)
    public int Found => building.Sum(v => v is null ? 0 : v.Names.Count - 1);

    // Every fixed drive, e.g. C:\ and D:\
    public static string[] FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToArray();

    // rescan: how often a root without a usable journal is walked again
    public LocalIndex(string[] roots, TimeSpan rescan)
    {
        this.roots = roots;
        volumes = new Volume?[roots.Length];
        building = new Volume?[roots.Length];
        firsts = roots.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        first = Task.WhenAll(firsts.Select(t => t.Task));
        for (var i = 0; i < roots.Length; i++)
        {
            var n = i;
            new Thread(() => Run(n, rescan)) { IsBackground = true, Name = "index " + roots[n] }.Start();
        }
    }

    void Publish(int i, Volume v)
    {
        volumes[i] = v;
        firsts[i].TrySetResult();
    }

    void Run(int i, TimeSpan rescan)
    {
        try
        {
            Journal(i);  // returns or throws only when the journal cannot be used
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException) { }
        while (true)
        {
            Publish(i, Scan(building[i] = new Volume(roots[i]), roots[i]));
            Thread.Sleep(rescan);
        }
    }

    // ---- folder walk: any file system, no admin rights ----

    static Volume Scan(Volume v, string root)
    {
        var slotOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [root.TrimEnd('\\')] = 0 };
        // AttributesToSkip = 0: hidden and system files too, like Everything
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var last = ("", 0);  // entries come grouped by folder, each folder after its own entry
        foreach (var (dir, name, folder) in new FileSystemEnumerable<(string, string, bool)>(root,
                     (ref FileSystemEntry e) => (e.Directory.ToString(), e.FileName.ToString(), e.IsDirectory), opts)
                 {
                     // .NET would follow junctions and folder symlinks: pnpm's node_modules listed 2-3 times, or a loop
                     ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
                 })
        {
            if (dir != last.Item1) last = (dir, slotOf.GetValueOrDefault(dir.TrimEnd('\\')));
            var s = v.Add(name, last.Item2, folder);
            if (folder) slotOf[Path.Join(dir, name)] = s;
        }
        return v;
    }

    // ---- NTFS master file table + change journal ----

    const uint FSCTL_READ_USN_JOURNAL = 0x900BB, FSCTL_QUERY_USN_JOURNAL = 0x900F4, FSCTL_QUERY_FILE_LAYOUT = 0x90277;
    const uint USN_REASON_FILE_CREATE = 0x100, USN_REASON_FILE_DELETE = 0x200, USN_REASON_RENAME_OLD_NAME = 0x1000,
        USN_REASON_RENAME_NEW_NAME = 0x2000, USN_REASON_HARD_LINK_CHANGE = 0x10000;
    const int ERROR_HANDLE_EOF = 38;
    const ulong IdMask = 0xFFFF_FFFF_FFFF;  // the MFT record number; the top 16 bits (its reuse count) are dropped
    const ulong RootId = 5;

    // NTFS's own files: $MFT, $Bitmap, the $Extend folder... (records 0-15, the root folder 5 among them)
    static bool Meta(ulong id) => id < 16;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inputSize, byte[] output, int outputSize,
        out int returned, IntPtr overlapped);

    // bytes returned, or minus the Windows error code
    static int Ioctl(SafeFileHandle h, uint code, byte[] input, byte[] output) =>
        DeviceIoControl(h, code, input, input.Length, output, output.Length, out var n, IntPtr.Zero) ? n : -Marshal.GetLastWin32Error();

    void Journal(int i)
    {
        var root = roots[i];
        if (root is not [_, ':', '\\'] || new DriveInfo(root).DriveFormat != "NTFS") return;
        // GENERIC_READ, share read + write, OPEN_EXISTING; fails with access denied without admin rights
        using var h = CreateFile(@"\\.\" + root[..2], 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception();
        var buf = new byte[1 << 20];
        while (true)  // once per full read; again when the journal was cut or recreated under us
        {
            if (Ioctl(h, FSCTL_QUERY_USN_JOURNAL, [], buf) is var n and < 0) throw new Win32Exception(-n);  // e.g. journal off
            var journalId = BinaryPrimitives.ReadUInt64LittleEndian(buf);
            var next = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16));  // changes during the read are replayed after it
            var v = ReadAll(h, building[i] = new Volume(root), buf);
            Publish(i, v);
            Follow(h, v, journalId, next, buf);
        }
    }

    // Every name of every file and folder on the drive, straight from the master file table. A file with hard links
    // (System32 into WinSxS, pnpm's node_modules) has several; FSCTL_ENUM_USN_DATA would give only one of them.
    static Volume ReadAll(SafeFileHandle h, Volume v, byte[] buf)
    {
        var parents = new List<ulong> { 0 };  // per slot, until every folder is known
        // QUERY_FILE_LAYOUT_INPUT: FilterEntryCount, Flags (RESTART | INCLUDE_NAMES), FilterType (none), Reserved, Filter
        var input = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 3);
        while (true)
        {
            var n = Ioctl(h, FSCTL_QUERY_FILE_LAYOUT, input, buf);
            if (n == -ERROR_HANDLE_EOF) break;
            if (n < 0) throw new Win32Exception(-n);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 2);  // go on where the last call stopped
            // QUERY_FILE_LAYOUT_OUTPUT: FileEntryCount, FirstFileOffset, ...; then FILE_LAYOUT_ENTRYs with their names
            var count = BinaryPrimitives.ReadInt32LittleEndian(buf);
            var e = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4));
            for (var k = 0; k < count && e > 0 && e < n; k++)
            {
                // FILE_LAYOUT_ENTRY: Version, NextFileOffset, Flags, FileAttributes, FileReferenceNumber, FirstNameOffset
                var entry = buf.AsSpan(e, n - e);
                var dir = (BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) & (uint)FileAttributes.Directory) != 0;
                var id = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]) & IdMask;
                var nameAt = BinaryPrimitives.ReadInt32LittleEndian(entry[24..]);
                for (var at = e + nameAt; nameAt > 0 && !Meta(id) && at < n;)
                {
                    // FILE_LAYOUT_NAME_ENTRY: NextNameOffset, Flags, ParentFileReferenceNumber, FileNameLength, Reserved, FileName
                    var ne = buf.AsSpan(at, n - at);
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(ne[4..]);
                    var parent = BinaryPrimitives.ReadUInt64LittleEndian(ne[8..]) & IdMask;
                    if (flags != 2 && (!Meta(parent) || parent == RootId))  // 2: the 8.3 short name, an alias of the long one
                    {
                        v.Add(Encoding.Unicode.GetString(ne.Slice(24, BinaryPrimitives.ReadInt32LittleEndian(ne[16..]))), 0, dir, id);
                        parents.Add(parent);
                    }
                    var next = BinaryPrimitives.ReadInt32LittleEndian(ne);
                    if (next <= 0) break;
                    at += next;
                }
                var nextEntry = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
                if (nextEntry <= 0) break;
                e += nextEntry;
            }
        }
        v.ResolveParents(parents);  // children can come before their folder
        return v;
    }

    // Applies each change as it happens. Returns when the journal was cut or recreated, so the drive is read again.
    static void Follow(SafeFileHandle h, Volume v, ulong journalId, long usn, byte[] buf)
    {
        // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID
        var input = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE
            | USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME | USN_REASON_HARD_LINK_CHANGE);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24), 1);  // block until there is something
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, usn);
            var n = Ioctl(h, FSCTL_READ_USN_JOURNAL, input, buf);
            if (n < 0) return;  // e.g. ERROR_JOURNAL_ENTRY_DELETED: changes were lost, read the drive again
            usn = BinaryPrimitives.ReadInt64LittleEndian(buf);
            if (n <= 8)
            {
                Thread.Sleep(500);
                continue;
            }
            lock (v)
                foreach (var r in Records(buf, 8, n))
                    if (Meta(r.Id) || Meta(r.ParentId) && r.ParentId != RootId) continue;
                    else if ((r.Reason & USN_REASON_FILE_DELETE) != 0) v.Remove(r.Id);
                    else v.Apply(r.Id, r.ParentId, r.Name, r.Dir);
        }
    }

    record struct UsnRecord(ulong Id, ulong ParentId, uint Reason, bool Dir, string Name);

    // USN_RECORD_V2 entries in buf[from..to]
    static List<UsnRecord> Records(byte[] buf, int from, int to)
    {
        var list = new List<UsnRecord>();
        for (var off = from; off + 60 <= to;)
        {
            var s = buf.AsSpan(off, to - off);
            var len = BinaryPrimitives.ReadInt32LittleEndian(s);
            if (len <= 0) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(s[4..]) == 2)  // V3 (ReFS, 128-bit ids) does not occur on NTFS
                list.Add(new UsnRecord(
                    BinaryPrimitives.ReadUInt64LittleEndian(s[8..]) & IdMask,
                    BinaryPrimitives.ReadUInt64LittleEndian(s[16..]) & IdMask,
                    BinaryPrimitives.ReadUInt32LittleEndian(s[40..]),
                    (BinaryPrimitives.ReadUInt32LittleEndian(s[52..]) & (uint)FileAttributes.Directory) != 0,
                    Encoding.Unicode.GetString(s.Slice(BinaryPrimitives.ReadUInt16LittleEndian(s[58..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(s[56..])))));
            off += len;
        }
        return list;
    }

    // ---- search ----

    public async Task<SearchResponse> SearchAsync(string q, CancellationToken ct)
    {
        await first.WaitAsync(ct);
        return await Task.Run(() =>
        {
            var total = 0;
            var indexed = 0;
            var top = new List<(string Low, Hit Hit, string Full, bool Dir)>();
            foreach (var v in volumes)
            {
                lock (v!)
                {
                    var test = Parse(q, v);
                    var hits = Enumerable.Range(0, v.Names.Count).AsParallel().WithCancellation(ct)
                        .Where(i => v.Names[i] is not null && test(i)).ToArray();
                    total += hits.Length;
                    indexed += v.Live;
                    // a few extra, for hits deleted since a walked root was last walked
                    foreach (var i in First(hits, v.Lows, Limit + 20))
                    {
                        var dir = v.DirPath(i);
                        top.Add((v.Lows[i]!, new Hit(v.Names[i]!, @"\\localhost\" + dir[0] + "$" + dir[2..], null, 0),
                            dir + "\\" + v.Names[i], v.IsDir[i]));
                    }
                }
            }
            var results = new List<Hit>();
            foreach (var (_, hit, full, dir) in top.OrderBy(t => t.Low, StringComparer.Ordinal))
            {
                if (results.Count == Limit) break;
                FileSystemInfo f = dir ? new DirectoryInfo(full) : new FileInfo(full);
                if (!f.Exists) continue;
                results.Add(hit with { Size = f is FileInfo fi ? fi.Length : null, Mtime = f.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds });
            }
            return new SearchResponse(total, indexed, results.ToArray());
        }, ct);
    }

    // The k hits first in name order, sorted. A max-heap of k: most hits lose one comparison against its top,
    // where sorting all of them (a short query matches most of the drive) took most of the search.
    static IEnumerable<int> First(int[] hits, List<string?> lows, int k)
    {
        var heap = new PriorityQueue<int, string>(k + 1, Comparer<string>.Create((a, b) => string.CompareOrdinal(b, a)));
        foreach (var i in hits)
        {
            if (heap.Count < k) heap.Enqueue(i, lows[i]!);
            else if (string.CompareOrdinal(lows[i], heap.TryPeek(out _, out var last) ? last : null) < 0) heap.EnqueueDequeue(i, lows[i]!);
        }
        var sorted = new int[heap.Count];
        for (var n = sorted.Length - 1; n >= 0; n--) sorted[n] = heap.Dequeue();
        return sorted;
    }

    static readonly Regex ExtOk = new(@"^[^\s|<>"";]+$");

    public async Task<ExtsResponse> ExtsAsync(CancellationToken ct)
    {
        await first.WaitAsync(ct);
        return await Task.Run(() =>
        {
            var counts = new Dictionary<string, int>();
            foreach (var v in volumes)
                lock (v!)
                    for (var i = 0; i < v.Lows.Count; i++)
                        if (v.Lows[i] is { } l && !v.IsDir[i] && l.LastIndexOf('.') is var dot and >= 0)
                            counts[l[(dot + 1)..]] = counts.GetValueOrDefault(l[(dot + 1)..]) + 1;
            return new ExtsResponse(counts.Where(c => ExtOk.IsMatch(c.Key)).Select(c => new ExtCount(c.Key, c.Value))
                .OrderByDescending(e => e.Count).ToArray());
        }, ct);
    }

    // Everything syntax, as in nas_search.py: space = AND, | = OR (binds tighter than AND), ! = NOT, < > = group,
    // "..." = plain text. Returns a test per live slot.
    static readonly Regex Token = new(@"[|<>]|(?:""[^""]*""?|[^\s|<>""])+");
    static readonly Regex Mod = new(@"^(files?|folders?|path|case|regex|ext):", RegexOptions.IgnoreCase);

    // Plain loops over arrays: these run once per entry per search, where LINQ's All/Any would allocate each time
    static Func<int, bool> All(Func<int, bool>[] fs) => fs.Length switch
    {
        0 => _ => true,
        1 => fs[0],
        2 => i => fs[0](i) && fs[1](i),
        _ => i =>
        {
            foreach (var f in fs) if (!f(i)) return false;
            return true;
        },
    };

    static Func<int, bool> Any(Func<int, bool>[] fs) => fs.Length == 1 ? fs[0] : i =>
    {
        foreach (var f in fs) if (f(i)) return true;
        return false;
    };

    static Func<int, bool> Parse(string q, Volume x)
    {
        var toks = new Stack<string>(Token.Matches(q).Select(m => m.Value).Reverse());
        // How much a test costs per entry; AND runs the cheap ones first, so the dear ones see only what is left.
        // Not set for NOT, OR and groups: they go last, as in nas_search.py.
        var cost = new Dictionary<Func<int, bool>, int>();

        Func<int, bool> Expr(bool inner)
        {
            var ands = new List<Func<int, bool>>();
            while (toks.Count > 0 && !(inner && toks.Peek() == ">"))
            {
                if (toks.Peek() is "|" or ">")  // stray operator
                {
                    toks.Pop();
                    continue;
                }
                var ors = new List<Func<int, bool>> { Unary() };
                while (toks.Count > 0 && toks.Peek() == "|")
                {
                    toks.Pop();
                    if (toks.Count > 0 && toks.Peek() is not ("|" or ">")) ors.Add(Unary());
                }
                ands.Add(Any([.. ors]));
            }
            return All([.. ands.OrderBy(f => cost.GetValueOrDefault(f, 3))]);  // stable: equal ones keep their order
        }

        Func<int, bool> Unary()
        {
            var t = toks.Pop();
            if (t == "<")
            {
                var node = Expr(true);
                if (toks.Count > 0) toks.Pop();  // the closing >
                return node;
            }
            var bang = t.Length - t.TrimStart('!').Length;
            t = t[bang..];
            var n = t == "" && toks.Count > 0 && toks.Peek() is not ("|" or ">") ? Unary() : Term(t, x, cost);
            return bang % 2 == 1 ? i => !n(i) : n;
        }

        return Expr(false);
    }

    static Func<int, bool> Term(string t, Volume x, Dictionary<Func<int, bool>, int> cost)
    {
        var mods = new HashSet<string>();
        while (Mod.Match(t) is { Success: true } m)
        {
            mods.Add(m.Groups[1].Value.ToLowerInvariant().TrimEnd('s'));
            t = t[m.Length..];
        }
        t = t.Replace("\"", "");
        bool regex = mods.Contains("regex"), ext = mods.Contains("ext"), cs = mods.Contains("case") && !ext;
        var s = cs ? t : t.ToLowerInvariant();
        var onPath = mods.Contains("path") || !(regex || ext) && (s.Contains('/') || s.Contains('\\'));
        if (!regex) s = s.Replace('\\', '/');
        bool? wantDir = mods.Contains("folder") ? true : mods.Contains("file") || ext ? false : null;
        var names = cs ? x.Names : x.Lows;
        var (preCase, preLow) = x.Prefixes();
        var pre = cs ? preCase : preLow;
        string Text(int i) => onPath ? pre[x.Parent[i]] + names[i] : names[i]!;

        Func<int, bool>? test;
        if (regex)
        {
            var opts = RegexOptions.Singleline | (cs ? 0 : RegexOptions.IgnoreCase);  // t, not s: lowercasing would turn \D into \d
            Regex rx;
            try { rx = new Regex(t, opts | RegexOptions.Compiled); }
            catch (ArgumentException) { rx = new Regex(Regex.Escape(t), opts); }  // half-typed pattern: plain text
            test = i => rx.IsMatch(Text(i));
        }
        else if (ext)
        {
            var exts = s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => "." + e).ToArray();
            var lows = x.Lows;
            test = i =>
            {
                foreach (var e in exts) if (lows[i]!.EndsWith(e, StringComparison.Ordinal)) return true;
                return false;
            };
        }
        else if (s.Contains('*') || s.Contains('?'))
        {
            // as in Everything, * and ? stop at a path separator; ** does not
            var rx = new Regex("^(?:" + string.Concat(Regex.Split(s, @"(\*\*|[*?])").Select(p =>
                p switch { "**" => ".*", "*" => "[^/]*", "?" => "[^/]", _ => Regex.Escape(p) })) + @")\z", RegexOptions.Singleline | RegexOptions.Compiled);
            // every match contains the longest literal piece: a plain scan for it skips the regex for nearly every entry
            var lit = Regex.Split(s, @"[*?]+").MaxBy(p => p.Length)!;
            test = lit == "" ? i => rx.IsMatch(Text(i))
                : onPath ? i => Text(i).Contains(lit, StringComparison.Ordinal) && rx.IsMatch(Text(i))
                : i => names[i]!.Contains(lit, StringComparison.Ordinal) && rx.IsMatch(names[i]!);
        }
        else if (onPath)
        {
            // s inside the folder path, or inside the name, or (with a /) ending the folder path and starting the name.
            // Checking each folder once avoids building a path string per entry.
            var inside = pre.Select(p => p is not null && p.Contains(s, StringComparison.Ordinal)).ToArray();
            var cut = s.LastIndexOf('/');
            if (cut < 0)
                test = i => inside[x.Parent[i]] || names[i]!.Contains(s, StringComparison.Ordinal);
            else
            {
                var (head, tail) = (s[..(cut + 1)], s[(cut + 1)..]);
                var ends = pre.Select(p => p is not null && p.EndsWith(head, StringComparison.Ordinal)).ToArray();
                test = i => inside[x.Parent[i]] || tail != "" && ends[x.Parent[i]] && names[i]!.StartsWith(tail, StringComparison.Ordinal);
            }
        }
        else if (s != "")
            test = i => names[i]!.Contains(s, StringComparison.Ordinal);
        else
            test = null;

        var isDir = x.IsDir;
        var f = wantDir is not { } want ? test ?? (_ => true)
            : test is null ? i => isDir[i] == want : i => isDir[i] == want && test(i);
        cost[f] = regex ? 2 : onPath || s.Contains('*') || s.Contains('?') ? 1 : 0;
        return f;
    }
}
