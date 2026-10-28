"""把「光之键盘侠词库合集」的 词条总表.tsv 转成插件用的 words.json。

用法：
    python tools/tsv_to_json.py "<词条总表.tsv>" "<输出 words.json>"

TSV 格式（UTF-8 BOM，制表符分隔）：
    词语    全拼    词频
    神罗    shen luo    1

输出 words.json：
    {"source": ..., "count": N,
     "words": [{"word":"神罗","pinyin":"shenluo","pinyinSpaced":"shen luo","freq":1}]}
"""

from __future__ import annotations

import csv
import json
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    rows = list(csv.reader(src.open(encoding="utf-8-sig"), delimiter="\t"))
    if not rows:
        raise SystemExit("空文件")

    header = [c.strip() for c in rows[0]]
    if "词语" not in header:
        raise SystemExit(f"表头不对: {header}")

    word_idx = header.index("词语")
    pinyin_idx = header.index("全拼") if "全拼" in header else -1
    freq_idx = header.index("词频") if "词频" in header else -1

    words: list[dict] = []
    seen: set[str] = set()
    for row in rows[1:]:
        if not row or len(row) <= word_idx:
            continue
        word = row[word_idx].strip()
        if not word or word in seen:
            continue
        seen.add(word)

        pinyin_spaced = row[pinyin_idx].strip() if pinyin_idx >= 0 and pinyin_idx < len(row) else ""
        pinyin = pinyin_spaced.replace(" ", "").replace("'", "")
        freq = 1
        if freq_idx >= 0 and freq_idx < len(row):
            try:
                freq = int(row[freq_idx].strip())
            except ValueError:
                freq = 1

        words.append({
            "word": word,
            "pinyin": pinyin,
            "pinyinSpaced": pinyin_spaced,
            "freq": freq,
        })

    payload = {
        "source": f"{src.parent.name}/{src.name}",
        "count": len(words),
        "words": words,
    }
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"解析 {len(rows) - 1} 行，去重后 {len(words)} 条 -> {dst}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
