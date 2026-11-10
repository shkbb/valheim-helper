"""Texts of the mod (Lang.T / Lang.F with a literal) that have no English translation yet, as JSON on stdout.

usage: missing.py <mod source root>
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wrap import lex  # noqa: E402

here = os.path.dirname(os.path.abspath(__file__))
known = set(json.load(open(os.path.join(here, 'keys.json'), encoding='utf-8')))
extra = os.path.join(here, 'en_extra.json')
if os.path.exists(extra):
    known.update(json.load(open(extra, encoding='utf-8')).keys())


def keys_in(src):
    toks, _ = lex(src)
    sig = [t for t in toks if t.kind != 'ws']
    for i in range(len(sig) - 4):
        if sig[i].text == 'Lang' and sig[i + 1].text == '.' and sig[i + 2].text in ('T', 'F') and sig[i + 3].text == '(':
            j = i + 4
            parts = []
            while j < len(sig) and sig[j].kind == 'str':
                parts.append(sig[j].text[1:-1])
                if j + 1 < len(sig) and sig[j + 1].text == '+' and j + 2 < len(sig) and sig[j + 2].kind == 'str':
                    j += 2
                    continue
                break
            if parts:
                yield ''.join(parts)


missing = []
for dp, dn, fn in os.walk(sys.argv[1]):
    if os.sep + 'obj' in dp or os.sep + 'bin' in dp:
        continue
    for f in fn:
        if f.endswith('.cs') and not f.startswith('LangEn'):
            for key in keys_in(open(os.path.join(dp, f), encoding='utf-8-sig').read()):
                if key not in known and key not in missing:
                    missing.append(key)
sys.stdout.reconfigure(encoding='utf-8')
print(json.dumps(missing, ensure_ascii=False, indent=0))
print(len(missing), file=sys.stderr)
