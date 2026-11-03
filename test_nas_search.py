import os
import random
import tempfile
import unittest
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

    def test_file_deleted_since_scan_is_dropped(self):
        os.remove(Path(TMP, 'docs/gone.txt'))
        self.assertEqual(self.names('gone'), [])

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


if __name__ == '__main__':
    unittest.main()
