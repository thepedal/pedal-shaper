# Pedal Shaper v1.0

Stereo waveshaper distortion effect for ReBuzz.

A simple but useful waveshaping pedal with five selectable curves, a
pre-shaper tilt EQ, a DC bias control for asymmetric harmonics, and a
dry/wet mix. The DC blocker on the output keeps things tidy even when
Bias and asymmetric shapes push the signal off-centre.

## Parameters

| Name   | Range  | Default | Notes |
|--------|--------|---------|-------|
| Drive  | 0..96  | 48      | Input drive into the shaper. 48 = 0 dB, each step = 0.5 dB. Range ±24 dB. |
| Shape  | 0..4   | 0       | Selects the waveshaping curve. Soft / Hard / Tube / Fold / Crush. |
| Tone   | 0..127 | 64      | Pre-shaper tilt EQ. 64 = flat. Lower = darker, higher = brighter. |
| Bias   | 0..127 | 64      | DC offset injected before the shaper. 64 = no bias. Drives even-order harmonics. |
| Mix    | 0..127 | 127     | Dry/wet mix. 0 = full dry, 127 = full wet. |
| Output | 0..96  | 48      | Post-shaper makeup gain. 48 = 0 dB, range ±24 dB. |

## Shapes

- **Soft** — `tanh(drive·x)`. Smooth, musical, gentle even at high
  drive. Reach for it when you want warmth without grain.

- **Hard** — pure clamp. Brick-wall clipping with all the high-order
  harmonics intact. Edgy, aggressive — when you want the buzz to bite.

- **Tube** — asymmetric tanh (1.5× positive, 0.6× negative). Models
  the asymmetric saturation of a vacuum-tube stage, generating the
  even-order harmonic content that gives tube amps their warmth. Push
  Bias up or down to taste — small offsets accentuate the asymmetry.

- **Fold** — sine-based wavefolder. Bounded by construction. At low
  drive, near-transparent; as drive climbs past 0 dB the wave starts
  to fold back, producing climbing/descending overtones with a
  characteristic "metallic" character. Great for resonant pad
  textures or harsh leads.

- **Crush** — bit-quantization at ~4-bit resolution. Drive pushes the
  signal harder against the fixed quantization grid. Lo-fi, aliased,
  noisy. Try it on drums or any signal with strong transients.

## Signal flow

```
input → pre-tone tilt → drive gain → bias offset → waveshaper → DC blocker → output gain → dry/wet mix → output
```

Tone shapes the signal **going into** the shaper, so it affects which
frequency content gets distorted. Dial Tone down for a darker
distortion (highs survive cleaner); dial it up to attack the high
end specifically.

The DC blocker after the shaper is automatic and not configurable —
it's there to clean up the DC offset that Bias and asymmetric shapes
introduce, which would otherwise eat headroom and click on bypass
toggles.

## Typical settings

- **Subtle warmth on a bus.** Soft, Drive +6 dB, Tone 64, Mix 64..96,
  Output 0 dB. Just a hint of low-order harmonic content.

- **Tube grit on a vocal/lead.** Tube, Drive +9 dB, Bias 80, Tone 70,
  Mix 100, Output −3 dB. The Bias offset gives the asymmetric "vintage"
  feel; Output trims back to compensate for the boost.

- **Fuzz on a bass line.** Hard, Drive +12 dB, Tone 50 (slightly dark),
  Mix 127, Output −6 dB. Hard clip eats headroom — use Output to bring
  it back.

- **Resonant wavefold lead.** Fold, Drive +15 dB, Tone 80, Mix 127.
  At extreme drive the fold cycle produces dense overtones — automate
  Drive for a building/falling lead.

- **Lo-fi drum bus.** Crush, Drive 0 dB or slightly positive, Mix
  64..96, Output 0 dB. Keep Mix below full wet to preserve transient
  clarity from the dry path.

## Installation

Build the project; the deploy target copies `Pedal Shaper.NET.dll` to
`C:\Program Files\ReBuzz\Gear\Effects\` automatically. Close any
running instance of ReBuzz first — it holds an open handle on every
loaded machine DLL, and the post-build Copy will fail silently
(`ContinueOnError`) otherwise.

## Future work

Not in v1.0; tracked here so future iterations don't have to
rediscover what was deliberately deferred.

- **Oversampling.** All shapes alias to some extent — Hard and Crush
  most aggressively. 2× or 4× oversampling around the shaper stage
  would reduce alias products at the cost of CPU. A `Quality` switch
  parameter is the natural surface.

- **Parameter smoothing.** Drive, Output, Mix change at block rate.
  Audio-rate automation may produce zipper noise; one-pole smoothing
  on these three would fix it without affecting steady-state CPU.

- **Per-channel processing.** v1.0 processes L and R independently
  but with shared parameter values. A "Stereo" / "Mid-Side" / "Mono"
  mode could route differently.

- **More shapes.** Sine fold variants (asymmetric, partial), polynomial
  clippers, foldback with adjustable threshold, RC-coupled tube models,
  diode-clipper emulations. Append-only per Build §3.3 — old presets
  keep working.

- **Drive metering.** A volatile float for input peak and another for
  post-shape RMS would feed a future GUI showing how hard the shaper
  is being pushed.
