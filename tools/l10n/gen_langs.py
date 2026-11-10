"""Builds ValheimHelper/lang/<code>.json (Ukrainian text → translation, real strings) from <code>.json of translate_agy.py
(index → translation as C# literal text) and keys.json + keys_extra.json (the Ukrainian texts as C# literal text).

usage: gen_langs.py <mod source root> [codes…]   (all *.json translations here when no codes are given)
"""
import json, os, re, sys

sys.stdout.reconfigure(encoding='utf-8')
here = os.path.dirname(os.path.abspath(__file__))
ESC = re.compile(r'\\(u[0-9a-fA-F]{4}|.)')
SIMPLE = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', "'": "'", '\\': '\\', '0': '\0'}
CODES = ['de', 'fr', 'es', 'pl', 'pt-br', 'zh', 'cs']


def unescape(literal):
    return ESC.sub(lambda m: chr(int(m.group(1)[1:], 16)) if m.group(1)[0] == 'u' else SIMPLE.get(m.group(1), m.group(1)), literal)


def main():
    root = sys.argv[1]
    codes = sys.argv[2:] or [c for c in CODES if os.path.exists(os.path.join(here, c + '.json'))]
    keys = json.load(open(os.path.join(here, 'keys.json'), encoding='utf-8'))
    for k in json.load(open(os.path.join(here, 'keys_extra.json'), encoding='utf-8')):
        if k not in keys:
            keys.append(k)
    target = os.path.join(root, 'lang')
    os.makedirs(target, exist_ok=True)
    for code in codes:
        if code == 'ru':
            sys.exit('No Russian: the author decided the mod has no Russian.')
        done = json.load(open(os.path.join(here, code + '.json'), encoding='utf-8'))
        table = {unescape(keys[int(i)]): unescape(text) for i, text in done.items() if int(i) < len(keys)}
        json.dump(table, open(os.path.join(target, code + '.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
        print(f'{code}: {len(table)} of {len(keys)}')


if __name__ == '__main__':
    main()
