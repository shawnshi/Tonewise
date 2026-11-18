# desktop — Tonewise Windows 11 原生桌面端

基于 .NET / C# 的桌面端工程与局域网同步宿主（`Tonewise.sln`）：

- **src/Tonewise.Desktop**：桌面工作台与曲库/情景视图。
- **src/Tonewise.Audio**：64-bit `BiquadFilter64` 参数均衡、EqualizerAPO 解析与桌面音频输出组件。
- **src/Tonewise.SyncHost**：mDNS (`_tonewise._tcp`) 服务广播与局域网 HTTP 同步宿主。
- **src/Tonewise.Interop**：对接 `core/` 产出的 `tonewise_core.dll` P/Invoke 封装。
- **tests/Tonewise.Desktop.Tests**：桌面端核心模块单元测试。

```powershell
dotnet test desktop/Tonewise.sln
```

## 界面预览 (Screenshots)

| 曲库与列头排序 | 批量多选与属性精修 |
| :---: | :---: |
| <img src="../docs/assets/screenshots/desktop-01-library-sort.png" alt="曲库与列头排序" width="460"> | <img src="../docs/assets/screenshots/desktop-02-multiselect-inspector.png" alt="批量多选与属性精修" width="460"> |
| **古典作品与四元组检查器** | **场景编排与 AutoDJ** |
| <img src="../docs/assets/screenshots/desktop-03-classical-works.png" alt="古典作品与四元组检查器" width="460"> | <img src="../docs/assets/screenshots/desktop-04-scenes-autodj.png" alt="场景编排与 AutoDJ" width="460"> |
| **入库审校与缺标体检** | **Last.fm 与局域网同步** |
| <img src="../docs/assets/screenshots/desktop-05-inbox-audit.png" alt="入库审校与缺标体检" width="460"> | <img src="../docs/assets/screenshots/desktop-06-lastfm-sync.png" alt="Last.fm 与局域网同步" width="460"> |
