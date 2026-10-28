"""把搜狗细胞词库（.scel）转成 KeyboundBrawler 插件用的 words.json。

用法：
    python tools/scel_to_json.py "<词库.scel>" "<输出 words.json>"

scel 结构（实测 2026-10-28 的 FF14 魔光键影词库）：
    0x00            uint32  拼音表偏移
    0x04            "DCS\x01"
    0x08            uint32  版本（1）
    0x0C            16 bytes 词库标识
    0x1C            UTF-16  词库名称（不固定长度，读到这里不再关心）
    <拼音表偏移>    uint32  拼音条目数 N
                    然后 N 条：uint16 索引, uint16 字节长度, UTF-16LE 拼音
    词条表          每条：uint16 标记（观测恒为 1）
                     uint16 拼音字节长度, 拼音索引串（每个 2 字节）
                     uint16 词字节长度, 词（UTF-16LE）
                     uint16 扩展长度, 扩展（前 4 字节是词频）
"""

from __future__ import annotations

import json
import struct
import sys
from pathlib import Path


def parse_scel(path: Path) -> list[dict]:
    data = path.read_bytes()

    pinyin_offset = struct.unpack_from("<I", data, 0)[0]
    off = pinyin_offset
    pinyin_count = struct.unpack_from("<I", data, off)[0]
    off += 4

    pinyins: list[str] = []
    for _ in range(pinyin_count):
        _idx, length = struct.unpack_from("<HH", data, off)
        off += 4
        pinyins.append(data[off:off + length].decode("utf-16-le"))
        off += length

    words: list[dict] = []
    while off + 2 <= len(data):
        start = off
        # 2 字节词条标记（本词库恒为 1）
        _marker = struct.unpack_from("<H", data, off)[0]
        off += 2

        pinyin_len = struct.unpack_from("<H", data, off)[0]
        if pinyin_len == 0 or pinyin_len % 2 or pinyin_len > 128:
            raise ValueError(f"词条 {start:#x} 拼音长度异常: {pinyin_len}")
        off += 2
        pinyin_idx = [struct.unpack_from("<H", data, off + k)[0]
                      for k in range(0, pinyin_len, 2)]
        off += pinyin_len

        word_len = struct.unpack_from("<H", data, off)[0]
        if word_len == 0 or word_len % 2:
            raise ValueError(f"词条 {start:#x} 词长度异常: {word_len}")
        off += 2
        word = data[off:off + word_len].decode("utf-16-le")
        off += word_len

        ext_len = struct.unpack_from("<H", data, off)[0]
        off += 2
        ext = data[off:off + ext_len]
        off += ext_len

        if any(i >= len(pinyins) for i in pinyin_idx):
            raise ValueError(f"词条 {start:#x} 拼音索引越界")

        freq = struct.unpack_from("<I", ext, 0)[0] if len(ext) >= 4 else 0
        words.append({
            "word": word,
            "pinyin": "".join(pinyins[i] for i in pinyin_idx),
            "freq": freq,
        })

    return words


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2

    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    words = parse_scel(src)

    # 去重（保留词频高的），按词频降序
    best: dict[str, dict] = {}
    for entry in words:
        old = best.get(entry["word"])
        if old is None or entry["freq"] > old["freq"]:
            best[entry["word"]] = entry
    unique = sorted(best.values(), key=lambda e: (-e["freq"], e["word"]))

    payload = {
        "source": src.name,
        "count": len(unique),
        "words": unique,
    }
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"解析 {len(words)} 条，去重后 {len(unique)} 条 -> {dst}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
