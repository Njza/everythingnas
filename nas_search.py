"""Everything-style instant file name search for a Synology NAS.

Scans ROOT every SCAN_MINUTES, keeps all names in memory and answers /search and /exts for NasSearch.exe.
Standard library only, so it runs in the stock python Docker image.
"""
import base64
import hmac
import json
import os
import re
import threading
import time
from array import array
from bisect import bisect_right
from collections import Counter, namedtuple
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from itertools import accumulate, compress, count, filterfalse, repeat
from operator import add, and_, contains, not_, or_
from urllib.parse import parse_qs, urlsplit

ROOT = os.environ.get('ROOT', '/data').rstrip('/')
NAS = os.environ.get('NAS_NAME', 'NAS')
SCAN_MINUTES = float(os.environ.get('SCAN_MINUTES', '30'))
PORT = int(os.environ.get('PORT', '8765'))
AUTH = os.environ.get('AUTH')  # "user:password" turns on HTTP basic auth
AUTH = AUTH and 'Basic ' + base64.b64encode(AUTH.encode()).decode()
LIMIT = 200

# Entries sorted by lowercase name; parent indexes dirs (relative to ROOT, like "/video/Movies").
# big is every lowercase name between \0 separators, starts[i] the offset of the \0 before entry i.
# size (-1 for folders) and mtime come from the scan, so a search reads no disk.
Index = namedtuple('Index', 'names lows parent isdir dirs big starts scanned size mtime')


def pack(names, parent, isdir, dirs, scanned, size=None, mtime=None):
    size = size or array('q', bytes(8 * len(names)))
    mtime = mtime or array('d', bytes(8 * len(names)))
    lows = [n.lower() for n in names]
    order = sorted(range(len(names)), key=lows.__getitem__)
    big = '\0' + '\0'.join([lows[i] for i in order]) + '\0'
    # fresh strings laid out in sorted order: the search scan then reads memory sequentially (~3x faster)
    names = '\0'.join([names[i] for i in order]).split('\0') if names else []
    lows = big[1:-1].split('\0') if names else []
    starts = array('q', accumulate(map(add, map(len, lows), repeat(1)), initial=0))
    return Index(names, lows, [parent[i] for i in order], bytearray(map(isdir.__getitem__, order)),
                 dirs, big, starts, scanned, array('q', map(size.__getitem__, order)),
                 array('d', map(mtime.__getitem__, order)))


INDEX = pack([], [], bytearray(), [''], 0)


def skip(d):
    # Synology metadata (@eaDir thumbnails, @tmp, ...; on a whole volume also @appstore, @docker...) and
    # recycle bin / snapshots
    return d.startswith('@') or d in ('#recycle', '#snapshot')


# A whole storage volume mounted as /data/volume1 (the default docker-compose.yml): its top folders are the shared
# folders, so /volume1/video/Movies is \\NAS\video\Movies. Per-share mounts (/data/video) need no stripping.
# shortcut: a shared folder itself named like "volume1" would lose that name in its paths; nobody names one so.
VOLUME = re.compile(r'volume\d+')


def unc_dir(rel):
    """'/volume1/video/Movies' or '/video/Movies' -> '/video/Movies', the folder as \\\\NAS\\... shows it."""
    head, _, rest = rel[1:].partition('/')
    if not VOLUME.fullmatch(head):
        return rel
    return '/' + rest if rest else ''  # the volume itself is the NAS root, like /data was


def build():
    # shortcut: full rescan, ~320 MB RAM per million entries (about double during a rescan); a file deleted since
    # the last scan stays listed until the next one. Upgrade to inotify + SQLite past a few million files.
    names, parent, isdir, dirs = [], [], bytearray(), []
    size, mtime = array('q'), array('d')
    todo = ['']  # folders still to list, relative to ROOT
    while todo:
        rel = todo.pop()
        here = len(dirs)
        dirs.append(unc_dir(rel))  # only for showing and path terms: searches read no disk
        try:
            entries = os.scandir(ROOT + rel)
        except OSError:  # gone or unreadable since its folder was listed
            continue
        with entries:
            for e in entries:
                try:
                    folder = e.is_dir(follow_symlinks=False)  # like os.walk: never into a linked folder
                    if folder and skip(e.name):
                        continue
                    st = e.stat(follow_symlinks=False)  # one stat per entry per scan, none per search
                except OSError:
                    continue
                if folder and rel == '' and VOLUME.fullmatch(e.name):
                    todo.append('/' + e.name)  # a mounted volume: list its shares, but it is no entry itself
                    continue
                names.append(e.name)
                parent.append(here)
                isdir.append(folder)
                size.append(-1 if folder else st.st_size)
                mtime.append(st.st_mtime)
                if folder:
                    todo.append(rel + '/' + e.name)
    return pack(names, parent, isdir, dirs, time.time(), size, mtime)


