"""把 KeyboundBrawler 注册为 Dalamud 开发插件（修改 dalamudConfig.json）。

前提：游戏必须完全退出（否则 Dalamud 退出时会覆盖配置）。

用法：
    python tools/register_dalamud_config.py "<KeyboundBrawler.dll 绝对路径>"

做的三件事（幂等，已注册则跳过）：
  1. 在 DevPluginLoadLocations 里加一条 dll 路径（IsEnabled = true）；
  2. 在 DevPluginSettings 里加一条（StartOnBoot = true，随机 GUID）；
  3. 备份原文件为 dalamudConfig.json.bak-kbb-时间戳。
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import uuid
from datetime import datetime
from pathlib import Path

LIST_TYPE = "System.Collections.Generic.List`1[[Dalamud.Configuration.DevPluginLocationSettings, Dalamud]], System.Private.CoreLib"
LOCATION_TYPE = "Dalamud.Configuration.DevPluginLocationSettings, Dalamud"
DICT_TYPE = ("System.Collections.Generic.Dictionary`2[[System.String, System.Private.CoreLib],"
             "[Dalamud.Configuration.Internal.DevPluginSettings, Dalamud]], System.Private.CoreLib")
SETTINGS_TYPE = "Dalamud.Configuration.Internal.DevPluginSettings, Dalamud"
STRING_LIST_TYPE = "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib"


def find_config() -> Path:
    appdata = Path(os.environ["APPDATA"])
    for root in ("XIVLauncherCN", "XIVLauncher"):
        path = appdata / root / "dalamudConfig.json"
        if path.is_file():
            return path
    raise SystemExit("找不到 dalamudConfig.json（XIVLauncherCN / XIVLauncher 都没有）")


def main() -> int:
    if len(sys.argv) not in (2, 3):
        print(__doc__)
        return 2

    dll = Path(sys.argv[1]).resolve()
    if not dll.is_file():
        raise SystemExit(f"dll 不存在: {dll}")

    config_path = Path(sys.argv[2]) if len(sys.argv) == 3 else find_config()
    if not config_path.is_file():
        raise SystemExit(f"配置文件不存在: {config_path}")
    raw = config_path.read_text(encoding="utf-8-sig")
    config = json.loads(raw)

    key = str(dll)

    locations = config.get("DevPluginLoadLocations", {})
    loc_values = locations.get("$values", []) if isinstance(locations, dict) else []
    if not any(str(item.get("Path", "")).lower() == key.lower() for item in loc_values):
        locations["$type"] = LIST_TYPE
        loc_values.append({
            "$type": LOCATION_TYPE,
            "Path": key,
            "IsEnabled": True,
            "Nickname": None,
        })
        locations["$values"] = loc_values
        config["DevPluginLoadLocations"] = locations
        print(f"[+] DevPluginLoadLocations: {key}")
    else:
        print("[=] DevPluginLoadLocations 已存在，跳过")

    settings = config.get("DevPluginSettings", {})
    if not isinstance(settings, dict):
        settings = {}
    if key not in settings:
        settings[key] = {
            "$type": SETTINGS_TYPE,
            "StartOnBoot": True,
            "NotifyForErrors": True,
            "AutomaticReloading": True,
            "WorkingPluginId": str(uuid.uuid4()),
            "DismissedValidationProblems": {
                "$type": STRING_LIST_TYPE,
                "$values": [],
            },
        }
        print(f"[+] DevPluginSettings: {key}（StartOnBoot=true）")
    else:
        print("[=] DevPluginSettings 已存在，跳过")
    settings["$type"] = DICT_TYPE
    config["DevPluginSettings"] = settings

    backup = config_path.with_name(f"dalamudConfig.json.bak-kbb-{datetime.now():%Y%m%d-%H%M%S}")
    shutil.copy2(config_path, backup)
    config_path.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"[ok] 已写入 {config_path}")
    print(f"[ok] 备份 {backup.name}")
    print("下一步：启动游戏，插件应自动加载；游戏内输入 /kbb 打开窗口。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
