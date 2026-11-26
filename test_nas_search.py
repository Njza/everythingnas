import base64
import json
import os
import random
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import unittest
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from unittest import mock

TMP = tempfile.mkdtemp()
os.environ.update(ROOT=TMP, NAS_NAME='DS')
import nas_search as ns  # noqa: E402  (reads ROOT at import)

FILES = ['video/Movies/Alien.MKV', 'video/Movies/@eaDir/Alien.MKV/SYNOVIDEO_VIDEO_SCREENSHOT.jpg',
         'video/#recycle/old.mkv', 'photo/2024/猫咪.jpg', 'photo/2024/dog.jpg', 'docs/alien-notes.txt', 'docs/gone.txt',
         'docs/[2024] 年度 报告.docx', 'docs/报告 年度.xlsx']


class SearchTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        for p in FILES:
            f = Path(TMP, p)
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_text('x')
        ns.INDEX = ns.build()

    def names(self, q):
        return [r['name'] for r in ns.search(q)['results']]

    def test_substring_ignores_case_and_sorts_by_name(self):
        self.assertEqual(self.names('ALIEN'), ['alien-notes.txt', 'Alien.MKV'])

    def test_synology_metadata_and_recycle_bin_skipped(self):
        everything = self.names('')
        self.assertNotIn('SYNOVIDEO_VIDEO_SCREENSHOT.jpg', everything)
        self.assertNotIn('old.mkv', everything)
        self.assertNotIn('@eaDir', everything)

    def test_wildcard_matches_whole_name(self):
        self.assertEqual(self.names('*.jpg'), ['dog.jpg', '猫咪.jpg'])
        self.assertEqual(self.names('*.mk'), [])

    def test_and_not(self):
        self.assertEqual(self.names('jpg !dog'), ['猫咪.jpg'])
        self.assertEqual(self.names('alien txt'), ['alien-notes.txt'])

    def test_path_term(self):
        self.assertEqual(self.names('movies\\'), ['Alien.MKV'])
        self.assertEqual(self.names('photo/ 猫'), ['猫咪.jpg'])

    def test_unc_path_size_and_folders(self):
        r = {x['name']: x for x in ns.search('')['results']}
        self.assertEqual(r['Alien.MKV']['path'], '\\\\DS\\video\\Movies')
        self.assertEqual(r['Alien.MKV']['size'], 1)
        self.assertIsNone(r['Movies']['size'])
        self.assertEqual(r['video']['path'], '\\\\DS')

    def test_size_and_date_come_from_the_scan(self):
        # a search reads no disk: a file deleted since the scan stays listed, as scanned, until the next scan
        os.remove(Path(TMP, 'docs/gone.txt'))
        with mock.patch.object(os, 'stat', side_effect=AssertionError('search touched the disk')):
            [r] = ns.search('gone')['results']
        self.assertEqual(r['size'], 1)
        self.assertGreater(r['mtime'], 0)

    def test_or_binds_tighter_than_and(self):
        self.assertEqual(self.names('jpg dog|txt'), ['dog.jpg'])
        self.assertEqual(self.names('jpg dog | txt'), ['dog.jpg'])

    def test_groups(self):
        self.assertEqual(self.names('<dog|alien> !txt'), ['Alien.MKV', 'dog.jpg'])
        self.assertEqual(self.names('jpg !<dog|alien>'), ['猫咪.jpg'])

    def test_quotes_keep_spaces_and_operators(self):
        self.assertEqual(self.names('年度 报告'), ['[2024] 年度 报告.docx', '报告 年度.xlsx'])
        self.assertEqual(self.names('"年度 报告"'), ['[2024] 年度 报告.docx'])
        self.assertEqual(self.names('"dog|txt"'), [])

    def test_brackets_are_literal_in_wildcards(self):
        self.assertEqual(self.names('[2024]*'), ['[2024] 年度 报告.docx'])  # fnmatch would also match "2024"

    def test_modifiers(self):
        self.assertEqual(self.names('ext:docx;XLSX'), ['[2024] 年度 报告.docx', '报告 年度.xlsx'])
        self.assertEqual(self.names('folder:'), ['2024', 'docs', 'Movies', 'photo', 'video'])
        self.assertEqual(self.names('file:movies'), [])
        self.assertEqual(self.names('case:alien'), ['alien-notes.txt'])
        self.assertEqual(self.names(r'regex:^dog\.'), ['dog.jpg'])
        self.assertEqual(self.names(r'regex:^\D+\.JPG$'), ['dog.jpg', '猫咪.jpg'])
        self.assertEqual(self.names('regex:dog('), [])  # half-typed pattern, no crash
        self.assertEqual(self.names('path:movies'), ['Alien.MKV', 'Movies'])

    def test_exts_counts_files_most_first(self):
        e = ns.exts()['exts']
        self.assertEqual({x['ext']: x['count'] for x in e}, {'jpg': 2, 'txt': 2, 'mkv': 1, 'docx': 1, 'xlsx': 1})
        self.assertEqual([x['count'] for x in e], sorted((x['count'] for x in e), reverse=True))
        self.assertIs(ns.exts()['exts'], e)  # counted once per scan

    def test_path_terms(self):
        self.assertEqual(self.names('movies\\ali'), ['Alien.MKV'])
        self.assertEqual(self.names('\\alien'), ['alien-notes.txt', 'Alien.MKV'])
        self.assertEqual(self.names('\\\\ds\\video\\*'), ['Movies'])  # * stops at a separator
        self.assertEqual(self.names('\\\\ds\\video\\**'), ['Alien.MKV', 'Movies'])

    def test_fast_path_matches_full_scan(self):
        qs = ['alien', '*.jpg', 'dog*', 'ext:jpg;txt', 'a|o', '[2024]*', '报告', 'case:Alien', 'zzz']
        fast = [self.names(q) for q in qs]
        with mock.patch.object(ns, 'find_all', lambda ix, s: None):
            self.assertEqual([self.names(q) for q in qs], fast)

    def test_chinese_dates_and_full_width_symbols_are_plain_text(self):
        named = ['2026.11.1 会议纪要.docx', '2026.11.01 报价单.xlsx', '2026.11.15 发票.pdf', '2026-11-01 合同.pdf',
                 '2026年11月1日 通知.docx', '《三体》全集.pdf', '合同、发票.zip', '“终版”方案！.pptx', 'ＡＢＣ.txt']
        ix = ns.pack(named, [0] * len(named), bytearray(len(named)), [''], 0)

        def find(q):
            return [ix.names[i] for i in ns.match(ns.parse(q), ix, range(len(named)))]
        self.assertEqual(find('2026.11'), ['2026.11.01 报价单.xlsx', '2026.11.1 会议纪要.docx', '2026.11.15 发票.pdf'])
        self.assertEqual(find('2026.11.1'), ['2026.11.1 会议纪要.docx', '2026.11.15 发票.pdf'])  # substring, as in Everything
        self.assertEqual(find('"2026.11.1 "'), ['2026.11.1 会议纪要.docx'])
        self.assertEqual(find('*2026?11?01*'), ['2026-11-01 合同.pdf', '2026.11.01 报价单.xlsx'])
        self.assertEqual(find('11月1日'), ['2026年11月1日 通知.docx'])
        self.assertEqual(find('《三体》'), ['《三体》全集.pdf'])  # full-width 《》 are not < > groups
        self.assertEqual(find('合同、'), ['合同、发票.zip'])  # 、 is not a path separator
        self.assertEqual(find('“终版”方案！'), ['“终版”方案！.pptx'])
        self.assertEqual(find('合同　pdf'), ['2026-11-01 合同.pdf'])  # IME full-width space = AND
        self.assertEqual(find('ａｂｃ'), ['ＡＢＣ.txt'])
        self.assertEqual(find('abc'), [])  # no full-width/half-width folding, as in Everything

    def test_any_substring_finds_exactly_the_names_containing_it(self):
        # big enough for all three find_all paths: str.find hopping, the 1/64 sample, the hit cap
        rnd = random.Random(7)
        parts = ['2026.11.1', '2026.11.01', '2026-11-1', '2026年11月1日', '合同', '报价单', '会议纪要', 'Final', '_', ' ',
                 '（终版）', 'v2', 'ǅ', 'İ']
        names = [''.join(rnd.choices(parts, k=3)) + rnd.choice(['.docx', '.xlsx', '.pdf', '']) for _ in range(3000)]
        ix = ns.pack(names, [0] * len(names), bytearray(len(names)), [''], 0)
        for n in rnd.sample(names, 300):
            a = rnd.randrange(len(n))
            s = n[a:a + rnd.randint(1, 12)]
            want = [i for i, x in enumerate(ix.lows) if s.lower() in x]
            self.assertEqual(list(ns.match(ns.parse('"%s"' % s), ix, range(len(names)))), want, s)

    def test_odd_queries_do_not_crash(self):
        for q in ['|', '<', '>', '!', '"', 'a||b', '<<a', 'a>>', '! <', 'ext:', '*', '**\\', 'file:folder:x']:
            ns.search(q)
        with mock.patch.object(ns, 'INDEX', ns.pack([], [], bytearray(), [''], 0)):  # before the first scan
            self.assertEqual(ns.search('alien|*.jpg ext:pdf')['total'], 0)


