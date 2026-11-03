"""Everything-style instant file name search for a Synology NAS.

Scans ROOT every SCAN_MINUTES, keeps all names in memory and serves a search page.
Standard library only, so it runs in the stock python Docker image.
"""
import base64
import hmac
import json
import os
import re
import stat
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
Index = namedtuple('Index', 'names lows parent isdir dirs big starts scanned')


def pack(names, parent, isdir, dirs, scanned):
    lows = [n.lower() for n in names]
    order = sorted(range(len(names)), key=lows.__getitem__)
    big = '\0' + '\0'.join([lows[i] for i in order]) + '\0'
    # fresh strings laid out in sorted order: the search scan then reads memory sequentially (~3x faster)
    names = '\0'.join([names[i] for i in order]).split('\0') if names else []
    lows = big[1:-1].split('\0') if names else []
    starts = array('q', accumulate(map(add, map(len, lows), repeat(1)), initial=0))
    return Index(names, lows, [parent[i] for i in order], bytearray(map(isdir.__getitem__, order)),
                 dirs, big, starts, scanned)


INDEX = pack([], [], bytearray(), [''], 0)


def skip(d):
    # Synology metadata (@eaDir thumbnails, @tmp, ...) and recycle bin / snapshots
    return d.startswith('@') or d in ('#recycle', '#snapshot')


def build():
    # shortcut: full rescan, ~300 MB RAM per million entries (about double during a rescan);
    # upgrade to inotify + SQLite if the NAS grows past a few million files.
    names, parent, isdir, dirs = [], [], bytearray(), []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if not skip(d)]
        parent += [len(dirs)] * (len(dirnames) + len(filenames))
        dirs.append(dirpath[len(ROOT):])
        names += dirnames + filenames
        isdir += b'\1' * len(dirnames) + b'\0' * len(filenames)
    return pack(names, parent, isdir, dirs, time.time())


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
    out = []
    for i in hits[:LIMIT]:
        d = ix.dirs[ix.parent[i]]
        try:
            st = os.stat(ROOT + d + '/' + ix.names[i])
        except OSError:  # deleted since the last scan
            continue
        out.append({'name': ix.names[i], 'path': '\\\\' + NAS + d.replace('/', '\\'),
                    'size': None if stat.S_ISDIR(st.st_mode) else st.st_size, 'mtime': st.st_mtime})
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
        else:
            body, ctype = PAGE, 'text/html; charset=utf-8'
        self.send_response(200)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


PAGE = r'''<!doctype html>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>NAS Search</title>
<style>
:root { color-scheme: light dark; font: 14px system-ui, "Microsoft YaHei", sans-serif; }
body { margin: 0; padding: 12px 16px; }
#q { width: 100%; box-sizing: border-box; font-size: 18px; padding: 8px 10px; }
#st { margin: 8px 0; opacity: .7; }
table { width: 100%; border-collapse: collapse; }
th, td { text-align: left; padding: 4px 8px; white-space: nowrap; }
td:nth-child(2) { white-space: normal; word-break: break-all; opacity: .7; }
td:nth-child(3) { text-align: right; }
tbody tr { cursor: pointer; }
tbody tr:hover { background: color-mix(in srgb, currentColor 10%, transparent); }
</style>
<input id="q" autofocus autocomplete="off"
  placeholder="搜索文件名　空格 = 并且　a|b = 或者　!词 = 排除　&quot;带 空格&quot;　*.mkv　ext:pdf;docx　含 \ = 匹配路径">
<div id="st"></div>
<table><thead><tr><th>名称</th><th>路径</th><th>大小</th><th>修改时间</th></tr></thead><tbody id="r"></tbody></table>
<script>
let timer, seq = 0;
q.oninput = () => { clearTimeout(timer); timer = setTimeout(go, 120); };

function size(n) {
  if (n == null) return '';
  const u = Math.min(4, Math.floor(Math.log(Math.max(n, 1)) / Math.log(1024)));
  return u ? (n / 1024 ** u).toFixed(1) + ' ' + 'KMGT'[u - 1] + 'B' : n + ' B';
}

function copy(s) {
  // navigator.clipboard needs HTTPS; this works on plain http://nas:port
  const a = document.createElement('textarea');
  a.value = s; document.body.append(a); a.select(); document.execCommand('copy'); a.remove();
  st.textContent = '已复制：' + s + '　（Win+R 粘贴回车即可打开）';
}

async function go() {
  const my = ++seq;
  let d;
  try { d = await (await fetch('search?q=' + encodeURIComponent(q.value))).json(); }
  catch (e) { st.textContent = '连接失败'; return; }
  if (my !== seq) return;  // a newer query already went out
  st.textContent = d.total + ' 个结果' + (d.total > d.results.length ? '，显示前 ' + d.results.length + ' 个' : '') +
    '　·　索引 ' + d.indexed + ' 项，' + (d.scanned ? '更新于 ' + new Date(d.scanned * 1000).toLocaleString() : '首次扫描中…');
  r.replaceChildren(...d.results.map(x => {
    const tr = document.createElement('tr');
    [x.name + (x.size == null ? '\\' : ''), x.path, size(x.size), new Date(x.mtime * 1000).toLocaleString()]
      .forEach(v => tr.insertCell().textContent = v);
    tr.onclick = () => copy(x.path + '\\' + x.name);
    return tr;
  }));
}
go();
</script>
'''.encode()


if __name__ == '__main__':
    threading.Thread(target=scan_forever, daemon=True).start()
    print(f'serving http://0.0.0.0:{PORT}  root={ROOT}', flush=True)
    ThreadingHTTPServer(('', PORT), Handler).serve_forever()
