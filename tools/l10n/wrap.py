"""Wraps every Ukrainian string of the mod's C# code into Lang.T("...") / Lang.F("...", args), so the menu can be shown
in English. Adjacent literals joined with + become one key; interpolated strings become format strings. Writes the keys
(C# literal text, escapes kept) to keys.json in the order they appear.

usage: wrap.py <root> <keys.json> [--dry]
"""
import json
import os
import re
import sys

CYR = re.compile('[А-Яа-яІіЇїЄєҐґ]')
SKIP_FILES = {os.path.join('Wiki', 'WikiModel.cs'), os.path.join('Wiki', 'WikiPack.cs'), os.path.join('Core', 'Lang.cs'),
              os.path.join('Core', 'LangEn.cs')}


class Tok:
    __slots__ = ('kind', 'text', 'parts')

    def __init__(self, kind, text, parts=None):
        self.kind = kind      # ws, str, istr, vstr, chr, word, punct
        self.text = text      # original source text
        self.parts = parts    # istr: list of ('lit', text) / ('hole', expr, spec)


def lex(s, i=0, stop=None):
    """Tokens of s from i; with stop (a set of chars) returns at an unmatched stop char at depth 0."""
    toks = []
    n = len(s)
    depth = 0
    while i < n:
        c = s[i]
        if stop is not None and depth == 0 and c in stop:
            return toks, i
        if c in ' \t\r\n':
            j = i
            while j < n and s[j] in ' \t\r\n':
                j += 1
            toks.append(Tok('ws', s[i:j]))
            i = j
            continue
        if s.startswith('//', i):
            j = s.find('\n', i)
            j = n if j < 0 else j
            toks.append(Tok('ws', s[i:j]))
            i = j
            continue
        if s.startswith('/*', i):
            j = s.index('*/', i) + 2
            toks.append(Tok('ws', s[i:j]))
            i = j
            continue
        if c == "'":
            j = i + 1
            while s[j] != "'":
                j += 2 if s[j] == '\\' else 1
            toks.append(Tok('chr', s[i:j + 1]))
            i = j + 1
            continue
        if s.startswith('$@"', i) or s.startswith('@$"', i):
            raise ValueError('verbatim interpolated string at %d' % i)
        if s.startswith('@"', i):
            j = i + 2
            while True:
                if s[j] == '"':
                    if j + 1 < n and s[j + 1] == '"':
                        j += 2
                        continue
                    break
                j += 1
            toks.append(Tok('vstr', s[i:j + 1]))
            i = j + 1
            continue
        if s.startswith('$"', i):
            parts, j = interp(s, i + 2)
            toks.append(Tok('istr', s[i:j], parts))
            i = j
            continue
        if c == '"':
            j = i + 1
            while s[j] != '"':
                j += 2 if s[j] == '\\' else 1
            toks.append(Tok('str', s[i:j + 1]))
            i = j + 1
            continue
        if c.isalnum() or c == '_':
            j = i
            while j < n and (s[j].isalnum() or s[j] == '_'):
                j += 1
            toks.append(Tok('word', s[i:j]))
            i = j
            continue
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        toks.append(Tok('punct', c))
        i += 1
    return toks, i


def interp(s, i):
    """Parts of an interpolated string whose content starts at i; returns (parts, index after the closing quote)."""
    parts = []
    lit = []
    n = len(s)
    while True:
        c = s[i]
        if c == '\\':
            lit.append(s[i:i + 2])
            i += 2
        elif s.startswith('{{', i) or s.startswith('}}', i):
            lit.append(s[i:i + 2])
            i += 2
        elif c == '{':
            if lit:
                parts.append(('lit', ''.join(lit)))
                lit = []
            toks, j = lex(s, i + 1, stop=set(',:}'))
            expr = ''.join(t.text for t in toks)
            spec = ''
            if s[j] in ',:':
                k = j
                while s[k] != '}':
                    k += 1
                spec = s[j:k]
                j = k
            parts.append(('hole', expr, spec))
            i = j + 1
        elif c == '"':
            if lit:
                parts.append(('lit', ''.join(lit)))
            return parts, i + 1
        else:
            lit.append(c)
            i += 1


def has_cyr_literal(tok):
    if tok.kind == 'str':
        return bool(CYR.search(tok.text))
    if tok.kind == 'istr':
        return any(p[0] == 'lit' and CYR.search(p[1]) for p in tok.parts)
    return False


KEYS = []


def transform_code(code):
    toks, _ = lex(code)
    return render(toks)


def prev_sig(toks, k):
    k -= 1
    while k >= 0 and toks[k].kind == 'ws':
        k -= 1
    return toks[k] if k >= 0 else None


