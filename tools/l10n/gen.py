"""Builds Core/LangEn.Generated.cs from keys.json (Ukrainian keys as C# literal text) and en_*.tsv (id → English)."""
import glob
import json
import os
import re
import sys

here = os.path.dirname(os.path.abspath(__file__))
keys = json.load(open(os.path.join(here, 'keys.json'), encoding='utf-8'))
extra_path = os.path.join(here, 'keys_extra.json')
if os.path.exists(extra_path):
    for k in json.load(open(extra_path, encoding='utf-8')):
        if k not in keys:
            keys.append(k)
english = {}
for path in sorted(glob.glob(os.path.join(here, 'en_*.tsv'))):
    for line in open(path, encoding='utf-8').read().split('\n'):
        if not line.strip():
            continue
        i, text = line.split('\t', 1)
        english[int(i)] = text
extra_en = os.path.join(here, 'en_extra.json')
if os.path.exists(extra_en):
    for k, v in json.load(open(extra_en, encoding='utf-8')).items():
        english[keys.index(k)] = v

HOLE = re.compile(r'\{(\d+)(?:[,:][^}]*)?\}')
problems = []
missing = [i for i in range(len(keys)) if i not in english]
for i, key in enumerate(keys):
    if i not in english:
        continue
    value = english[i]
    # trailing spaces of the key (lost when the translation files were saved) come back
    tail = len(key) - len(key.rstrip(' '))
    if tail and not value.endswith(' '):
        value = value + ' ' * tail
        english[i] = value
    if sorted(set(HOLE.findall(key))) != sorted(set(HOLE.findall(value))):
        problems.append(f'{i}: holes differ: {key!r} → {value!r}')
    if key.count('\\n') != value.count('\\n'):
        problems.append(f'{i}: line breaks differ')
    if key.startswith(' ') != value.startswith(' ') or key.endswith(' ') != value.endswith(' '):
        problems.append(f'{i}: edge spaces differ: {key!r} → {value!r}')
    if re.search(r'(?<!\\)"', value):
        problems.append(f'{i}: raw quote in value')
    for tag in ('<color', '</color>', '<b>', '</b>', '<i>', '</i>'):
        if key.count(tag) != value.count(tag):
            problems.append(f'{i}: tag {tag} differs')
if missing:
    problems.append(f'missing translations: {missing[:20]} ({len(missing)})')
for p in problems:
    print(p)
out = ['using System.Collections.Generic;', '', 'namespace ValheimHelper', '{',
       '    // Generated from the mod\'s Ukrainian texts and their English translation (tools: l10n/gen.py). Edit the translation there.',
       '    internal static partial class LangEn', '    {', '        static partial void Add(Dictionary<string, string> table)', '        {']
for i, key in enumerate(keys):
    if i in english:
        out.append(f'            table["{key}"] = "{english[i]}";')
out += ['        }', '    }', '}', '']
target = os.path.join(sys.argv[1], 'Core', 'LangEn.Generated.cs')
open(target, 'w', encoding='utf-8-sig', newline='\n').write('\n'.join(out))
print('keys', len(keys), 'translated', len(english), 'problems', len(problems))