class WholeVolumeTest(unittest.TestCase):
    """The default docker-compose.yml mounts all of /volume1 as /data/volume1: shares are its top folders."""

    def test_volume_folder_is_dropped_from_paths(self):
        root = tempfile.mkdtemp()
        for p in ['volume1/video/Movies/Alien.mkv', 'volume1/@appstore/pkg/app.bin', 'volume1/docs/#recycle/old.txt',
                  'volume2/photo/猫.jpg']:
            Path(root, p).parent.mkdir(parents=True, exist_ok=True)
            Path(root, p).write_text('x')
        with mock.patch.object(ns, 'ROOT', root), mock.patch.object(ns, 'INDEX', None):
            ns.INDEX = ns.build()
            r = {x['name']: x['path'] for x in ns.search('')['results']}
            self.assertEqual(r['Alien.mkv'], '\\\\DS\\video\\Movies')
            self.assertEqual(r['猫.jpg'], '\\\\DS\\photo')
            self.assertEqual(r['video'], '\\\\DS')  # a shared folder, at the NAS root like \\DS\video
            self.assertNotIn('volume1', r)
            self.assertNotIn('app.bin', r)  # Synology's @ folders, also at the volume's top
            self.assertNotIn('old.txt', r)
            self.assertEqual([x['name'] for x in ns.search('video\\ alien')['results']], ['Alien.mkv'])

    def test_homes_left_out_of_a_whole_volume(self):
        root = tempfile.mkdtemp()
        for p in ['volume1/homes/mei/diary.txt', 'volume1/docs/homes/plan.txt', 'volume1/docs/report.txt']:
            Path(root, p).parent.mkdir(parents=True, exist_ok=True)
            Path(root, p).write_text('x')
        with mock.patch.object(ns, 'ROOT', root), mock.patch.object(ns, 'INDEX', None):
            ns.INDEX = ns.build()
            names = [x['name'] for x in ns.search('')['results']]
            self.assertNotIn('diary.txt', names)  # every user's own folder, at the volume's top
            self.assertIn('plan.txt', names)  # a folder called homes inside a share is ordinary
            self.assertIn('report.txt', names)
            with mock.patch.object(ns, 'SKIP_SHARES', set()):  # SKIP_SHARES: "" indexes homes too
                ns.INDEX = ns.build()
                self.assertIn('diary.txt', [x['name'] for x in ns.search('')['results']])

    def test_a_homes_share_mounted_on_its_own_is_searched(self):
        root = tempfile.mkdtemp()
        Path(root, 'homes/mei').mkdir(parents=True)
        Path(root, 'homes/mei/diary.txt').write_text('x')
        with mock.patch.object(ns, 'ROOT', root), mock.patch.object(ns, 'INDEX', None):
            ns.INDEX = ns.build()
            self.assertEqual([x['path'] for x in ns.search('diary')['results']], ['\\\\DS\\homes\\mei'])


