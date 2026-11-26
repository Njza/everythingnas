"""Everything-style instant file name search for a Synology NAS.

Checks ROOT's folders for changes every CHECK_MINUTES, rescans it all every SCAN_MINUTES, keeps all names in memory
and answers /search and /exts for NasSearch.exe.
Standard library only, so it runs in the stock python Docker image.
"""
import base64
import hashlib
import json
import os
import re
import ssl
import sys
import threading
import time
import urllib.request
from array import array
from bisect import bisect_right
from collections import Counter, namedtuple
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from itertools import accumulate, compress, count, filterfalse, repeat
from operator import add, and_, contains, not_, or_
from urllib.parse import parse_qs, urlencode, urlsplit

VERSION = '1.2.0'  # raise it when this file changes: the web page shows it, so you can see which one the NAS runs
ROOT = os.environ.get('ROOT', '/data').rstrip('/')
NAS = os.environ.get('NAS_NAME', 'NAS')
SCAN_MINUTES = float(os.environ.get('SCAN_MINUTES', '30'))  # full rescan: also catches edited files' sizes and dates
CHECK_MINUTES = float(os.environ.get('CHECK_MINUTES', '1'))  # lists again only folders whose date modified changed
PORT = int(os.environ.get('PORT', '8765'))
# DSM on this NAS, as the container reaches it. Set: every search needs a NAS account (each person's own, as for
# \\NAS) and finds only the shared folders DSM lets that account open. Unset: no login, everyone finds everything.
DSM_URL = os.environ.get('DSM_URL', '').rstrip('/')
LOGIN_MINUTES = 30  # a login is checked with DSM this often: a permission change in DSM counts within this time
DSM_TIMEOUT = 4  # seconds per DSM call: under NasSearch.exe's 5 s, so the reason reaches it
# DSM's own certificate, mounted read-only: the server then speaks HTTPS, so NAS passwords never cross the LAN in
# plain text. Without it the server speaks HTTP, and refuses to start if DSM_URL asks for passwords.
CERT_DIR = os.environ.get('CERT_DIR', '/cert')
# Shared folders left out when a whole volume is mounted, comma-separated. homes holds every user's own folder,
# whose file names others on the LAN should not see. "" indexes them all.
SKIP_SHARES = {s.strip().lower() for s in os.environ.get('SKIP_SHARES', 'homes').split(',') if s.strip()}
LIMIT = 200

# Entries sorted by lowercase name; parent indexes dirs (relative to ROOT, like "/video/Movies").
# big is every lowercase name between \0 separators, starts[i] the offset of the \0 before entry i.
# size (-1 for folders) and mtime come from the scan, so a search reads no disk.
# cache holds what is worked out once per scan (per-user visibility, format counts) and goes with the index.
# Per folder, for the next check: rels is its path on disk under ROOT, dirtime its date modified in ns when it was
# listed (-1: list it again). listed counts the folders this scan read from disk.
Index = namedtuple('Index', 'names lows parent isdir dirs big starts scanned size mtime cache rels dirtime listed')


def pack(names, parent, isdir, dirs, scanned, size=None, mtime=None, rels=None, dirtime=None, listed=0):
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
                 array('d', map(mtime.__getitem__, order)), {}, rels, dirtime, listed)


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


def shown(name):
    """A name as JSON readers take it: bytes that are not UTF-8 on disk (an old GBK zip, say) become U+FFFD.
    The exe's JSON reader rejects the lone surrogates Python keeps for them, and with them the whole reply."""
    return name.encode('utf-8', 'surrogateescape').decode('utf-8', 'replace')


def stamp(rel):
    """A folder's date modified in ns: it changes when an entry in it is added, deleted or renamed. -1: unreadable."""
    try:
        return os.stat(ROOT + rel).st_mtime_ns
    except OSError:
        return -1


