# Tonewise Cross-Endpoint DSP Profile Contract (`.tonewise/dsp_profiles/*.txt`)

- **Base Syntax**: Compatible with EqualizerAPO configuration subset (`Preamp:` + `Filter:` directives).
- **Supported Filter Types**:
  - `PK`: Peaking EQ (`Fc <Hz> Gain <dB> Q <val>`)
  - `LSC` / `LS`: Low-Shelf (`Fc <Hz> Gain <dB> Q <val>`)
  - `HSC` / `HS`: High-Shelf (`Fc <Hz> Gain <dB> Q <val>`)
  - `LP` / `LPQ`: Low-Pass (`Fc <Hz> Q <val>`)
  - `HP` / `HPQ`: High-Pass (`Fc <Hz> Q <val>`)
- **Gain-Safety Preamp Rule**:
  - `G_total = RG2_dB - max(0, Peak_EQ_dB)` (or explicit `Preamp:` if lower than `-max(0, Peak_EQ_dB)`).
  - Both PC (`64-bit Float Biquad`) and Android (`RootlessJamesDSP` / built-in float chain) evaluate `Preamp` before cascaded filters to prevent clipping.
- **Bit-Perfect Bypass Precedence**:
  - When USB DAC `MIXER_BEHAVIOR_BIT_PERFECT` is active on Android or ASIO Native DSD is active on Windows, DSP profiles MUST be bypassed unless DSD->DXD conversion is explicitly enabled.