class FakeDsm(ns.BaseHTTPRequestHandler):
    """DSM's /webapi/entry.cgi: login, list_share, logout, for a few accounts."""
    accounts = {'婉': 'pw', '梅': 'a:b', '2fa': 'x', 'nofs': 'x'}
    shares = {'婉': ['婉', '监控', 'home'], '梅': ['梅'], 'nofs': []}
    calls = []

    def do_POST(self):
        p = {k: v[0] for k, v in ns.parse_qs(self.rfile.read(int(self.headers['Content-Length'])).decode()).items()}
        self.calls.append((p['method'], p.get('account') or p.get('_sid')))
        if p['method'] == 'login':
            ok = self.accounts.get(p['account']) == p['passwd'] and p['session'] == 'FileStation'
            reply = ({'success': False, 'error': {'code': 403}} if ok and p['account'] == '2fa' else
                     {'success': True, 'data': {'sid': p['account']}} if ok else
                     {'success': False, 'error': {'code': 400}})
        elif p['method'] == 'list_share':
            reply = ({'success': False, 'error': {'code': 105}} if p['_sid'] == 'nofs' else
                     {'success': True, 'data': {'offset': 0, 'total': 1,
                                                'shares': [{'isdir': True, 'name': s, 'path': '/' + s}
                                                           for s in self.shares[p['_sid']]]}})
        else:
            reply = {'success': True}
        body = json.dumps(reply).encode()
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def serve(handler):
    server = ns.ThreadingHTTPServer(('127.0.0.1', 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f'http://127.0.0.1:{server.server_port}'


class DsmLoginTest(unittest.TestCase):
    """DSM_URL set: each person logs in with their NAS account and finds only the shares DSM lets them open."""

    @classmethod
    def setUpClass(cls):
        cls.root = tempfile.mkdtemp()
        for p in ['volume1/婉/顺德25/报检.docx', 'volume1/婉/合同.pdf', 'volume1/梅/合同.pdf', 'volume1/梅/私人.xlsx',
                  'volume1/监控/2025.mp4']:
            Path(cls.root, p).parent.mkdir(parents=True, exist_ok=True)
            Path(cls.root, p).write_text('x')
        with mock.patch.object(ns, 'ROOT', cls.root):
            cls.ix = ns.build()
        cls.dsm, cls.dsm_url = serve(FakeDsm)

    @classmethod
    def tearDownClass(cls):
        cls.dsm.shutdown()
        cls.dsm.server_close()

    def setUp(self):
        FakeDsm.calls.clear()
        for name, value in [('INDEX', self.ix), ('DSM_URL', self.dsm_url), ('_checked', {}), ('_failed', {}),
                            ('_dsm_down', (float('-inf'), None))]:
            patch = mock.patch.object(ns, name, value)
            patch.start()
            self.addCleanup(patch.stop)

    def login(self, who, ip='10.0.0.2'):
        return ns.login(basic(who), ip)

    def test_dsm_says_which_shares_and_is_asked_once(self):
        self.assertEqual(self.login('婉:pw'), {'婉', '监控', 'home'})
        self.assertEqual(self.login('梅:a:b'), {'梅'})  # the password may hold a ':'
        self.login('婉:pw')
        self.assertEqual(FakeDsm.calls, [('login', '婉'), ('list_share', '婉'), ('logout', '婉'),
                                         ('login', '梅'), ('list_share', '梅'), ('logout', '梅')])
        with mock.patch.object(ns.time, 'monotonic', return_value=ns.time.monotonic() + 31 * 60):  # LOGIN_MINUTES later
            self.login('婉:pw')
        self.assertEqual(len(FakeDsm.calls), 9)

    def test_refusals(self):
        for who, code in [('婉:wrong', 401), ('nobody:pw', 401), ('2fa:x', 403), ('nofs:x', 403)]:
            with self.assertRaises(ns.Refused, msg=who) as e:
                self.login(who)
            self.assertEqual(e.exception.code, code, who)
        for junk in ['', 'Basic', 'Basic %%%', 'Bearer abc']:
            with self.assertRaises(ns.Refused) as e:
                ns.login(junk, '10.0.0.2')
            self.assertEqual(e.exception.code, 401)
        n = len(FakeDsm.calls)
        with self.assertRaises(ns.Refused):
            self.login('婉:wrong')  # a wrong password is not sent to DSM again
        self.assertEqual(len(FakeDsm.calls), n)
        # kept as (code, why), not as the exception: re-raising one grows its traceback, whose frames hold the password
        self.assertTrue(all(isinstance(got, (tuple, frozenset)) for _, got in ns._checked.values()))

    def test_too_many_fails_stop_before_dsm_blocks_this_container(self):
        for i in range(5):
            with self.assertRaises(ns.Refused):
                self.login(f'婉:wrong{i}')
        n = len(FakeDsm.calls)
        with self.assertRaises(ns.Refused) as e:
            self.login('婉:pw')
        self.assertEqual((e.exception.code, len(FakeDsm.calls)), (429, n))
        self.assertEqual(self.login('婉:pw', ip='10.0.0.3'), {'婉', '监控', 'home'})  # other PCs go on

    def test_unreachable_dsm_answers_everyone_at_once_for_30_seconds(self):
        silent = socket.socket()  # accepts connections, never answers
        silent.bind(('127.0.0.1', 0))
        silent.listen()
        self.addCleanup(silent.close)
        with mock.patch.object(ns, 'DSM_URL', f'http://127.0.0.1:{silent.getsockname()[1]}'), \
                mock.patch.object(ns, 'DSM_TIMEOUT', 0.3):
            start = ns.time.monotonic()
            for who in ['婉:pw', '梅:a:b', '婉:pw']:  # only the first waits for the timeout
                with self.assertRaises(ns.Refused) as e:
                    self.login(who)
                self.assertEqual(e.exception.code, 502)
            self.assertLess(ns.time.monotonic() - start, 0.6)
        self.assertIn('连不上群晖', str(e.exception))
        self.assertEqual(ns._failed['10.0.0.2'], [])  # no fault of the logins: no step towards the 5-fail stop
        with mock.patch.object(ns.time, 'monotonic', return_value=ns.time.monotonic() + 31):
            self.assertEqual(self.login('婉:pw'), {'婉', '监控', 'home'})

    def test_each_login_finds_only_its_shares(self):
        def found(who, q=''):
            hits = ns.search(q, self.login(who))['results']
            return sorted(r['path'].removeprefix('\\\\DS') + '\\' + r['name'] for r in hits)
        self.assertEqual(found('婉:pw', '合同'), ['\\婉\\合同.pdf'])
        self.assertEqual(found('梅:a:b', '合同'), ['\\梅\\合同.pdf'])
        self.assertEqual(found('婉:pw'), ['\\婉', '\\婉\\合同.pdf', '\\婉\\顺德25', '\\婉\\顺德25\\报检.docx',
                                         '\\监控', '\\监控\\2025.mp4'])
        self.assertEqual(found('婉:pw', '\\梅\\'), [])  # path terms too
        self.assertEqual(ns.search('', self.login('婉:pw'))['indexed'], 6)
        self.assertEqual({x['ext']: x['count'] for x in ns.exts(self.login('梅:a:b'))['exts']}, {'pdf': 1, 'xlsx': 1})
        self.assertEqual(ns.me('婉', self.login('婉:pw'))['shares'], ['婉', '监控'])  # DSM's home is not indexed

    def test_http(self):
        server, base = serve(ns.Handler)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)

        def get(path, who=None):
            req = urllib.request.Request(base + path, headers={'Authorization': basic(who)} if who else {})
            try:
                with urllib.request.urlopen(req) as r:
                    return r.status, r.read().decode()
            except urllib.error.HTTPError as e:
                with e:
                    return e.code, e.read().decode()
        q = '/search?q=' + urllib.parse.quote('合同')
        self.assertEqual(get(q)[0], 401)
        self.assertEqual(get(q, '婉:wrong'), (401, 'NAS 账号或密码不对'))
        self.assertEqual(get(q, '2fa:x'), (403, '这个 NAS 账号开了两步验证，不能用来搜索'))
        code, body = get(q, '梅:a:b')
        self.assertEqual([r['path'] for r in json.loads(body)['results']], ['\\\\DS\\梅'])
        self.assertIn('<title>NAS 搜索</title>', get('/', '梅:a:b')[1])
        self.assertEqual(json.loads(get('/me', '梅:a:b')[1])['shares'], ['梅'])
        self.assertEqual(get('/nope', '梅:a:b')[0], 404)
        with mock.patch.object(ns, 'DSM_URL', ''):  # no DSM: no login, everything
            code, body = get(q)
            self.assertEqual(json.loads(body)['total'], 2)
            self.assertIsNone(json.loads(get('/me')[1])['shares'])


class NotUtf8NameTest(unittest.TestCase):
    def test_names_that_are_not_utf8_on_disk_still_make_json_the_exe_reads(self):
        gbk = ('合同'.encode('gbk') + b'.pdf').decode('utf-8', 'surrogateescape')  # as read from a Linux disk
        self.assertIn('\\udcba', json.dumps(gbk))  # a lone surrogate: the exe's JSON reader rejects the reply
        self.assertNotIn('\\ud', json.dumps(ns.shown(gbk)))
        self.assertEqual(ns.shown('合同.pdf'), '合同.pdf')
        root = tempfile.mkdtemp()
        try:
            open(os.fsencode(root) + b'/' + '合同'.encode('gbk') + b'.pdf', 'w').close()
        except (OSError, ValueError):  # macOS refuses the name, Windows cannot even spell it as a path
            self.skipTest('this system takes UTF-8 names only; Linux, like the NAS, runs the rest')
        with mock.patch.object(ns, 'ROOT', root):
            self.assertNotIn('\\ud', json.dumps(ns.build().names))


class HttpsTest(unittest.TestCase):
    """With DSM's certificate mounted the server speaks HTTPS; NAS passwords never go over plain HTTP."""

    def setUp(self):
        if not shutil.which('openssl'):
            self.skipTest('needs openssl to make a certificate')
        self.cert = tempfile.mkdtemp()
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=nas',
                        '-keyout', f'{self.cert}/privkey.pem', '-out', f'{self.cert}/cert.pem'],
                       check=True, capture_output=True)

    def test_https_and_a_client_that_never_speaks_blocks_nobody(self):
        with mock.patch.object(ns, 'CERT_DIR', self.cert), mock.patch.object(ns, 'DSM_URL', ''):
            server = ns.make_server(0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        port = server.server_address[1]
        silent = socket.create_connection(('127.0.0.1', port))  # connects, never starts the TLS handshake
        self.addCleanup(silent.close)
        tls = ssl.create_default_context()
        tls.check_hostname, tls.verify_mode = False, ssl.CERT_NONE  # DSM's certificate is self-signed too
        with urllib.request.urlopen(f'https://127.0.0.1:{port}/exts', context=tls, timeout=3) as r:
            self.assertEqual(r.status, 200)

    def test_no_logins_without_a_certificate(self):
        with mock.patch.object(ns, 'CERT_DIR', tempfile.mkdtemp()):
            with mock.patch.object(ns, 'DSM_URL', 'https://host.docker.internal:5001'), self.assertRaises(SystemExit):
                ns.make_server(0)
            with mock.patch.object(ns, 'DSM_URL', ''):
                ns.make_server(0).server_close()  # no logins, no passwords: plain HTTP is fine


def basic(credentials):
    return 'Basic ' + base64.b64encode(credentials.encode()).decode()  # UTF-8, as NasSearch.exe sends it


if __name__ == '__main__':
    unittest.main()