def build(old=None):
    """Every folder under ROOT, listed -> Index. With old, a quick check: a folder whose date modified is the same as
    when old listed it is not listed again, its entries are copied from old (as Everything's fast rescan)."""
    # shortcut: ~320 MB RAM per million entries (about double while the index changes). A check sees names added,
    # deleted or renamed, but an edited file's size and date, and a folder date set back by a copy tool (robocopy
    # /DCOPY:T, rsync -t), wait for the next full scan. Upgrade to inotify + SQLite past a few million files.
    same, kids, subs = {}, [], []
    if old:
        now = array('q', map(stamp, old.rels))  # one stat per folder, no listing
        if now == old.dirtime:
            return old._replace(scanned=time.time(), listed=0)
        at = {r: k for k, r in enumerate(old.rels)}
        same = {r: k for r, k in at.items() if now[k] == old.dirtime[k]}
        kids, subs = [[] for _ in old.rels], [[] for _ in old.rels]  # per folder: its entries, its folders' rels
        for i, p in enumerate(old.parent):
            kids[p].append(i)
        for r in old.rels[1:]:  # [0] is ROOT itself
            subs[at[r.rpartition('/')[0]]].append(r)
    names, parent, isdir, dirs, rels, dirtime = [], [], bytearray(), [], [], array('q')
    size, mtime = array('q'), array('d')
    listed = 0
    todo = ['']  # folders still to list, relative to ROOT
    while todo:
        rel = todo.pop()
        here = len(dirs)
        dirs.append(shown(unc_dir(rel)))  # only for showing and path terms: searches read no disk
        rels.append(rel)
        k = same.get(rel)
        if k is not None:
            dirtime.append(old.dirtime[k])
            for i in kids[k]:
                names.append(old.names[i])
                parent.append(here)
                isdir.append(old.isdir[i])
                size.append(old.size[i])
                mtime.append(old.mtime[i])
            todo += subs[k]
            continue
        listed += 1
        t = stamp(rel)  # before listing: a change made while listing then shows at the next check
        # changed within the last 2 s: a change later in the same clock tick would keep this date, so list it again
        dirtime.append(-1 if time.time_ns() - t < 2_000_000_000 else t)
        try:
            entries = os.scandir(ROOT + rel)
        except OSError:  # gone or unreadable since its folder was listed
            continue
        with entries:
            for e in entries:
                try:
                    folder = e.is_dir(follow_symlinks=False)  # like os.walk: never into a linked folder
                    if folder and (skip(e.name) or VOLUME.fullmatch(rel[1:]) and e.name.lower() in SKIP_SHARES):
                        continue  # the second: a skipped shared folder at the top of a mounted volume
                    st = e.stat(follow_symlinks=False)  # one stat per entry per scan, none per search
                except OSError:
                    continue
                if folder and rel == '' and VOLUME.fullmatch(e.name):
                    todo.append('/' + e.name)  # a mounted volume: list its shares, but it is no entry itself
                    continue
                names.append(shown(e.name))
                parent.append(here)
                isdir.append(folder)
                size.append(-1 if folder else st.st_size)
                mtime.append(st.st_mtime)
                if folder:
                    todo.append(rel + '/' + e.name)
    return pack(names, parent, isdir, dirs, time.time(), size, mtime, rels, dirtime, listed)


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


