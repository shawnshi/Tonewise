<p align="center"><img src="docs/assets/tonewise-icon.jpg" width="150"></p>
<h1 align="center"><b>Tonewise 知律</b></h1>
<h4 align="center">懂作品的章法，亦懂入景的直觉。Structured for works. Built for scenes. Bit-perfect to the ear.</h4>

<p align="center"><img src="docs/assets/tonewise-banner.jpg" width="640"></p>

## About

流媒体时代不缺音乐入口，缺的是一套真正尊重专辑叙事与录音本貌的本地听音器物。通用播放器往往按流行单曲逻辑设计：交响曲的完整乐章被随机打碎，爵士三重奏里的伴奏名家无处检索，精心抓轨的数百张无损专辑最终困在繁琐的目录层级里吃灰。

**Tonewise（知律）** 是一款面向古典、爵士与摇滚本地无损曲库打造的开源跨端本地音乐系统（GPL-3.0）。项目以 **Rust 同源核心库 (`core/`) + Windows 11 原生桌面端 (`desktop/`) + Android 深度定制端 (`android/`, 基于 [Auxio](https://github.com/OxygenCobalt/Auxio)) + `.tonewise/` 双端同步协议 (`protocol/`)** 构成“PC 重算力策展与母带直通 + 手机随身零摩擦入景 + 局域网无云同步”的完整本地听音体系。

### 当前实现状态（Tonewise `v0.9.0` · `com.shawnshi.tonewise` `versionCode 90`）

* **Rust 同源内核 (`core/`)**：完成 `tonewise-metadata`、`tonewise-rules`、`tonewise-dsp-core`、`tonewise-sync`、`tonewise-ffi`、`tonewise-cli` 六大 crate 构建与双端 JNI/FFI 对齐（`build-core-android.ps1` / `build-core-desktop.ps1`），支持 EqualizerAPO 编号行 `Filter <n>:` 跨端统一解析及共享五维场景字典（`protocol/dictionaries/default_scenes_5d.json`）。
* **Windows 11 桌面端 (`desktop/`)**：完成 P1–P4、R1–R8、数据库拆库治理与播放/挂钟全链重构：
  * **母带级 64-bit 声学引擎 (`Tonewise.Audio`)**：Event-Driven WASAPI Exclusive / `AsioOut` 输出（修复初始化异常句柄泄漏、`_engineLock` 锁外异步释放与 `IMMNotificationClient` 设备热插拔实时重连）、`.dsf`/`.dff` 原生 DSD→`352.8 kHz / 64-bit DXD`（127-tap FIR 抽取与 `.dff` 区块边界校验，停止后正确复位 `Idle — Standby` 并读取真实 `OutputWaveFormat` 位深/采样率）、Stereo Linked & L/R 独立 64 段 EqualizerAPO 热更新保留滤波器状态与 Auto-EQ 写回、`±12ms` 音量斜坡与平方律 0% 静音曲线、ISO 226 动态等响度热更新、带线性重采样/立体声联动归一化的零延迟分区 FFT FIR 卷积（支持 REW `.wav` 与 `.sofa` 双耳脉冲）、滑动峰值环 3ms Lookahead Limiter、分段隔离 ReplayGain 的无缝队列连播（`GaplessSampleProvider`）、古典整部作品与指挥粒度原子随机排序（`WorkAwareShuffleEngine`）及统一睡眠定时引擎（`SleepTimerEngine`：15/30/45/60 分钟最后 10 秒淡出、本曲结束、本古典作品结束）。
  * **音乐信息维护与双库分离架构 (`Tonewise.SyncHost`)**：采用 `manifest.db`（曲库只读索引）与 `desktop_state.db`（`DesktopStateStore` 队列/同步/配对状态库，经统一 `SqliteConnectionFactory` 执行 WAL 与 busy-timeout 治理）物理分库；入库审校台支持 `lofty` 物理音频文件头事务回写（保持纯音频 `audio_frame_blake3` 不变）与回滚、超长路径（`\\?\`）与防抖 `FileSystemWatcher` 实时曲库监测、Spotify 歌单导入、多源元数据与传记库，以及带 `_tonewise._tcp` mDNS + Bearer Token 6 位配对码的持久化幂等 LAN Sync v2（五类同步粒度、`Range` 分发）和 BOM-free 相对路径 `.m3u8` 导出。
  * **曜石黑工作台与 OLED 壁挂钟 (`Tonewise.Desktop`)**：`#131720` + `#D9B372` 高密度三栏工作台、单/多曲批量检查器、AutoDJ 排曲、底部睡眠定时与焦点防护热键（`Ctrl+Alt+P/C/M`）；独立纯黑 `#000000` `DeskClockWidgetWindow` 支持悬浮伴侣窗与 `F11`/`Esc` 全屏壁挂钟双形态、HiDPI `GetDpiForWindow` 缩放、`SetThreadExecutionState` 防熄屏（暂停 10 分钟或睡眠定时到期自动释放）、16 点 `±12 DIP` 防烧屏轨道、封面与古典三层元数据展示及全屏 10 秒无操作自动隐栏。
* **Android 客户端四阶段演进 (`android/` S1–S4 及 v0.9.0 增强)**：
  * **S1 — 64-bit 浮点调音引擎与双模直通链路**：通过 `media-float-dsp-processors.patch` 修复 Media3 `DefaultAudioSink` 浮点输出分支确保自定义 `AudioProcessor` 全链运行，`TonewiseDspEngine` 提供 `C¹` 连续软限幅、IIR 系数热更新、滑动峰值环 Lookahead + `onQueueEndOfStream()` 尾帧排空、EqualizerAPO 编号行解析、与桌面端对齐的 10 段蓝牙/夜间预设，以及 API 33+/34+ 路由实测与 USB DAC Mixer 位深一致校验。
  * **S2 — 古典多版本作品索引与原子洗牌**：`:musikr` 深度解析 `WORK` / `MOVEMENTNAME` / `MOVEMENTNUMBER` / `MUSICBRAINZ_WORKID` / `MOOD`（缓存库升级至 v75 两阶段冷启动加载）、`ClassicalWork` 多版本对比切曲以及按指挥维度隔离合辑同作品录音块的 `WorkShuffleOrder` 原子洗牌与动态 ReplayGain 对齐。
  * **S3 — 古典四元组排版与数码画册**：播放页呈现“作曲家—作品名—乐章—演绎者/指挥”衬线排版、`SignalPathDialog` 真实链路面板与原生 `PdfRenderer` `BookletPdfDialog` 数码画册阅读器。
  * **S4 — 独立情景与专注页签、同步中心、睡眠定时与 OLED 挂钟体系**：`Library` 新增独立 **Scenes** 与 **Focus** 页签、作曲家/指挥家专属排序视图与 5 槽位场景热切；同步中心集成 `TonewiseSyncDatabase`（导出的 Room `schemas/` 迁移治理）与 `SecretBox` / `LwwMutationStore` / `LanSyncForegroundService`，修复 MediaSession 100 首队列滑窗、Last.fm 50%/4 分钟 Scrobble 门槛、瞬时音频焦点丢失状态抑制及 `WaveformRepository` 60 秒负缓存/2048 LRU 指纹缓存；支持主页顶栏常驻进入与独立 `TonewiseDreamService` OLED 充电屏保、`DeskClockDialog` 150 点可拖拽波形进度条、视口等比大字号、系统 12/24h 秒边界对齐、10 秒控件隐去及联动释锁的 `SleepTimerController`。

## Release Download / 发行包下载

仓库已随附最新构建并校验的双端 Release 发行包：

| 平台 | 安装包 / 发行归档 | 版本与运行环境 | 文件大小 | SHA-256 |
| :--- | :--- | :--- | :--- | :--- |
| **Android** | [releases/tonewise-android-release.apk](releases/tonewise-android-release.apk) | `com.shawnshi.tonewise` · `v0.9.0` (`versionCode 90`) | `16,006,589` bytes (~15.27 MiB) | `79eeacec5ae1e292ac0d99a5aa991c64700d043bf6d5e1f4b7a4b374d00c9db3` |
| **Windows x64** | [releases/tonewise-windows-x64-release.zip](releases/tonewise-windows-x64-release.zip) | `v0.9.0` · WinUI 3 (`net9.0-windows10.0.26100.0` + 含 `tonewise_core.dll` & `tonewise-cli.exe`) | `48,920,930` bytes (~46.65 MiB) | `29b5e080fee4b37429d534de04b0cf47cedc6650757405749448c269baafc772` |

* **Android 签名说明**：已通过 `zipalign` 4 字节对齐与 `apksigner` v2/v3 签名校验（当前使用开发者本机调试签名密钥 `CN=Android Debug, O=Android, C=US` 封装，可直接侧载安装体验；正式商店分发请替换为独立发布密钥签章）。
* **Windows x64 运行说明**：解压 `tonewise-windows-x64-release.zip` 后直接运行 `Tonewise.Desktop.exe`（需安装 .NET 9 Desktop Runtime；Windows App SDK 1.7 与 Rust `tonewise_core.dll` / `tonewise-cli.exe` 已随压缩包自包含打包）。

## Screenshots

### Android 移动端（核心界面）

| 情景与专注导航 | 专辑与曲库浏览 | 古典四元组播放与波形进度条 |
| :---: | :---: | :---: |
| <img src="docs/assets/screenshots/mobile-01-scenes.png" alt="Android 情景与专注导航" width="260"> | <img src="docs/assets/screenshots/mobile-02-library.png" alt="Android 专辑与曲库浏览" width="260"> | <img src="docs/assets/screenshots/mobile-03-playback-classical.png" alt="Android 古典四元组播放与波形进度条" width="260"> |
| **64-bit 浮点参量均衡与调音引擎** | **Focus 正反选布尔漏斗** | **5 槽位接力、Last.fm 与局域网同步** |
| <img src="docs/assets/screenshots/mobile-04-dsp.png" alt="Android 64-bit 浮点参量均衡与调音引擎" width="260"> | <img src="docs/assets/screenshots/mobile-05-focus-quicktag.png" alt="Android Focus 正反选布尔漏斗" width="260"> | <img src="docs/assets/screenshots/mobile-06-sync-hub.png" alt="Android 5 槽位接力、Last.fm 与局域网同步" width="260"> |

### Windows 桌面端（工作台精选）

| 桌面曲库与增强播放栏 | 古典作品与多版本演绎 |
| :---: | :---: |
| <img src="docs/assets/screenshots/desktop-01-library-workbench.png" alt="Windows 桌面曲库与增强播放栏" width="460"> | <img src="docs/assets/screenshots/desktop-02-classical-works.png" alt="Windows 古典作品与多版本演绎" width="460"> |
| **场景编排与 AutoDJ** | **局域网 6 位配对码与精细化同步** |
| <img src="docs/assets/screenshots/desktop-03-scenes-autodj.png" alt="Windows 场景编排与 AutoDJ" width="460"> | <img src="docs/assets/screenshots/desktop-04-lan-sync.png" alt="Windows 局域网 6 位配对码与精细化同步" width="460"> |

## Current Android Features

* **Media3 & 无损软解内核**：基于定制版 [Media3 ExoPlayer](https://developer.android.com/guide/topics/media/exoplayer)（含 `media-float-dsp-processors.patch` 浮点处理链修复）+ FFmpeg FLAC/ALAC 解码 + Rust `tonewise-core` JNI 原生能力
* **发烧级音频路径与 64-bit DSP**：USB DAC `BIT_PERFECT` 硬件直通与 Mixer 位深实测、5 级 64-bit 热更新 Biquad PEQ / Bauer Crossfeed / 滑动峰值环 Lookahead `C¹` 软限幅、`onQueueEndOfStream` 尾帧排空、EqualizerAPO 编号行导入与桌面端一致预设、等响度 A/B 盲听对比与信号路径实时监控
* **古典音乐一等公民支持**：`WORK` / `MOVEMENTNAME` / `MOVEMENTNUMBER` / `MUSICBRAINZ_WORKID` / 作曲家 / 指挥深度索引、独立作曲家/指挥家视图与专项排序（`PersonListFragment` / `ComposerSortDialog` / `ConductorSortDialog`）、同作品多版本同乐章对比切换、按指挥区分的原子乐章随机排序、衬线四元组视图与 `PdfRenderer` 数码画册阅读
* **多维详情与传记体系**：曲目列表展示 `album` / `year` / `play count` / `genre`；专辑、艺术家、作曲家、指挥详情页支持本地及缓存传记展示
* **极速冷启动、局域网零云同步与 OLED 挂钟 / 睡眠定时**：`CacheDatabase` v75 两阶段瞬时加载与后台静默刷新，**Scenes** / **Focus** 页签与 `QuickTagDialog` 快捷五维打标，`TonewiseSyncDatabase` Schema 治理、Last.fm 50%/4 分钟 Scrobble 门槛与离线队列、`_tonewise._tcp` mDNS + `SecretBox` 安全配对同步、100 首 MediaSession 队列滑窗、`WaveformRepository` 负缓存与 2048 LRU 指纹缓存、可拖拽 150 点波形挂钟 `DeskClockDialog`、独立 `TonewiseDreamService` 充电屏保及统一睡眠定时（`SleepTimerController`）
* **高完成度本地播放器基础**：Material Design Expressive 交互、SAF/MediaStore 双后端、无缝播放、完整 ReplayGain 与 Android Auto / 桌面微件支持

## Permissions

* Storage (`READ_MEDIA_AUDIO`, `READ_EXTERNAL_STORAGE` maxSdk 32) to read local music directories and liner note booklets
* Services (`FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_MEDIA_PLAYBACK`, `FOREGROUND_SERVICE_DATA_SYNC`, `WAKE_LOCK`) for background playback and LAN sync
* Notifications (`POST_NOTIFICATIONS`) for ongoing media playback and library indexing status
* Local Network (`INTERNET`, `ACCESS_NETWORK_STATE`, `CHANGE_WIFI_MULTICAST_STATE`) strictly for `_tonewise._tcp` LAN SyncHost discovery/transfer and optional metadata lookup

## Workspace Structure

```text
Tonewise/
├── android/    # Android 客户端 Gradle 工程 (:app, :musikr, vendored media/, patches/)
├── core/       # Rust tonewise-core 双端同源内核与离线回填 CLI
├── desktop/    # Windows 11 原生桌面端 (.NET / C# / WinUI 3) 与 SyncHost
├── protocol/   # .tonewise/ 双端同步协议 Schema、字典与回归夹具
├── releases/   # 已构建校验的 Android APK 与 Windows x64 ZIP 归档
├── scripts/    # 跨端构建脚本 (apply-media-patch [windows-ffmpeg + float-dsp-processors], build-core-android, build-core-desktop)
└── docs/       # 设计方案 (specs/)、声学调研 (research/) 与视觉资产 (assets/)
```

## Building (Windows 11)

Tonewise builds natively on Windows 11 with PowerShell 7 (JDK 21, CMake 3.22.1+, Ninja, Android NDK `28.2.13676358`, Rust stable toolchain, and .NET SDK for `desktop/`).

### 1. Clone, patch Media3, and build `tonewise-core` JNI/Desktop libs

```powershell
git -c core.autocrlf=false -c core.symlinks=true -c core.longpaths=true clone --recurse-submodules https://github.com/shawnshi/Tonewise.git
cd Tonewise
.\scripts\apply-media-patch.ps1
.\scripts\build-core-desktop.ps1
.\scripts\build-core-android.ps1
```

### 2. Prepare FFmpeg static libraries once (MSYS2 UCRT64)

`musikr` builds TagLib for all four Android ABIs automatically with CMake on Windows. For the patched Media3 FFmpeg decoder, build the static libraries once inside an MSYS2 UCRT64 shell:

```bash
pacman -Syu
pacman -S --needed base-devel diffutils nasm perl mingw-w64-ucrt-x86_64-gcc

module="$(cygpath -u "$PWD/android/media/libraries/decoder_ffmpeg/src/main")"
sdk="$(cygpath -u "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ndk="$sdk/ndk/28.2.13676358"
bash "$module/jni/build_ffmpeg.sh" "$module" "$ndk" windows-x86_64 21 flac alac
```

### 3. Build & test Android with Gradle

```powershell
Set-Location android
.\gradlew.bat :musikr:testDebugUnitTest :app:testDebugUnitTest :app:assembleDebug
.\gradlew.bat :app:assembleRelease
```

### 4. Connecting and installing on an Android device

```powershell
adb devices
Set-Location android
.\gradlew.bat installDebug
# Or sideload the release package from repo root:
adb install -r ..\releases\tonewise-android-release.apk
```

### 5. Build & test Windows Desktop (`desktop/`)

```powershell
cargo test --manifest-path core\Cargo.toml --workspace
cargo build --manifest-path core\Cargo.toml --release -p tonewise-ffi -p tonewise-cli
dotnet build desktop\Tonewise.sln -c Release -p:Platform=x64
dotnet test desktop\Tonewise.sln
```

## License

[![GNU GPLv3 Image](https://www.gnu.org/graphics/gplv3-127x51.png)](https://www.gnu.org/licenses/gpl-3.0.en.html)

Tonewise is Free Software: You can use, study, share and improve it at your will. Specifically you can redistribute and/or modify it under the terms of the [GNU General Public License](https://www.gnu.org/licenses/gpl.html) as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
