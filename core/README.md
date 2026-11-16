# core — `tonewise-core` (Rust Workspace)

双端同源核心业务库与离线算力 CLI（对应开发方案 L2 & M1–M2），待初始化 `Cargo.toml`。

- **crates/tonewise-metadata**：TagLib FFI、五维扩展字段（`DJ_ENERGY`/`DJ_SCENE`/`DJ_VOCALS`/`Tempo`/`Mood`）读写、斜杠艺人（保护专名白名单）与 ISO 日期清洗。
- **crates/tonewise-rules**：Mood 五簇同义词字典、`Tempo IS NULL` 容错 AST 求值与能量弧线排序。
- **crates/tonewise-dsp-core**：64-bit Biquad PEQ、EqualizerAPO `.txt` 解析、150 点 RMS 包络提取与 BPM 瞬态检测。
- **crates/tonewise-sync**：Blake3 分块哈希、Header-Only 微补丁、`.m3u8` 三路归并与 `history.jsonl` 幂等合并。
- **crates/tonewise-ffi**：对外导出 Windows C-FFI (`.dll`) 与 Android JNI (`libtonewise_core.so`)。
- **crates/tonewise-cli**：PC 多核离线回填工具与车载 U 盘导出 CLI。