class Refused(Exception):
    """A request that cannot search; code is the HTTP status to answer, the message says why for the person."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


class DsmError(Exception):
    """DSM answered success: false, with this error code."""


# DSM's login error codes (DSM Login Web API guide) -> what to answer
LOGIN_ERRORS = {
    400: (401, 'NAS 账号或密码不对'),
    401: (403, '这个 NAS 账号已停用'),
    402: (403, '这个 NAS 账号没有 File Station 权限（控制面板 → 用户账号 → 应用程序）'),
    403: (403, '这个 NAS 账号开了两步验证，不能用来搜索'),
    404: (403, '这个 NAS 账号开了两步验证，不能用来搜索'),
    406: (403, '这个 NAS 账号开了两步验证，不能用来搜索'),
    407: (502, '群晖封锁了搜索服务的 IP：控制面板 → 安全性 → 保护 → 允许/封锁列表 里解除'),
    408: (403, '这个 NAS 账号的密码过期了，先在 DSM 里改密码'),
    409: (403, '这个 NAS 账号的密码过期了，先在 DSM 里改密码'),
    410: (403, '这个 NAS 账号要先在 DSM 里改密码'),
    411: (403, '这个 NAS 账号被锁定了'),
}
# DSM's certificate is self-signed or made for another name, and this hop never leaves the NAS
TLS = ssl.create_default_context()
TLS.check_hostname = False
TLS.verify_mode = ssl.CERT_NONE


def dsm(api, method, version, **params):
    """One DSM Web API call -> its data. DsmError when DSM says no, Refused(502) when it cannot be reached."""
    body = urlencode({'api': api, 'method': method, 'version': version, **params}).encode()  # POST: no password in a URL
    try:
        with urllib.request.urlopen(DSM_URL + '/webapi/entry.cgi', body, timeout=DSM_TIMEOUT, context=TLS) as r:
            reply = json.load(r)
    except (OSError, ValueError) as e:  # refused, timed out, or a page that is no DSM reply
        raise Refused(502, f'连不上群晖 DSM（{DSM_URL}）：{e}')
    if not reply.get('success'):
        raise DsmError((reply.get('error') or {}).get('code', 0))
    return reply.get('data') or {}


def dsm_shares(name, password):
    """The shared folders DSM lets this account open, as File Station lists them. Refused when it cannot log in."""
    try:
        sid = dsm('SYNO.API.Auth', 'login', 6, account=name, passwd=password, session='FileStation',
                  format='sid')['sid']
    except DsmError as e:
        raise Refused(*LOGIN_ERRORS.get(e.args[0], (502, f'群晖登录出错，错误码 {e.args[0]}')))
    try:
        return [s['name'] for s in dsm('SYNO.FileStation.List', 'list_share', 2, _sid=sid)['shares']]
    except DsmError as e:
        raise Refused(*LOGIN_ERRORS[402] if e.args[0] == 105 else (502, f'群晖列共享文件夹出错，错误码 {e.args[0]}'))
    finally:
        try:
            dsm('SYNO.API.Auth', 'logout', 6, session='FileStation', _sid=sid)
        except (DsmError, Refused):
            pass  # DSM ends the session itself later


def credentials(header):
    """An Authorization: Basic header -> (name, password); ('', '') when it is missing or broken."""
    try:
        name, _, password = base64.b64decode(header.removeprefix('Basic ')).decode().partition(':')
    except ValueError:  # not base64 or not UTF-8
        return '', ''
    return name, password


# Ages use time.monotonic(): a clock set back by NTP must not stretch how long a login is trusted
_checked = {}  # sha256 of an Authorization header -> (when DSM was asked, the shares it allows or (code, why) refused)
_failed = {}  # client IP -> when its recent logins were refused
_dsm_down = (float('-inf'), None)  # when DSM last could not be reached, and (code, why)
_dsm_lock = threading.Lock()


def login(header, ip):
    """The lowercase shared folders this request may search, None for all of them (DSM_URL unset); else Refused.
    A login is checked with DSM once per LOGIN_MINUTES, not once per keystroke."""
    global _dsm_down
    if not DSM_URL:
        return None
    name, password = credentials(header)
    if not name:
        raise Refused(401, '要用 NAS 账号登录')
    key = hashlib.sha256(header.encode()).digest()
    hit = _checked.get(key)
    if not hit or time.monotonic() - hit[0] > LOGIN_MINUTES * 60:
        with _dsm_lock:  # one DSM login at a time: keystrokes with a new password ask DSM once, not once each
            now = time.monotonic()
            hit = _checked.get(key)
            if not hit or now - hit[0] > LOGIN_MINUTES * 60:
                if now - _dsm_down[0] < 30:  # DSM just failed to answer: say so now, not after another timeout each
                    raise Refused(*_dsm_down[1])
                # DSM sees every login as coming from this container, and its auto block (10 fails in 5 minutes
                # by default) would then lock everyone out: stop one client well before that
                fails = _failed[ip] = [t for t in _failed.get(ip, []) if now - t < 300]
                if len(fails) >= 5:
                    raise Refused(429, '登录失败太多次，5 分钟后再试')
                try:
                    got = frozenset(s.lower() for s in dsm_shares(name, password))
                except Refused as e:
                    got = (e.code, str(e))  # not e: re-raising one exception grows its traceback, which holds the password
                    if e.code == 502:  # DSM unreachable: no fault of this login, so not remembered for it
                        _dsm_down = (now, got)
                        raise
                    fails.append(now)
                for k in [k for k, (t, _) in _checked.items() if now - t > LOGIN_MINUTES * 60]:
                    del _checked[k]
                hit = _checked[key] = (now, got)
    if isinstance(hit[1], tuple):
        raise Refused(*hit[1])
    return hit[1]


def tops(ix):
    """Each folder's shared folder, the first part of its path; '' for the NAS root."""
    if 'tops' not in ix.cache:
        ix.cache['tops'] = [d[1:].partition('/')[0] for d in ix.dirs]
    return ix.cache['tops']


