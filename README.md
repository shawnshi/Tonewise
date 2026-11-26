<p align="center"><img src="docs/assets/tonewise-icon.jpg" width="140" alt="Tonewise Icon"></p>
<h1 align="center"><b>Tonewise 知律</b></h1>
<h4 align="center">懂作品的章法，亦懂入景的直觉。Structured for works. Built for scenes. Bit-perfect to the ear.</h4>
<p align="center"><code>v1.0.7</code> · <code>GPL-3.0</code> · <code>Rust 2024</code> · <code>WinUI 3 (.NET 9)</code> · <code>Android Kotlin / Compose</code></p>

<p align="center"><img src="docs/assets/tonewise-banner.jpg" width="640" alt="Tonewise Banner"></p>

## 定位与核心理念

流媒体时代不缺音乐入口，缺的是尊重专辑叙事与录音本貌的本地听音器物。通用播放器通常按流行单曲逻辑设计：交响曲的乐章被随机打散，爵士三重奏里的伴奏名家无处检索，精心抓轨的数百张无损专辑最终困在目录层级里吃灰。

**Tonewise（知律）** 是一款面向古典、爵士与摇滚本地无损曲库打造的开源跨端音乐系统。项目以 **Rust 同源内核（`core/`）+ Windows 11 原生桌面端（`desktop/`）+ Android 移动端（`android/`，基于 [Auxio](https://github.com/OxygenCobalt/Auxio) 深度重构）+ `.tonewise/` 同步协议（`protocol/`）**，构成“PC 重算力策展与母带直通 + 手机随身零摩擦入景 + 局域网 / USB 无云同步”的本地听音体系。

---

## 核心能力

### 1. 64-bit 声学引擎与双层串联 Auto-EQ

* **双端同源浮点 DSP（`tonewise-dsp-core`）**：内置可热更新的 64-bit Biquad 参数均衡器（`15 ms` 系数平滑过渡，保留 IIR 滤波器状态）、`3 ms` 前瞻滑动峰值环 `C¹` 软限幅（`onQueueEndOfStream` 尾帧排空），以及 EqualizerAPO `Filter <n>:` 编号行跨端解析。
* **双层串联 Auto-EQ（`auto_eq.rs`）**：将“Layer A 设备校准”与“Layer B 流派 / 人声内容自适应”自动叠加并执行防削波合成；古典纯器乐默认保持 Flat `0 dB` 并支持可选自动联动 Bauer Crossfeed。
* **Windows 11 母带直通（`Tonewise.Audio`）**：支持 Event-Driven WASAPI Exclusive 与 ASIO 输出、`.dsf` / `.dff` 原生 DSD→`352.8 kHz / 64-bit DXD`（127-tap FIR 抽取）、ISO 226 动态等响度、分区零延迟 FFT FIR / `.sofa` 双耳卷积，以及 Modern Standby 锁屏 / 息屏保活与设备热插拔自愈重连。
* **Android 无损直通与实时调音**：通过 `media-float-dsp-processors.patch` 打通 Media3 32-bit 浮点处理链并集成 FFmpeg FLAC / ALAC 软解，支持 USB DAC `BIT_PERFECT` 硬件直通、Mixer 位深一致性校验、等响度 A/B 盲听对比与真实信号链路面板。

### 2. 古典与专辑一等公民索引

* **多维度古典元数据建模**：深度解析 `WORK`、`MOVEMENTNAME`、`MOVEMENTNUMBER`、`MUSICBRAINZ_WORKID` 与作曲家 / 指挥字段；支持“作曲家 → 作品 → 录音版本”三级联动检索，并在播放与洗牌时自动按作品整部展开、按指挥维度隔离合辑同作品录音。
* **版本对比与数码画册**：支持同一作品多个演绎版本之间一键比对与按比例进度切曲，内置基于原生 `PdfRenderer` 的专辑 `booklet.pdf` 阅读器。
* **安全原子标签策展**：桌面端审校台基于 Rust `lofty` 实现原地事务写回（保留纯音频 `audio_frame_blake3` 不变、规避 ID3v1 30 字节截断异常），并共享多语种艺人 / 作曲家 / 流派规整词典与 ICU 汉字拼音映射表。

### 3. 双端 Zune / Metro 界面体系（`v1.0.7`）

* **Windows 11 桌面端**：基于 WinUI 3 无边框壳层构建纯黑 `#000000` / 纯白 `#FFFFFF` 双场高对比工作台；顶部设 **`quickplay · collection · now playing · studio`** 四大枢纽与 10 维馆藏子视图，集成专辑拼贴背景墙、`up next | booklet | recordings` 可收起队列面板、键盘多选撤销工作流与纯黑 OLED 壁挂钟。
* **Android 移动端**：
  * **精简全景架构**：顶部 **`quickplay · collection · autodj`** 三大枢纽；`collection` 固定提供 **`albums · tracks · artists · genres · classical · composers · conductors · playlists`** 八维常驻二级视图；`autodj` 聚焦一键 50 首时段电台与 **`scenes`** 场景磁贴；右上角 `···` 抽屉收敛为 6 项高频工具。
  * **谜底黑胶灵感双形态播放页**：支持在**拟真旋转黑胶唱盘（同心纹路 + 居中封面唱芯 + 动态金属唱臂）**与**精装专辑封套**之间一键切换，默认采用当前封面取色的低饱和径向氛围光晕背景（设置中可切回 Zune 马赛克墙）。
  * **降噪与交互收敛**：移除歌曲下方冗余通道与 5D 胶囊；配备 150 段声学波形进度条（等宽数字 `tnum`，点击总时长可切换剩余时间）；曲目更多菜单（含信号链路、多版录音对比与数码画册）移至顶栏右上角；睡眠定时统一合并进纯黑 OLED 电子挂钟界面，提供大尺寸夜间触控靶区与常驻倒计时徽标。

### 4. 无云双端同步（LAN Sync v2 & USB ADB）

* **双通道近场发现与直推**：通过 `_tonewise._tcp` mDNS + 6 位配对码建立安全连接，支持局域网 `Range` 断点传输与插线 USB ADB 自动直推隧道。
* **职责明确的双向账本**：PC→手机执行单向防污染歌单与音频镜像（内置五道清理熔断保护）；手机⇄PC 双向同步播放次数 / Last.fm 账本、LWW 五维标签与评分、Auto-EQ 规则及 5 槽位场景接力，并内置跨语种拼音 / 繁简 / 日文新字体 / 别名歌单精准匹配引擎。

---

## 系统架构与仓库目录

| 目录 | 技术栈 | 核心职责 |
| :--- | :--- | :--- |
| [core/](core) | Rust 2024 | `tonewise-metadata`、`tonewise-rules`、`tonewise-dsp-core`、`tonewise-sync`、`tonewise-ffi`、`tonewise-cli` 六大共享 crate |
| [desktop/](desktop) | .NET 9 / C# / WinUI 3 | Windows 11 桌面工作台、WASAPI / ASIO 声学引擎、LAN / USB 同步宿主与桌面端单测 |
| [android/](android) | Kotlin / Jetpack Compose / Media3 | Android 客户端（`:app`、`:musikr` 标签引擎、定制 `media/` 与 `patches/`） |
| [protocol/](protocol) | SQL / JSON / Fixture | `manifest.db` DDL、五维场景 Schema、共享词典（拼音 / 艺人白名单 / 别名 / 流派）与黄金测试夹具 |
| [scripts/](scripts) | PowerShell 7 | Media3 补丁应用与 Rust 双端产物（`.dll` / `.so`）编译分发脚本 |
| [releases/](releases) | APK / ZIP | 已构建校验的 Android APK 与 Windows x64 发行归档 |
| [docs/assets/](docs/assets) | Assets | 品牌图标、横幅与双端界面视觉资产 |

---

## 发行包下载（`v1.0.7`）

仓库随附最新构建并校验的双端 Release 发行包：

| 平台 | 安装包 / 发行归档 | 版本与运行环境 | 文件大小 | SHA-256 |
| :--- | :--- | :--- | :--- | :--- |
| **Android** | [releases/tonewise-android-release.apk](releases/tonewise-android-release.apk) | `com.shawnshi.tonewise` · `v1.0.7`（`versionCode 107`） | `16,813,006` bytes（~16.03 MiB） | `cfc505e07fff80995dd156556940624c550cd18cb9309f5333b9fb10b253ff58` |
| **Windows x64** | [releases/tonewise-windows-x64-release.zip](releases/tonewise-windows-x64-release.zip) | `v1.0.7` · WinUI 3（`net9.0-windows10.0.26100.0`） | `48,995,125` bytes（~46.73 MiB） | `b734eb434fb30b3b1b85652929cc6462075acb6682597a0d419a328167f7ad63` |

* **Android 签名与升级说明**：已通过 R8 优化、`zipalign` 4 字节对齐与 `apksigner` 校验（覆盖 `arm64-v8a`、`armeabi-v7a`、`x86`、`x86_64`）；当前仓库发行包使用调试签名密钥封装以保持本地覆盖升级一致，正式构建可通过 `TONEWISE_RELEASE_*` 环境变量与 `-PrequireReleaseSigning` 启用独立发布签名。
* **Windows x64 运行说明**：解压 `tonewise-windows-x64-release.zip` 后运行 `Tonewise.Desktop.exe`（需安装 .NET 9 Desktop Runtime；Windows App SDK 1.7、Rust `tonewise_core.dll` 与 `tonewise-cli.exe` 已自包含打包；USB 自动同步支持自动探测 `adb.exe`）。

---

## 界面预览

> 以下预览图展示 Zune / Metro 视觉基线与桌面 / 移动核心布局；`v1.0.7` 手机端已进一步精简主菜单、固定 `collection` 八维子导航并新增黑胶双形态播放舞台。

### Android 移动端

| Quickplay 速播与固定直达墙 | Collection 馆藏与古典索引 | Now Playing 播放舞台与四元组 |
| :---: | :---: | :---: |
| <img src="docs/assets/screenshots/mobile-01-scenes.png" alt="Android Quickplay 速播与固定直达墙" width="260"> | <img src="docs/assets/screenshots/mobile-02-library.png" alt="Android Collection 馆藏与古典索引" width="260"> | <img src="docs/assets/screenshots/mobile-03-playback-classical.png" alt="Android Now Playing 播放舞台与四元组" width="260"> |
| **64-bit 浮点参量均衡与 Auto-EQ** | **五维 Focus 漏斗与快速标定** | **精简版右上角 `···` 工具抽屉** |
| <img src="docs/assets/screenshots/mobile-04-dsp.png" alt="Android 64-bit 浮点参量均衡与 Auto-EQ" width="260"> | <img src="docs/assets/screenshots/mobile-05-focus-quicktag.png" alt="Android 五维 Focus 漏斗与快速标定" width="260"> | <img src="docs/assets/screenshots/mobile-06-sync-hub.png" alt="Android 精简版右上角工具抽屉" width="260"> |

### Windows 11 桌面端

| Quickplay 速播全景与封面直达墙 | Collection 馆藏与古典属性检查器 |
| :---: | :---: |
| <img src="docs/assets/screenshots/desktop-01-library-workbench.png" alt="Windows Quickplay 速播全景与封面直达墙" width="460"> | <img src="docs/assets/screenshots/desktop-02-classical-works.png" alt="Windows Collection 馆藏与古典属性检查器" width="460"> |
| **Now Playing 专辑拼贴画廊与可收起队列** | **Studio 工坊与局域网 / USB 自动同步** |
| <img src="docs/assets/screenshots/desktop-03-scenes-autodj.png" alt="Windows Now Playing 专辑拼贴画廊与可收起队列" width="460"> | <img src="docs/assets/screenshots/desktop-04-lan-sync.png" alt="Windows Studio 工坊与局域网 / USB 自动同步" width="460"> |

---

## Android 权限说明

Tonewise 不包含任何商业分析、崩溃遥测或云端账号服务，所有权限仅用于本地音频与近场同步：

| 权限组 | 具体权限 | 用途说明 |
| :--- | :--- | :--- |
| 本地存储 | `READ_MEDIA_AUDIO`、`READ_EXTERNAL_STORAGE`（`maxSdk 32`） | 读取本地音乐目录、嵌入式封面与 `booklet.pdf` 数码画册 |
| 后台与保活 | `FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_MEDIA_PLAYBACK`、`FOREGROUND_SERVICE_DATA_SYNC`、`WAKE_LOCK` | 保障息屏播放与局域网 / USB 同步传输不中断 |
| 通知栏 | `POST_NOTIFICATIONS` | 展示媒体播控通知与曲库索引进度 |
| 本地网络 | `INTERNET`、`ACCESS_NETWORK_STATE`、`CHANGE_WIFI_MULTICAST_STATE` | 仅用于 `_tonewise._tcp` 局域网同步发现、文件分发与可选联网元数据查询 |

---

## 从源码构建（Windows 11）

构建环境要求：Windows 11、PowerShell 7、JDK 21、CMake 3.22.1+、Ninja、Android NDK `28.2.13676358`、Rust stable 工具链，以及 .NET 9 SDK。

### 1. 克隆仓库、打入 Media3 补丁并编译 `tonewise-core`

```powershell
git -c core.autocrlf=false -c core.symlinks=true -c core.longpaths=true clone --recurse-submodules https://github.com/shawnshi/Tonewise.git
cd Tonewise
.\scripts\apply-media-patch.ps1
.\scripts\build-core-desktop.ps1
.\scripts\build-core-android.ps1
```

### 2. 编译 Android FFmpeg 静态库（MSYS2 UCRT64，首次构建执行一次）

`:musikr` 会自动通过 CMake 编译四架构 TagLib；Media3 FFmpeg 解码器静态库需在 MSYS2 UCRT64 终端执行一次编译：

```bash
pacman -Syu
pacman -S --needed base-devel diffutils nasm perl mingw-w64-ucrt-x86_64-gcc

module="$(cygpath -u "$PWD/android/media/libraries/decoder_ffmpeg/src/main")"
sdk="$(cygpath -u "${ANDROID_HOME:-$LOCALAPPDATA/Android/Sdk}")"
ndk="$sdk/ndk/28.2.13676358"
bash "$module/jni/build_ffmpeg.sh" "$module" "$ndk" windows-x86_64 21 flac alac
```

### 3. 构建与测试 Android 端

```powershell
Set-Location android
.\gradlew.bat :musikr:testDebugUnitTest :app:testDebugUnitTest :app:assembleDebug

# 使用独立发布密钥签名（未配置环境变量时将在 -PrequireReleaseSigning 下快速失败）
.\gradlew.bat :app:assembleRelease -PrequireReleaseSigning

# 本地冒烟验证（使用调试密钥签名）
.\gradlew.bat :app:assembleRelease -PuseDebugSigning
```

### 4. 安装至 Android 真机

```powershell
adb devices
Set-Location android
.\gradlew.bat installDebug
# 或直接覆盖安装 Release APK：
adb install -r ..\releases\tonewise-android-release.apk
```

### 5. 构建与测试 Rust 内核及 Windows 桌面端

```powershell
cargo test --manifest-path core\Cargo.toml --workspace
cargo build --manifest-path core\Cargo.toml --release -p tonewise-ffi -p tonewise-cli
dotnet build desktop\Tonewise.sln -c Release -p:Platform=x64
dotnet test desktop\Tonewise.sln -p:Platform=x64
```

---

## 致谢与开源协议

Tonewise Android 端基于优秀的开源本地播放器 [Auxio](https://github.com/OxygenCobalt/Auxio) 深度演进，并得益于 [Media3 ExoPlayer](https://developer.android.com/guide/topics/media/exoplayer)、TagLib、`lofty` 与 `symphonia` 等开源基础设施。

[![GNU GPLv3 Image](https://www.gnu.org/graphics/gplv3-127x51.png)](https://www.gnu.org/licenses/gpl-3.0.en.html)

本项目基于 [GNU General Public License v3.0 or later](https://www.gnu.org/licenses/gpl-3.0.en.html) 开源发布，你可以自由地使用、研究、分享与改进本软件。