# Everything syntax: space = AND, | = OR (binds tighter than AND), ! = NOT, < > = group, "..." = plain text
TOKEN = re.compile(r'[|<>]|(?:"[^"]*"?|[^\s|<>"])+')
MOD = re.compile(r'(files?|folders?|path|case|regex|ext):', re.I)
WILD = {'**': '.*', '*': '[^/]*', '?': '[^/]'}  # as in Everything, * and ? stop at a path separator


def parse(q):
    """Query -> ('and', [nodes]) / ('or', [nodes]) / ('not', node) / ('term', run, rank) tree."""
    toks = TOKEN.findall(q)[::-1]

    def expr(inner):
        ands = []
        while toks and not (inner and toks[-1] == '>'):
            if toks[-1] in ('|', '>'):  # stray operator
                toks.pop()
                continue
            ors = [unary()]
            while toks and toks[-1] == '|':
                toks.pop()
                if toks and toks[-1] not in ('|', '>'):
                    ors.append(unary())
            ands.append(ors[0] if len(ors) == 1 else ('or', ors))
        return ('and', ands)

    def unary():
        t = toks.pop()
        if t == '<':
            node = expr(True)
            if toks:
                toks.pop()  # the closing >
            return node
        bang = len(t) - len(t.lstrip('!'))
        t = t[bang:]
        node = unary() if not t and toks and toks[-1] not in ('|', '>') else term(t)
        return ('not', node) if bang % 2 else node

    return expr(False)


def term(t):
    """One term and its modifiers -> ('term', run, rank); run(ix, cand) keeps the entries of cand that match."""
    mods = set()
    while m := MOD.match(t):
        mods.add(m[1].lower().rstrip('s'))
        t = t[m.end():]
    t = t.replace('"', '')
    regex, ext = 'regex' in mods, 'ext' in mods
    case = 'case' in mods and not ext
    s = t if case else t.lower()
    on_path = 'path' in mods or not (regex or ext) and ('/' in s or '\\' in s)
    if not regex:
        s = s.replace('\\', '/')
    want_dir = True if 'folder' in mods else False if 'file' in mods or ext else None
    keys = []  # text every match contains, one of them at least: lets run() use str.find over Index.big
    lit = ''  # text every match contains, checked with a plain scan before a slow wildcard regex
    if regex:
        flags = re.S if case else re.S | re.I  # t, not s: lowercasing would turn \D into \d
        try:
            rx = re.compile(t, flags)
        except re.error:  # half-typed pattern: match it as plain text
            rx = re.compile(re.escape(t), flags)
        test = lambda ix, cand: map(rx.search, texts(ix, cand, on_path, case))
    elif ext:
        exts = tuple('.' + e for e in s.split(';') if e)
        keys = [e + '\0' for e in exts]
        test = lambda ix, cand: map(str.endswith, texts(ix, cand, False, False), repeat(exts))
    elif '*' in s or '?' in s:
        rx = re.compile(''.join(WILD.get(p) or re.escape(p) for p in re.split(r'(\*\*|[*?])', s)), re.S)
        test = lambda ix, cand: map(rx.fullmatch, texts(ix, cand, on_path, case))
        if not on_path:  # wildcards match the whole name, so the first and last literal sit next to a \0
            lits = re.split(r'[*?]+', s.lower())
            lit = max(lits, key=len)
            lits[0], lits[-1] = '\0' + lits[0], lits[-1] + '\0'
            keys = [max(lits, key=len)]
    elif on_path:
        test = lambda ix, cand: path_contains(ix, cand, s, case)
    elif s:
        keys = [s.lower()]
        test = lambda ix, cand: map(contains, texts(ix, cand, False, case), repeat(s))
    else:
        test = None
    if not all(k.strip('\0') for k in keys):  # a bare * has no text to look for
        keys = []

    def run(ix, cand):
        found = find_any(ix, keys) if keys and isinstance(cand, range) else None
        if found is not None:
            cand = found
        elif lit:
            cand = list(compress(cand, map(contains, col(ix.lows, cand), repeat(lit))))
        if want_dir is not None:
            sel = col(ix.isdir, cand)
            cand = list(compress(cand, sel if want_dir else map(not_, sel)))
        return list(compress(cand, test(ix, cand))) if test else cand
    return ('term', run, max(map(len, keys), default=0))


def match(node, ix, cand):
    """The entries of cand (ascending, so in name order) that match node."""
    op = node[0]
    if op == 'term':
        return node[1](ix, cand)
    if op == 'not':
        drop = set(match(node[1], ix, cand))
        return list(filterfalse(drop.__contains__, cand))
    if op == 'or':
        found = set()
        for n in node[1]:
            found.update(match(n, ix, cand))
        return sorted(found)
    # AND: longest literal first, so the later terms scan fewer entries; exclusions last
    for n in sorted(node[1], key=lambda n: (n[0] == 'not', -n[2] if n[0] == 'term' else 0)):
        cand = match(n, ix, cand)
    return cand