def visible(ix, only):
    """One byte per entry, 1 where it lies in one of the shared folders in only; None when only is None."""
    # shortcut: whole shared folders only; a subfolder that DSM denies someone inside a share they may read still
    # shows its file names to them (they cannot open the files). Ask DSM per folder if subfolder permissions are used.
    if only is None:
        return None
    if only not in ix.cache:
        top = [t.lower() for t in tops(ix)]
        ok = set(compress(count(), map(only.__contains__, top)))
        vis = bytearray(map(ok.__contains__, ix.parent))
        root = set(compress(count(), map(not_, top)))
        for i in compress(count(), map(root.__contains__, ix.parent)):
            vis[i] = ix.lows[i] in only  # a shared folder itself, at the NAS root
        ix.cache[only] = vis
    return ix.cache[only]


def found(ix, only):
    """How many entries a search in the shared folders in only can find."""
    vis = visible(ix, only)
    return len(ix.names) if vis is None else vis.count(1)


def search(q, only=None):
    """Entries matching q, in the shared folders in only (None: all of them)."""
    ix = INDEX  # one snapshot: the scan thread swaps INDEX while searches run
    hits = match(parse(q), ix, range(len(ix.names)))
    vis = visible(ix, only)
    if vis is not None:  # after the search, not before: the str.find fast path needs every entry as candidates
        hits = list(compress(hits, map(vis.__getitem__, hits)))
    out = [{'name': ix.names[i], 'path': '\\\\' + NAS + ix.dirs[ix.parent[i]].replace('/', '\\'),
            'size': None if ix.size[i] < 0 else ix.size[i], 'mtime': ix.mtime[i]} for i in hits[:LIMIT]]
    return {'total': len(hits), 'indexed': found(ix, only), 'scanned': ix.scanned, 'results': out}


EXT_OK = re.compile(r'[^\s|<>";]+')  # usable inside an ext:a;b term


def exts(only=None):
    """Every file extension in the shared folders in only, most files first, as {ext, count}, lowercase like ext:
    matches."""
    ix = INDEX
    key = ('exts', only)
    if key not in ix.cache:  # one count per scan, not per request
        vis = visible(ix, only)
        keep = zip(ix.lows, ix.isdir, repeat(1) if vis is None else vis)
        n = Counter(low.rpartition('.')[2] for low, d, v in keep if v and not d and '.' in low)
        ix.cache[key] = [{'ext': e, 'count': c} for e, c in n.most_common() if EXT_OK.fullmatch(e)]
    return {'exts': ix.cache[key]}


def me(name, only):
    """For the web page: who is logged in, the shared folders they search, how much that is."""
    ix = INDEX
    shares = None if only is None else sorted((t for t in set(tops(ix)) if t and t.lower() in only), key=str.lower)
    return {'name': name, 'shares': shares, 'indexed': found(ix, only), 'total': len(ix.names),
            'scanned': ix.scanned, 'every': SCAN_MINUTES, 'check': CHECK_MINUTES, 'version': VERSION}


# Updates from the web page, like NasSearch.exe's own: main's nas_search.py, replacing this file and restarting.
# Anyone who may search may start one; it only ever installs a newer VERSION from this repo.
UPDATE_URL = 'https://raw.githubusercontent.com/Njza/everythingnas/main/nas_search.py'
SELF = os.path.abspath(__file__)  # docker-compose.yml mounts its folder writable, so it can be replaced
_update_lock = threading.Lock()


def newer(v):
    """Whether version v, like '1.2.0', comes after this file's VERSION."""
    return tuple(map(int, v.split('.'))) > tuple(map(int, VERSION.split('.')))


def latest():
    """main's nas_search.py -> (its VERSION, its text); ValueError or SyntaxError when it would not run."""
    with urllib.request.urlopen(UPDATE_URL, timeout=10) as r:  # default context: GitHub's certificate is checked
        src = r.read().decode()
    m = re.search(r"^VERSION = '(\d+(?:\.\d+)*)'", src, re.M)
    if not m:
        raise ValueError('no VERSION in it')
    compile(src, 'nas_search.py', 'exec')  # a file that would not even start is never installed
    return m[1], src


