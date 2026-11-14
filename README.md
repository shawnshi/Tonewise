<p align="center"><img src="fastlane/metadata/android/en-US/images/icon.png" width="150"></p>
<h1 align="center"><b>Auxio</b></h1>
<h4 align="center">A simple, rational music player for Android.</h4>
<p align="center">
    <a href="https://github.com/oxygencobalt/Auxio/releases/tag/v4.1.5">
        <img alt="Latest Version" src="https://img.shields.io/static/v1?label=tag&message=v4.1.5&color=64B5F6&style=flat">
    </a>
    <a href="https://github.com/oxygencobalt/Auxio/releases/">
        <img alt="Releases" src="https://img.shields.io/github/downloads/OxygenCobalt/Auxio/total.svg?color=4B95DE&style=flat">
    </a>
    <a href="https://www.gnu.org/licenses/gpl-3.0">
        <img src="https://img.shields.io/badge/license-GPL%20v3-2B6DBE.svg?style=flat">
    </a>
    <img alt="Minimum SDK Version" src="https://img.shields.io/badge/API-24%2B-1450A8?style=flat">
</p>
<h4 align="center"><a href="/CHANGELOG.md">Changelog</a> | <a href="https://github.com/OxygenCobalt/Auxio/wiki">Wiki</a> | <a href="https://github.com/OxygenCobalt/Auxio#Donate">Donate</a></h4>
<p align="center">
    <a href="https://f-droid.org/app/org.oxycblt.auxio"><img src="https://fdroid.gitlab.io/artwork/badge/get-it-on.png" width="250"></a>
    <a href="https://accrescent.app/app/org.oxycblt.auxio">
        <img alt="Get it on Accrescent" src="https://accrescent.app/badges/get-it-on.png" width="250">
    </a>
</p>
<p align="center">
    <a href="https://hosted.weblate.org/engage/auxio/"><img height=64 src="https://hosted.weblate.org/widgets/auxio/-/strings/287x66-grey.png" alt="Translation status" /></a>
</p>

## About
Tonewise 知律 
​
懂作品的章法，亦懂入景的直觉。
Structured for works. Built for scenes. Bit-perfect to the ear.

流媒体时代不缺音乐入口，缺的是一套真正尊重专辑叙事与录音本貌的本地听音器物。通用播放器往往按流行单曲逻辑设计：交响曲的完整乐章被随机打碎，爵士三重奏里的伴奏名家无处检索，精心抓轨的数百张无损专辑最终困在繁琐的目录层级里吃灰。
​Tonewise（知律） 是一款专为古典、爵士与摇滚本地无损曲库打造的开源 Android 音乐播放器（GPL-3.0）。它在底层构建独立的 TagLib 图谱引擎，支持将古典曲目按“作曲家—作品编号—乐章”四元衬线排版呈现，聚合同一作品的不同指挥与独奏录音版本，并完整保留爵士伴奏乐手网络、原盘数字小册子（Booklet PDF）与摇滚首版编年。面对选歌疲劳，Tonewise 将本地 .m3u8 歌单与情绪规则封装为全幅场景横幅与多会话持久队列——点按横幅即刻入景起播，上拉底部悬浮胶囊即可在深夜古典、通勤爵士与现场摇滚多条断点队列间毫秒级切换。
​在声音还原上，Tonewise 坚持全链路透明：内置 150 点静态包络波形进度条让长篇乐章的高潮与弱音一目了然，打通 32-bit 浮点防截断管线与 Android 原生 USB DAC 无损直通，并通过实时信号路径面板如实展示每一级采样率与增益状态，按输出设备自动协同外置参量均衡。零联网追踪、零推荐干扰、零花哨动效；横置桌面接入解码耳放时，它只是一方安静显示时间与原画封套的听音伴侣。


**The default branch is the development version of the repository. For a stable version, see the master branch.**

## Screenshots

<p align="center">
    <img src="fastlane/metadata/android/en-US/images/phoneScreenshots/shot0.png" width=250>
    <img src="fastlane/metadata/android/en-US/images/phoneScreenshots/shot1.png" width=250>
    <img src="fastlane/metadata/android/en-US/images/phoneScreenshots/shot2.png" width=250>
    <img src="fastlane/metadata/android/en-US/images/phoneScreenshots/shot3.png" width=250>
    <img src="fastlane/metadata/android/en-US/images/phoneScreenshots/shot4.png" width=250>
    <img src="fastlane/metadata/android/en-US/images/phoneScreenshots/shot5.png" width=250>
</p>


## Features