def find_all(ix, s):
    """Entries whose \0-delimited lowercase name contains s, via str.find hopping over Index.big at C speed.
    None when s is so common that a plain scan is cheaper."""
    big, starts, out = ix.big, ix.starts, []
    cap = len(starts) // 32 + 100
    n = len(big)
    if 64 * sum(big.count(s, a, a + n // 2048 + 1) for a in range(0, n, n // 32 + 1)) > cap:
        return None  # a 1/64 sample says s is common: skip the hopping instead of giving up halfway
    i = big.find(s)
    while i >= 0:
        k = bisect_right(starts, i) - 1
        out.append(k)
        if len(out) > cap:
            return None
        i = big.find(s, starts[k + 1])
    return out


def find_any(ix, keys):
    found = set()
    for k in keys:
        hits = find_all(ix, k)
        if hits is None:
            return None
        found.update(hits)
    return sorted(found)


def col(seq, cand):
    # one value per candidate; map/compress keep the per-entry loop in C (~10x faster than a comprehension)
    return seq if isinstance(cand, range) else map(seq.__getitem__, cand)


def prefixes(ix, case):
    # "//NAS/share/folder/" per folder: path terms match the UNC path, with / for \
    pre = ['//' + NAS + d + '/' for d in ix.dirs]
    return pre if case else [p.lower() for p in pre]


def texts(ix, cand, on_path, case):
    xs = col(ix.names if case else ix.lows, cand)
    return map(add, map(prefixes(ix, case).__getitem__, col(ix.parent, cand)), xs) if on_path else xs


def path_contains(ix, cand, s, case):
    # path = folder prefix + name, and names have no /: s lies inside the prefix, or inside the name, or
    # (when s has a /) its part up to the last / ends the prefix and the rest starts the name.
    # Checking folders once avoids building a path string per entry (~4x faster).
    pre = prefixes(ix, case)
    xs = col(ix.names if case else ix.lows, cand)
    inside = set(compress(count(), map(contains, pre, repeat(s))))
    hit = map(inside.__contains__, col(ix.parent, cand))
    if '/' not in s:
        return map(or_, hit, map(contains, xs, repeat(s)))
    head, _, tail = s.rpartition('/')
    if not tail:
        return hit
    ends = set(compress(count(), map(str.endswith, pre, repeat(head + '/'))))
    return map(or_, hit, map(and_, map(ends.__contains__, col(ix.parent, cand)), map(str.startswith, xs, repeat(tail))))


def search(q):
    ix = INDEX  # one snapshot: the scan thread swaps INDEX while searches run
    hits = match(parse(q), ix, range(len(ix.names)))
    out = [{'name': ix.names[i], 'path': '\\\\' + NAS + ix.dirs[ix.parent[i]].replace('/', '\\'),
            'size': None if ix.size[i] < 0 else ix.size[i], 'mtime': ix.mtime[i]} for i in hits[:LIMIT]]
    return {'total': len(hits), 'indexed': len(ix.names), 'scanned': ix.scanned, 'results': out}


EXT_OK = re.compile(r'[^\s|<>";]+')  # usable inside an ext:a;b term
_exts = (None, [])  # (index, its counts): one count per scan, not per request


def exts():
    """Every file extension in the index, most files first, as {ext, count}, lowercase like ext: matches."""
    global _exts
    ix = INDEX
    if _exts[0] is not ix:
        n = Counter(low.rpartition('.')[2] for low, d in zip(ix.lows, ix.isdir) if not d and '.' in low)
        _exts = (ix, [{'ext': e, 'count': c} for e, c in n.most_common() if EXT_OK.fullmatch(e)])
    return {'exts': _exts[1]}


def scan_forever():
    global INDEX
    while True:
        t = time.time()
        INDEX = build()
        exts()  # count the formats now, so the first format list after a scan does not wait for it
        print(f'indexed {len(INDEX[0])} entries in {time.time() - t:.1f}s', flush=True)
        time.sleep(SCAN_MINUTES * 60)


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'  # keep-alive: a search reuses the connection instead of opening one per keystroke

    def do_GET(self):
        if AUTH and not hmac.compare_digest(self.headers.get('Authorization', ''), AUTH):
            self.send_response(401)
            self.send_header('WWW-Authenticate', 'Basic realm="nas-search"')
            self.send_header('Content-Length', '0')  # HTTP/1.1: without it the client waits for a body
            self.end_headers()
            return
        u = urlsplit(self.path)
        if u.path == '/search':
            body = json.dumps(search(parse_qs(u.query).get('q', [''])[0])).encode()
            ctype = 'application/json'
        elif u.path == '/exts':
            body = json.dumps(exts()).encode()
            ctype = 'application/json'
        elif u.path == '/':  # no search page: NasSearch.exe is the client; this line shows the server is up
            ix = INDEX
            state = f'{len(ix.names)} entries indexed' if ix.scanned else 'first scan running'
            body = f'nas-search: {state}. Search with NasSearch.exe.\n'.encode()
            ctype = 'text/plain; charset=utf-8'
        else:
            self.send_response(404)
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        self.send_response(200)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


if __name__ == '__main__':
    threading.Thread(target=scan_forever, daemon=True).start()
    print(f'serving http://0.0.0.0:{PORT}  root={ROOT}', flush=True)
    ThreadingHTTPServer(('', PORT), Handler).serve_forever()