def update():
    """Installs main's nas_search.py when it is newer and restarts into it a second later.
    Returns the version it installs; None when this one is the newest."""
    with _update_lock:  # one install at a time
        try:
            version, src = latest()
        except (OSError, ValueError, SyntaxError) as e:
            raise Refused(502, f'取不到 GitHub 上的新版本：{e}')
        if not newer(version):
            return None
        try:
            with open(SELF + '.new', 'w', encoding='utf-8') as f:
                f.write(src)
            os.replace(SELF + '.new', SELF)  # the whole new file or the old one, never half of it
        except OSError as e:
            raise Refused(500, f'写不了 {SELF}：{e.strerror}。docker-compose.yml 里要用 - .:/app 挂载整个文件夹')
    threading.Timer(1, restart).start()  # once this request's reply is out
    return version


def restart():
    print('restarting into the new nas_search.py', flush=True)
    os.execv(sys.executable, sys.orig_argv)  # the same command, new code; the index is built again


def scan_forever():
    global INDEX
    full = float('-inf')  # when the last full scan started, time.monotonic()
    while True:
        t = time.monotonic()
        quick = t - full < SCAN_MINUTES * 60
        full = full if quick else t
        INDEX = build(INDEX if quick else None)
        for only in {got for _, got in list(_checked.values()) if isinstance(got, frozenset)} or {None}:
            exts(only)  # count the formats now, so the first format list after a scan does not wait for it
        if not quick or INDEX.listed:
            print(f'{"checked" if quick else "indexed"} {len(INDEX.names)} entries, listed {INDEX.listed} of '
                  f'{len(INDEX.dirs)} folders in {time.monotonic() - t:.1f}s', flush=True)
        time.sleep(CHECK_MINUTES * 60)


