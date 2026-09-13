#!/usr/bin/env python3
"""列出每个文件未覆盖的行（供补测定位）。"""
import glob, sys, xml.etree.ElementTree as ET

root_dir = sys.argv[1] if len(sys.argv) > 1 else "tests"
files = glob.glob(f"{root_dir}/*/TestResults/*/coverage.cobertura.xml")

def normalize(asm, filename):
    marker = asm + "/"
    return filename.split(marker, 1)[1] if marker in filename else filename

hits = {}
for path in files:
    for package in ET.parse(path).getroot().iter("package"):
        asm = package.get("name")
        for cls in package.iter("class"):
            fn = normalize(asm, cls.get("filename") or "")
            for line in cls.iter("line"):
                if line.get("branch") == "true":
                    continue
                key = (asm, fn, int(line.get("number")))
                hits[key] = max(hits.get(key, 0), int(line.get("hits")))

by_file = {}
for (asm, fn, num), h in hits.items():
    by_file.setdefault((asm, fn), [0, 0, []])
    by_file[(asm, fn)][1] += 1
    if h > 0:
        by_file[(asm, fn)][0] += 1
    else:
        by_file[(asm, fn)][2].append(num)

rows = sorted(by_file.items(), key=lambda kv: -(kv[1][1] - kv[1][0]))
for (asm, fn), (cov, total, missing) in rows:
    if cov == total:
        continue
    pct = 100 * cov / total
    print(f"{asm:<40}{fn:<58}{pct:6.1f}%  未覆盖 {total-cov:4d} 行: {missing if total-cov<=40 else str(missing[:40])+['...',''][len(missing)<=40]}")
