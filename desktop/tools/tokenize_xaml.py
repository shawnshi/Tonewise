"""Tonewise XAML theme tokenization: typography ramp + contrast fixes + hard-coded color -> ThemeResource."""
import colorsys
import re
import sys

XAML = r"D:\projects\Tonewise\desktop\src\Tonewise.Desktop\MainWindow.xaml"
TOKENS = r"D:\projects\Tonewise\desktop\src\Tonewise.Desktop\Themes\Tokens.xaml"

s = open(XAML, encoding="utf-8").read()

# ---------- 0. namespaces & converter resource ----------
if 'xmlns:local="using:Tonewise.Desktop"' not in s:
    s = s.replace('xmlns:skia="using:SkiaSharp.Views.Windows"',
                  'xmlns:skia="using:SkiaSharp.Views.Windows"\n    xmlns:local="using:Tonewise.Desktop"', 1)
if 'x:Key="SecondsToClockConverter"' not in s:
    s = s.replace('        <Grid.Resources>\n',
                  '        <Grid.Resources>\n            <local:SecondsToClockConverter x:Key="SecondsToClockConverter" />\n', 1)

# ---------- 1. filter strip headers (Chinese) + width for 12px ramp ----------
for old, new in [('Header="SCENE"', 'Header="场景"'), ('Header="ENERGY"', 'Header="能量"'),
                 ('Header="VOCALS"', 'Header="人声"'), ('Header="MOOD 同义簇"', 'Header="情绪"'),
                 ('Header="ENERGY ARC"', 'Header="能量曲线"')]:
    s = s.replace(old, new)
def widen(m):
    return f'<ComboBox Width="{int(m.group(1)) + 18}" Header="{m.group(2)}"'
s = re.sub(r'<ComboBox Width="(\d+)" Header="(场景|能量|人声|情绪|能量曲线)"', widen, s)
s = s.replace('Content="NOT vocal"', 'Content="排除人声"')

# ---------- 2. track table header labels ----------
for old, new in [('Text="TRACK / ALBUM"', 'Text="曲目 / 专辑"'), ('Text="ARTIST"', 'Text="艺人"'),
                 ('Text="COMPOSER / WORK"', 'Text="作曲 / 作品"'), ('Text="CONDUCTOR"', 'Text="指挥"'),
                 ('Text="YEAR"', 'Text="年份"'), ('Text="SCENE / MOOD"', 'Text="场景 / 情绪"'),
                 ('Text="ENERGY · TEMPO"', 'Text="能量 · 节奏"'), ('Text="SPECS / TIME"', 'Text="时长 / 增益"')]:
    s = s.replace(old, new)
s = s.replace('<TextBlock Text="{x:Bind Title}" FontSize="12" FontWeight="SemiBold"',
              '<TextBlock Text="{x:Bind Title}" FontSize="14" FontWeight="SemiBold"')

# ---------- 3. typography ramp: nothing below Caption 12 except 10px badges ----------
ramp = {"9": "10", "10": "12", "11": "12", "13": "14"}
s = re.sub(r'FontSize="(\d+)"', lambda m: f'FontSize="{ramp.get(m.group(1), m.group(1))}"', s)
s = re.sub(r'\s+CharacterSpacing="\d+"', '', s)

# ---------- 4. contrast: tertiary ink #64748B (3.7:1) -> #94A3B8 (6.9:1); placeholder icon #475569 -> #64748B ----------
s = s.replace('Foreground="#64748B"', 'Foreground="#94A3B8"')
s = s.replace('Foreground="#475569"', 'Foreground="#64748B"')

# ---------- 5. tokenization ----------
ROLE_OF_ATTR = {"Foreground": "Fg", "Fill": "Fg", "Stroke": "Fg",
                "Background": "Bg", "PaneBackground": "Bg", "BorderBrush": "Line"}
tokens = {}  # key -> (role, hex)

def token_key(role, hexv):
    key = f"Tw{role}{hexv.upper()}Brush"
    tokens[key] = (role, hexv.upper())
    return key

def attr_sub(m):
    attr, hexv = m.group(1), m.group(2)
    role = ROLE_OF_ATTR[attr]
    return f'{attr}="{{ThemeResource {token_key(role, hexv)}}}"'