# The web page at /: search as the logged-in NAS account sees it, to check logins and permissions from a browser.
# Every name from the server goes in with textContent, never innerHTML: file names may hold < and &.
PAGE = r'''<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>NAS 搜索</title>
<style>
:root {
  color-scheme: light dark;
  /* cool paper and ink; teal for what you can act on, amber only for folders */
  --paper: #f3f5f2; --surface: #fff; --ink: #18201c; --muted: #5b6660; --line: #d9dfda;
  --accent: #0e6b5b; --on-accent: #fff; --folder: #c4870f; --bad: #b3261e; --hover: #ebf0ec; --chip: #e2eae5;
  font: 15px/1.6 "Segoe UI Variable Text", "Segoe UI", system-ui, "PingFang SC", "Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans SC", sans-serif;
  accent-color: var(--accent);
}
@media (prefers-color-scheme: dark) {
  :root {
    --paper: #131715; --surface: #1a1f1c; --ink: #e2e8e4; --muted: #96a19b; --line: #2b3330;
    --accent: #5cc2aa; --on-accent: #0b1f1a; --folder: #e2b04a; --bad: #ff8b80; --hover: #212824; --chip: #25312c;
  }
}
* { box-sizing: border-box; }
[hidden] { display: none !important; }
body { margin: 0; background: var(--paper); color: var(--ink); }
main { max-width: 1080px; margin: 0 auto; padding: 28px 24px 56px; }
:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.note { margin: 0; color: var(--muted); font-size: 14px; }
.note.error { color: var(--bad); }
button { font: inherit; font-size: 14px; color: var(--ink); background: var(--surface); border: 1px solid var(--line); border-radius: 8px; padding: 5px 14px; cursor: pointer; }
button:hover { border-color: var(--muted); }
button.primary { background: var(--accent); border-color: var(--accent); color: var(--on-accent); font-weight: 600; }
input { font: inherit; color: inherit; width: 100%; background: var(--surface); border: 1px solid var(--line); border-radius: 8px; padding: 9px 12px; }
input:focus-visible { outline-offset: 0; border-color: var(--accent); }

.top { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 6px 24px; padding-bottom: 18px; border-bottom: 1px solid var(--line); }
h1 { font-size: 22px; font-weight: 650; margin: 0; }
h1 small { font-size: 13px; font-weight: 400; color: var(--muted); margin-left: 6px; }
#update button { margin-left: 10px; }

.login { max-width: 380px; margin-top: 40px; }
.login h2 { font-size: 19px; font-weight: 650; margin: 0 0 4px; }
.login label { display: block; margin: 18px 0 6px; font-size: 14px; font-weight: 600; }
.login button { width: 100%; margin-top: 24px; padding: 10px; font-size: 15px; }
#refused { margin-top: 12px; min-height: 1.6em; }

.who { display: flex; align-items: center; justify-content: space-between; gap: 8px 16px; margin: 20px 0 14px; }
#who { margin: 0; display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
#who strong { font-size: 17px; font-weight: 650; margin-right: 4px; }
#logout { flex: none; }
.share { background: var(--chip); border-radius: 6px; padding: 0 8px; font-size: 14px; }
.count { color: var(--muted); font-size: 14px; margin-left: 6px; }
/* the search box is the page: large, and drawn in ink rather than a hairline */
#q { font-size: 19px; padding: 13px 16px; border: 2px solid var(--ink); border-radius: 10px; }
#q:focus-visible { border-color: var(--accent); outline: 3px solid color-mix(in srgb, var(--accent) 30%, transparent); }
.status { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 2px 24px; margin: 12px 2px 8px; }

.table-wrapper { overflow: auto; max-block-size: min(620px, 68vh); border: 1px solid var(--line); border-radius: 10px; background: var(--surface); container-type: inline-size; }
table { border-collapse: separate; border-spacing: 0; width: 100%; font-size: 14px; }
th, td { text-align: left; padding: 8px 14px; border-bottom: 1px solid var(--line); white-space: nowrap; }
tbody tr:last-child > * { border-bottom: 0; }
thead th { position: sticky; top: 0; z-index: 1; background: var(--surface); color: var(--muted); font-size: 13px; font-weight: 600; }
tbody th { font-weight: 500; }
tbody tr:hover { background: var(--hover); }
/* a page outline before a file, an amber folder before a folder */
tbody th::before { content: ""; display: inline-block; width: 10px; height: 13px; margin-right: 10px; vertical-align: -2px; border: 1.5px solid var(--muted); border-radius: 2px; }
tbody .dir th::before { width: 14px; height: 11px; vertical-align: -1px; border: 0; border-radius: 1px; background: var(--folder); clip-path: polygon(0 0, 42% 0, 52% 20%, 100% 20%, 100% 100%, 0 100%); }
.path { color: var(--muted); max-width: 30em; overflow: hidden; text-overflow: ellipsis; }
.num, .date { font-variant-numeric: tabular-nums; }
.num { text-align: right; }
.date, .dir .num { color: var(--muted); }

/* narrow (phone, or a narrow window): one entry per block, name on top */
@container (width < 600px) {
  table, thead, tbody, tr, th, td { display: block; }
  thead { display: none; }
  tbody tr { padding: 10px 14px; border-bottom: 1px solid var(--line); }
  tbody tr:last-child { border-bottom: 0; }
  tbody tr > * { padding: 0; border: 0; }
  tbody th { white-space: normal; overflow-wrap: anywhere; }
  .path { max-width: none; margin-top: 2px; }
  td.num, td.date { display: inline-block; font-size: 13px; }
  td.num { min-width: 5.5em; text-align: left; color: var(--muted); }
  td.date::before { content: "修改于 "; }
}
@media (max-width: 480px) {
  main { padding: 20px 16px 40px; }
  #q { font-size: 17px; }
}
</style>
</head>
<body>
<main>
  <header class="top">
    <h1>NAS 搜索 <small>NAS 端 v__VERSION__</small></h1>
    <p id="update" class="note"></p>
  </header>
  <section id="login" class="login" aria-labelledby="login-h" hidden>
    <h2 id="login-h">用 NAS 账号登录</h2>
    <p class="note">就是打开 \\NAS 时用的账号。只搜得到这个账号有权限的共享文件夹。</p>
    <form>
      <label for="user">NAS 账号</label>
      <input id="user" name="username" autocomplete="username" spellcheck="false" autocapitalize="none" autocorrect="off" required>
      <label for="pass">密码</label>
      <input id="pass" name="password" type="password" autocomplete="current-password" required>
      <button class="primary">登录</button>
      <p id="refused" class="note" role="alert"></p>
    </form>
  </section>
  <div id="app" hidden>
    <div class="who">
      <p id="who"></p>
      <button id="logout" type="button" hidden>退出</button>
    </div>
    <input id="q" type="search" enterkeyhint="search" aria-label="搜索文件名"
           placeholder="搜索文件名，写法同电脑上的搜索框">
    <div class="status">
      <p id="summary" class="note" aria-live="polite"></p>
      <p id="scan" class="note"></p>
    </div>
    <div class="table-wrapper" tabindex="0" role="region" aria-label="搜索结果">
      <table>
        <thead><tr><th scope="col">名称</th><th scope="col">位置</th><th scope="col" class="num">大小</th><th scope="col">修改日期</th></tr></thead>
        <tbody id="hits"></tbody>
      </table>
    </div>
  </div>
</main>
<script>
const $ = s => document.querySelector(s);
const num = n => n.toLocaleString('zh-CN');
const el = (tag, props, ...kids) => { const e = Object.assign(document.createElement(tag), props); e.append(...kids); return e; };

// The account logged in on this page, as an Authorization header. Only in memory: 退出, or closing the tab, forgets it.
let auth = '';

async function get(path, init = {}) {
  const r = await fetch(path, {...init, headers: auth ? {Authorization: auth} : {}});
  if (!r.ok) throw Object.assign(new Error(await r.text() || `出错了（HTTP ${r.status}）`), {status: r.status});
  return r.json();
}

function say(node, text, error = false) {
  node.textContent = text;
  node.classList.toggle('error', error);
}

let timer, seq = 0;
function find() {
  clearTimeout(timer);
  timer = setTimeout(async () => {
    const my = ++seq;  // only the newest search may fill the table
    try {
      const r = await get('/search?' + new URLSearchParams({q: $('#q').value}));
      if (my !== seq) return;
      say($('#summary'), `${num(r.total)} 个结果` + (r.total > r.results.length ? `，列出前 ${r.results.length} 个` : ''));
      $('#hits').replaceChildren(...r.results.map(h => el('tr', {className: h.size == null ? 'dir' : ''},
        el('th', {scope: 'row', textContent: h.name}),
        el('td', {className: 'path', textContent: h.path, title: h.path}),
        el('td', {className: 'num', textContent: size(h.size)}),
        el('td', {className: 'date', textContent: time(h.mtime)}))));
    } catch (err) {
      if (my === seq) say($('#summary'), err.message, true);
    }
  }, 150);
}

function size(b) {
  if (b == null) return '文件夹';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  for (; b >= 1024 && i < units.length - 1; i++) b /= 1024;
  return (i ? b.toFixed(1) : b) + ' ' + units[i];
}

$('#q').addEventListener('input', find);
const time = s => new Date(s * 1000).toLocaleString('zh-CN');

async function start() {
  let m;
  try {
    m = await get('/me');
  } catch (err) {
    const tried = auth;
    auth = '';  // refused (wrong password, two-step verification...): back to the form, to try another account
    return showLogin(tried || err.status !== 401 ? err.message : '');
  }
  $('#login').hidden = true;
  $('#app').hidden = false;
  $('#logout').hidden = m.shares === null;
  $('#who').replaceChildren(...(m.shares === null
    ? [`不用登录：谁都能搜到全部 ${num(m.total)} 项。`]
    : [el('strong', {textContent: m.name}), '能搜',
       ...(m.shares.length ? m.shares.map(s => el('span', {className: 'share', textContent: s})) : ['（没有共享文件夹）']),
       el('span', {className: 'count', textContent: `共 ${num(m.indexed)} 项，NAS 上一共 ${num(m.total)} 项`})]));
  say($('#scan'), m.scanned
    ? `索引 ${time(m.scanned)} 更新。每 ${m.check} 分钟查新建、删除和改名的文件，每 ${m.every} 分钟全部重扫。`
    : '第一次扫描进行中，稍后刷新。');
  $('#q').focus();
  find();
  get('/update').then(u => {
    if (!u.newer) return say($('#update'), '已是最新版本');
    say($('#update'), `有新版本 v${u.latest}`);
    $('#update').append(el('button', {type: 'button', className: 'primary', textContent: '更新并重启', onclick: () => install(u.latest)}));
  }, err => say($('#update'), err.message));
}

function showLogin(why) {
  $('#app').hidden = true;
  $('#login').hidden = false;
  say($('#refused'), why, true);
  $('#pass').value = '';
  $('#user').select();
}

$('#login form').addEventListener('submit', e => {
  e.preventDefault();
  const utf8 = new TextEncoder().encode($('#user').value + ':' + $('#pass').value);  // like NasSearch.exe
  auth = 'Basic ' + btoa(String.fromCharCode(...utf8));
  say($('#refused'), '登录中…');
  start();
});

$('#logout').addEventListener('click', () => {
  auth = '';
  clearTimeout(timer);
  seq++;  // a search still on its way must not fill the table for the next person
  $('#q').value = '';
  $('#hits').replaceChildren();
  say($('#summary'), '');
  say($('#update'), '');
  showLogin('');
});

start();

async function install(v) {
  say($('#update'), `正在更新到 v${v}…`);
  try {
    if ((await get('/update', {method: 'POST'})).updating === null) return say($('#update'), '已是最新版本。');
  } catch (err) {
    return say($('#update'), err.message, true);
  }
  say($('#update'), `已装上 v${v}，服务器正在重启，之后会重新建索引…`);
  for (;;) {  // wait for the restarted server, then show the new version
    await new Promise(ok => setTimeout(ok, 2000));
    try { if ((await get('/me')).version === v) return location.reload(); } catch {}
  }
}
</script>
</body>
</html>
'''.replace('__VERSION__', VERSION)


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'  # keep-alive: a search reuses the connection instead of opening one per keystroke

    def do_GET(self):
        u = urlsplit(self.path)
        if u.path not in ('/', '/search', '/exts', '/me', '/update'):
            return self.reply(404)
        if u.path == '/':  # the page holds no names: it logs in with its own form
            return self.reply(200, PAGE, 'text/html; charset=utf-8')
        auth = self.headers.get('Authorization', '')
        try:
            only = login(auth, self.client_address[0])
        except Refused as e:
            return self.reply(e.code, str(e), 'text/plain; charset=utf-8')
        if u.path == '/update':
            try:
                v = latest()[0]
            except (OSError, ValueError, SyntaxError) as e:
                return self.reply(502, f'查不到 GitHub 上的版本：{e}', 'text/plain; charset=utf-8')
            return self.reply(200, json.dumps({'version': VERSION, 'latest': v, 'newer': newer(v)}))
        q = parse_qs(u.query).get('q', [''])[0]
        data = (search(q, only) if u.path == '/search' else exts(only) if u.path == '/exts'
                else me(credentials(auth)[0], only))
        self.reply(200, json.dumps(data))

    def do_POST(self):  # POST /update: install a newer nas_search.py from main and restart into it
        self.rfile.read(int(self.headers.get('Content-Length') or 0))  # keep-alive: the next request starts after it
        if urlsplit(self.path).path != '/update':
            return self.reply(404)
        try:
            login(self.headers.get('Authorization', ''), self.client_address[0])
            data = {'updating': update()}
        except Refused as e:
            return self.reply(e.code, str(e), 'text/plain; charset=utf-8')
        self.reply(200, json.dumps(data))

    def reply(self, code, body='', ctype='application/json'):
        body = body.encode()
        # No WWW-Authenticate on a 401: the browser's own login box keeps an account until the browser closes, with
        # no way to log out, and stays stuck on one DSM refuses (403). The page and NasSearch.exe send the account.
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))  # HTTP/1.1: without it the client waits for a body
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def make_server(port):
    """The server, on HTTPS with DSM's certificate when it is mounted; DSM logins never go without it."""
    server = ThreadingHTTPServer(('', port), Handler)
    chain = next((f'{CERT_DIR}/{f}' for f in ('fullchain.pem', 'cert.pem') if os.path.exists(f'{CERT_DIR}/{f}')), None)
    if chain:
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(chain, f'{CERT_DIR}/privkey.pem')
        # handshake in each request's thread, not in accept(): one client that never speaks would block all others
        server.socket = tls.wrap_socket(server.socket, server_side=True, do_handshake_on_connect=False)
    elif DSM_URL:
        server.server_close()
        raise SystemExit(f'DSM_URL is set but there is no certificate in {CERT_DIR}: NAS passwords would cross the '
                         'LAN in plain text. Mount DSM\'s certificate there (see docker-compose.yml).')
    return server


if __name__ == '__main__':
    server = make_server(PORT)
    threading.Thread(target=scan_forever, daemon=True).start()
    scheme = 'https' if isinstance(server.socket, ssl.SSLSocket) else 'http'
    logins = f'NAS account logins via {DSM_URL}' if DSM_URL else 'no logins (DSM_URL unset)'
    print(f'nas-search {VERSION} serving {scheme}://0.0.0.0:{PORT}  root={ROOT}  {logins}', flush=True)
    server.serve_forever()