- Playback based on [Media3 ExoPlayer](https://developer.android.com/guide/topics/media/exoplayer)
- Snappy UI derived from the latest Material Design guidelines
- Opinionated UX that prioritizes ease of use over edge cases
- Customizable behavior
- Support for disc numbers, multiple artists, release types,
precise/original dates, sort tags, and more
- Advanced artist system that unifies artists and album artists
- SD Card-aware folder management
- Reliable playlisting functionality
- Playback state persistence
- Android Auto support
- Automatic gapless playback
- Full ReplayGain support (On MP3, FLAC, OGG, OPUS, and MP4 files)
- External equalizer support (ex. Wavelet)
- Edge-to-edge
- Embedded covers support
- Search functionality
- Headset autoplay
- Stylish widgets that automatically adapt to their size
- Completely private and offline
- No rounded album covers (if you want them)

## Permissions

- Storage (`READ_MEDIA_AUDIO`, `READ_EXTERNAL_STORAGE`) to read and play your music files
- Services (`FOREGROUND_SERVICE`, `WAKE_LOCK`) to keep the music playing in the background
- Notifications (`POST_NOTIFICATION`) to indicate ongoing playback and music loading

## Donate

You can support Auxio's development through [my Github Sponsors page](https://github.com/sponsors/OxygenCobalt). Get the ability to prioritize features and have your profile added to the README, Release Changelogs, and even the app itself!

<h3 align="center">Legendary supporters:</h3>
<p align="center">
    <a href="https://github.com/bkkellyh"><img src="https://avatars.githubusercontent.com/u/248118457?v=4" width=250 /></a> 
    <a href="ttps://github.com/gruntstumper"><img src="https://avatars.githubusercontent.com/u/308816080?v=4" width=125 /></a> 
</p>
<h3 align="center">
    <a href="https://github.com/bkkellyh">@bkkellyh</a> - <i>$500!</i>, <a href="https://github.com/gruntstumper">@gruntstumper</a> - $100
</h3>

<hr />

<p align="center"><b>$8/month supporters:</b></p>

<p align="center">
    <a href="https://github.com/uku3lig"><img src="https://avatars.githubusercontent.com/u/61147779?v=4" width=50 /></a>
</p>
<p align="center">
    And **1** Private Sponsor!
</p>

## Building

Auxio relies on a patched version of Media3 that enables some extra playback features, alongside taglib for metadata
parsing. This adds some caveats to the build process:
1. `cmake` and `ninja-build` must be installed before building the project.
2. The project uses submodules, so when cloning initially, use `git clone --recurse-submodules` to properly
download the external code.
3. You are **unable** to build this project on windows, as the custom Media3 build runs shell scripts that
will only work on unix-based systems.

### Set up Android Studio

#### Install Android Studio.

```bash
pkg -S android-studio
```

#### Configuring Android Studio:

- Be sure to have NDK tools, version 28.2.13676358. You can search it on Languages & Frameworks > Android SDK.
- Install Java-21 with your system package manager

    ```bash
    sudo pkg -S jdk21-openjdk
    ```
    Additionally: Set java version to jdk21-openjdk

- Run ./gradlew assembleDebug

#### Connecting to your Android Device

You can connect your Mobile Phone through USB to run the app. 

1. **Enable Developer Options on your phone**
   - Go to **Settings > About phone**  
   - Tap **Build number** 7 times until you see *"You are now a developer!"*

2. **Enable USB debugging**
   - Go to **Settings > Developer options**  
   - Turn on **USB debugging**

3. **Connect your phone to the computer**
   - Use a USB cable  
   - On your phone, accept the *Allow USB debugging?* prompt

4. **Verify that your device is detected**
   ```bash
   cd ~/Android/Sdk/platform-tools
   ./adb devices
   ```

Android Studio also offers virtual devices that come with this pre-configured.

#### Install the app on the Android Phone
To install the app on your physical device or emulator, run this command:

```bash
./gradlew installDebug
```

Auxio should now appear in the list of Apps

#### Load music to Auxio (Optional)

You can move files from your pc to your device / emulator to test the music using this command:

```bash
cd ~/Android/Sdk/platform-tools
./adb push ~Music/ /sdcard/Music
```

## Contributing

Auxio accepts most contributions as long as they follow the [Contribution Guidelines](/.github/CONTRIBUTING.md).

However, feature additions and major UI changes are less likely to be accepted. See
[Why Are These Features Missing?](https://github.com/OxygenCobalt/Auxio/wiki/Why-Are-These-Features-Missing%3F)
for more information.



## License

[![GNU GPLv3 Image](https://www.gnu.org/graphics/gplv3-127x51.png)](http://www.gnu.org/licenses/gpl-3.0.en.html)

Auxio is Free Software: You can use, study share and improve it at your
will. Specifically you can redistribute and/or modify it under the terms of the
[GNU General Public License](https://www.gnu.org/licenses/gpl.html) as
published by the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

More information can be found [here](https://github.com/OxygenCobalt/Auxio/wiki/Licenses).