s = re.sub(r'\b(Foreground|Fill|Stroke|Background|PaneBackground|BorderBrush)="#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})"', attr_sub, s)

def setter_sub(m):
    prop, hexv = m.group(1), m.group(2)
    role = ROLE_OF_ATTR.get(prop)
    if role is None:
        return m.group(0)
    return f'<Setter Property="{prop}" Value="{{ThemeResource {token_key(role, hexv)}}}"'

s = re.sub(r'<Setter Property="(\w+)" Value="#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})"', setter_sub, s)

left = re.findall(r'"#[0-9A-Fa-f]{6,8}"', s)
open(XAML, "w", encoding="utf-8").write(s)

# ---------- 6. Tokens.xaml generation ----------
def split_hex(h):
    if len(h) == 8:
        return h[:2], h[2:]
    return None, h

def to_rgb(h6):
    return tuple(int(h6[i:i + 2], 16) / 255 for i in (0, 2, 4))

def to_hex(rgb):
    return "".join(f"{max(0, min(255, round(c * 255))):02X}" for c in rgb)

def light_variant(role, h6):
    r, g, b = to_rgb(h6)
    h, l, sat = colorsys.rgb_to_hls(r, g, b)
    nl = 1.0 - l
    if role == "Fg" and sat > 0.25:
        nl = min(nl, 0.36)          # saturated accents must stay dark on light canvas (>= 4.5:1)
    if role == "Bg" and sat > 0.25 and l > 0.45:
        nl = min(nl, 0.38)          # gold action fill -> deep gold, paired with inverted light text
    return to_hex(colorsys.hls_to_rgb(h, nl, sat))

def hc_color(role, h6, alpha):
    if role == "Fg":
        return "SystemColorHighlightTextColor" if h6 == "131720" else "SystemColorWindowTextColor"
    if role == "Bg":
        if h6 == "D9B372":
            return "SystemColorHighlightColor"
        return "SystemColorWindowColor"
    return "SystemColorWindowTextColor"

def brush_lines(theme):
    out = []
    for key in sorted(tokens):
        role, hexv = tokens[key]
        alpha, h6 = split_hex(hexv)
        if theme == "Default":
            col = f"#{hexv}"
            out.append(f'                <SolidColorBrush x:Key="{key}" Color="{col}" />')
        elif theme == "Light":
            col = f"#{(alpha or '')}{light_variant(role, h6)}"
            out.append(f'                <SolidColorBrush x:Key="{key}" Color="{col}" />')
        else:
            out.append(f'                <SolidColorBrush x:Key="{key}" Color="{{ThemeResource {hc_color(role, h6, alpha)}}}" />')
    return "\n".join(out)

tokens_xaml = f'''<?xml version="1.0" encoding="utf-8"?>
<!--
  Tonewise semantic color tokens (generated by tokenize_xaml.py; edit the generator, not this file).
  Default = obsidian dark design field; Light = lightness-inverted counterpart with accent clamping;
  HighContrast = Windows contrast-theme system colors (applied automatically when a contrast theme is on).
-->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- Brand accent (gold) feeding AccentButtonStyle, ToggleSwitch, Slider, NavigationView indicator, ProgressBar -->
    <Color x:Key="SystemAccentColor">#B8924F</Color>
    <Color x:Key="SystemAccentColorLight1">#C9A462</Color>
    <Color x:Key="SystemAccentColorLight2">#D9B372</Color>
    <Color x:Key="SystemAccentColorLight3">#E8CC98</Color>
    <Color x:Key="SystemAccentColorDark1">#7E6029</Color>
    <Color x:Key="SystemAccentColorDark2">#664D20</Color>
    <Color x:Key="SystemAccentColorDark3">#4D3A18</Color>

    <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Default">
{brush_lines("Default")}
        </ResourceDictionary>
        <ResourceDictionary x:Key="Light">
{brush_lines("Light")}
        </ResourceDictionary>
        <ResourceDictionary x:Key="HighContrast">
{brush_lines("HighContrast")}
        </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>
</ResourceDictionary>
'''
import os
os.makedirs(os.path.dirname(TOKENS), exist_ok=True)
open(TOKENS, "w", encoding="utf-8").write(tokens_xaml)
print(f"tokens={len(tokens)} remaining_hex_literals={len(left)} {sorted(set(left))[:10]}")