def statement_has_const(toks, k):
    k -= 1
    while k >= 0:
        t = toks[k]
        if t.kind == 'punct' and t.text in ';{}':
            return False
        if t.kind == 'word' and t.text == 'const':
            return True
        k -= 1
    return False


def render(toks):
    out = []
    k = 0
    n = len(toks)
    while k < n:
        t = toks[k]
        if t.kind not in ('str', 'istr'):
            out.append(t.text)
            k += 1
            continue
        # a chain: string tokens joined by +
        chain = [k]
        seps = []
        m = k
        while True:
            a = m + 1
            sep = []
            while a < n and toks[a].kind == 'ws':
                sep.append(toks[a].text)
                a += 1
            if a < n and toks[a].kind == 'punct' and toks[a].text == '+':
                sep.append('+')
                b = a + 1
                while b < n and toks[b].kind == 'ws':
                    sep.append(toks[b].text)
                    b += 1
                if b < n and toks[b].kind in ('str', 'istr'):
                    # the next literal must not be followed by member access (it would bind to it alone)
                    c = b + 1
                    while c < n and toks[c].kind == 'ws':
                        c += 1
                    if c < n and toks[c].kind == 'punct' and toks[c].text in '.[':
                        break
                    chain.append(b)
                    seps.append(''.join(sep))
                    m = b
                    continue
            break
        after = m + 1
        while after < n and toks[after].kind == 'ws':
            after += 1
        member = after < n and toks[after].kind == 'punct' and toks[after].text in '.['
        wrap = any(has_cyr_literal(toks[x]) for x in chain)
        p = prev_sig(toks, k)
        if p is not None and p.kind == 'word' and p.text == 'case':
            wrap = False
        if statement_has_const(toks, k):
            wrap = False
        if not wrap:
            # untouched, but holes may hold Ukrainian strings
            for idx, x in enumerate(chain):
                if idx > 0:
                    out.append(seps[idx - 1])
                out.append(render_plain(toks[x]))
            k = m + 1
            continue
        holes = []
        pieces = []
        key = []
        for idx, x in enumerate(chain):
            tk = toks[x]
            if idx > 0:
                pieces.append(seps[idx - 1])
            if tk.kind == 'str':
                pieces.append(tk.text)
                key.append(tk.text[1:-1])
            else:
                fmt = []
                for part in tk.parts:
                    if part[0] == 'lit':
                        fmt.append(part[1])
                    else:
                        fmt.append('{%d%s}' % (len(holes), part[2]))
                        holes.append(transform_code(part[1]).strip())
                pieces.append('"' + ''.join(fmt) + '"')
                key.append(''.join(fmt))
        KEYS.append(''.join(key))
        body = ''.join(pieces)
        if holes:
            text = 'Lang.F(' + body + ', ' + ', '.join(holes) + ')'
        else:
            text = 'Lang.T(' + body + ')'
        if member:
            text = '(' + text + ')'
        out.append(text)
        k = m + 1
    return ''.join(out)


def render_plain(tok):
    if tok.kind != 'istr':
        return tok.text
    res = ['$"']
    for part in tok.parts:
        if part[0] == 'lit':
            res.append(part[1])
        else:
            res.append('{' + transform_code(part[1]) + part[2] + '}')
    res.append('"')
    return ''.join(res)


def main():
    root = sys.argv[1]
    keys_path = sys.argv[2]
    dry = '--dry' in sys.argv
    changed = 0
    for dp, dn, fn in os.walk(root):
        if os.sep + 'obj' in dp or os.sep + 'bin' in dp:
            continue
        for f in sorted(fn):
            if not f.endswith('.cs'):
                continue
            path = os.path.join(dp, f)
            rel = os.path.relpath(path, root)
            if rel in SKIP_FILES:
                continue
            src = open(path, encoding='utf-8-sig').read()
            if not CYR.search(src):
                continue
            before = len(KEYS)
            new = transform_code(src)
            if new != src:
                changed += 1
                if 'using ValheimHelper;' not in new and not re.search(r'namespace ValheimHelper\s*\n', new):
                    # Lang lives in namespace ValheimHelper; nested namespaces see it already
                    pass
                if not dry:
                    open(path, 'w', encoding='utf-8-sig', newline='').write(new)
            print(f'{len(KEYS) - before:5}  {rel}')
    seen = []
    for k in KEYS:
        if k not in seen:
            seen.append(k)
    json.dump(seen, open(keys_path, 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    print('files', changed, 'keys', len(KEYS), 'unique', len(seen))


if __name__ == '__main__':
    main()
