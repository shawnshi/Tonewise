# protocol — `.tonewise/` 双端同步契约与测试夹具

PC 端与 Android 端共享协议的单一事实来源（SSOT，对应开发方案第四节 & M1/M4 验收门）。

- **schemas/**：`manifest.db` SQLite DDL、`scenes_5d.json` Schema、`history.jsonl` 事件规范与跨端 DSP 档案子集约定。
- **dictionaries/**：双端共享 Mood 五簇同义词折叠字典与艺人专名白名单。
- **fixtures/**：五维规则一致性和 `.m3u8` 三路归并黄金测试夹具（供 `core/` 与 `android/` 联合回归）。
