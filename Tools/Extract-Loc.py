"""Writes Languages/English.json from every Lang.T / Lang.F / Lang.N call in the root *.cs.

English.json is the base file a translator copies to <ValheimLanguage>.json and translates; the game
still takes its English from the call sites, so this file is regenerated, never hand-edited.
Run from the project root: python Tools/Extract-Loc.py
"""
import json
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent
LIT = r'"((?:[^"\\]|\\.)*)"'
SINGLE = re.compile(r'Lang\.[TF]\(\s*' + LIT + r'\s*,\s*' + LIT)
PLURAL = re.compile(r'Lang\.N\(\s*' + LIT + r'\s*,\s*[^",()]+,\s*' + LIT + r'\s*,\s*' + LIT)


def unescape(text):
    return json.loads('"' + text + '"')


texts = {}


def add(key, english):
    key, english = unescape(key), unescape(english)
    if texts.get(key, english) != english:
        sys.exit("Key " + key + " has two different English texts")
    texts[key] = english


for source in sorted((root / ".").glob("*.cs")):
    if source.name == "Lang.cs":
        continue
    code = source.read_text(encoding="utf-8")
    for key, english in SINGLE.findall(code):
        add(key, english)
    for key, one, other in PLURAL.findall(code):
        add(unescape(key) + ".One", one)
        add(unescape(key) + ".Other", other)

out = {"_note": "English base file. Copy to <Valheim language name>.json (for example German.json), translate the values, keep the keys and every {0} placeholder."}
out.update(dict(sorted(texts.items())))
(root / "Languages").mkdir(exist_ok=True)
(root / "Languages" / "English.json").write_text(json.dumps(out, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(len(texts), "texts")
