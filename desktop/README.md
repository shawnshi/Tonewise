# desktop — Tonewise Windows 11 原生桌面端

WinUI 3 (.NET 8 / C# 12 / Windows App SDK 1.6) 桌面工作台与声学输出端（对应开发方案 L1/L3/L4 & M3–M5），待初始化 `Tonewise.sln`。

- **src/Tonewise.Desktop**：三栏主工作台、五维胶囊筛选、SkiaSharp 150 点包络波形与古典四级研读/Booklet 阅读。
- **src/Tonewise.Widget**：副屏防烧屏伴侣微挂件与全局热键守护。
- **src/Tonewise.Audio**：Event-driven WASAPI Exclusive、ASIO Native DSD/DoP、64-bit DSP 管线与 Windows SMTC 集成。
- **src/Tonewise.SyncHost**：mDNS (`_tonewise._tcp`)、HTTP/2 局域网服务端、Handoff 接力与车载 `.m3u8` 导出。
- **src/Tonewise.Interop**：对接 `core/` 产出的 `tonewise_core.dll` P/Invoke 封装。
- **src/Tonewise.McpServer**：本地 MCP 与自动化桥接。
