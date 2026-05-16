# Pedal Shaper v1.2

Stereo waveshaper distortion effect for ReBuzz.

Five selectable distortion curves, a pre-shaper tilt EQ, a DC bias
control for asymmetric harmonics, a dry/wet mix, and two **texture**
controls (new in v1.1) that give the waveshaper time-domain memory
and signal-correlated noise — features that the rest of the Pedal
series doesn't cover. A DC blocker on the output keeps things tidy
even when Bias and asymmetric shapes push the signal off-centre.

## Parameters

| Name       | Range  | Default | Notes |
|------------|--------|---------|-------|
| Drive      | 0..96  | 48      | Input drive into the shaper. 48 = 0 dB, each step = 0.5 dB. Range ±24 dB. |
| Shape      | 0..4   | 0       | Selects the waveshaping curve. Soft / Hard / Tube / Fold / Crush. |
| Tone       | 0..127 | 64      | Pre-shaper tilt EQ. 64 = flat. Lower = darker, higher = brighter. |
| Bias       | 0..127 | 64      | DC offset injected before the shaper. 64 = no bias. Drives even-order harmonics. |
| Mix        | 0..127 | 127     | Dry/wet mix. 0 = full dry, 127 = full wet. |
| Output     | 0..96  | 48      | Post-shaper makeup gain. 48 = 0 dB, range ±24 dB. |
| Hysteresis | 0..127 | 0       | *v1.1* — Tape-style release smear. 0 = off (transparent). |
| Dust       | 0..127 | 0       | *v1.1* — Signal-correlated noise grain. 0 = off (transparent). |

Hysteresis and Dust default to 0 so v1.0 preset bundles loaded into
v1.1 sound bit-identical.

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

## Texture (v1.1)

The shapes above are *memoryless* — output at any sample depends only
on input at that sample. Hysteresis and Dust both add the kind of
small-scale, signal-correlated, time-varying behaviour that real
analog gear has and that pure waveshaping can't produce. They sit in
unclaimed territory relative to the rest of the Pedal lineup — neither
is a compressor, EQ, delay, or modulation effect.

- **Hysteresis** — applies an asymmetric one-pole lag after the
  shaper: fast on rising slope, slow on falling slope. This is what
  magnetic tape, audio transformers, and ferrite cores do at the
  macro level, and it's the single most "analog-feeling" feature the
  pedal can offer. Range is approximately 0 → 1 ms rise / 10 ms fall
  at maximum. Audibly, transients still come through with their shape
  intact but release tails smear out — kick drums get fatter, bass
  notes get rounder, the whole signal sits back a touch.

  Try it at moderate settings (30–60) on top of any shape; the
  character shift is subtle but consistent. At high settings (90+)
  the smear becomes a feature in its own right and works well on
  sustained material.

- **Dust** — injects signal-correlated white noise *before* the
  shaper. The dust amplitude is driven by an envelope follower on
  the dry input (fast attack, slow release — roughly 5 ms / 200 ms)
  rather than the instantaneous waveform, so the noise sits on top
  of the program material like a vinyl-record layer: it comes in
  with the music, hangs on briefly after notes end, and disappears
  during true silence. Because the noise is added pre-shaper, it
  gets distorted along with the signal — on Crush it sounds like
  a corrupted ADC, on Tube it sounds like valve hiss, on Fold it
  sounds like a malfunctioning ring modulator.

  Tracking from the dry input (rather than the post-drive,
  post-bias internal signal) means Drive and Bias don't change when
  dust appears — silence with non-default Bias stays silent.

  The L and R PRNG streams are independently seeded so the dust also
  gives free stereo decorrelation: a mono source through Pedal Shaper
  with Dust at any non-zero setting comes out subtly stereo.

The two combine well: low-to-moderate Hysteresis plus moderate Dust
gives a convincing tape-saturation impression on almost any source.

## Signal flow (v1.2)

```
input ─┬─────────────────────────────────────────────────────────────────────┐
       ├─ env follower (v1.2) ──┐                                            │
       └─ tone tilt ─ drive ─ bias ─┴─ +dust ─ shaper ─ hysteresis ─ DC ─ out ─ mix ─→ output
                                                                            ↑
                                                                       (dry path)
```

The dry path skips everything between the input split and the mixer,
so Mix=0 still gives bypass-equivalent output even with texture
controls dialed in. The Dust envelope follower (v1.2) sits on the
dry input, which is why Drive and Bias don't affect when dust appears.

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

- **Tape saturation (v1.1).** Soft, Drive +3 dB, Tone 60, Mix 100,
  Hysteresis 60, Dust 30, Output 0 dB. The hysteresis lag plus the
  signal-correlated dust together emulate the smoothing-plus-noise
  of a quarter-inch tape stage. Sits very naturally on full mixes.

- **Tube grit on a vocal/lead.** Tube, Drive +9 dB, Bias 80, Tone 70,
  Mix 100, Output −3 dB. The Bias offset gives the asymmetric
  "vintage" feel; Output trims back to compensate for the boost. Add
  Dust ~25 for a touch of valve-hiss authenticity.

- **Fuzz on a bass line.** Hard, Drive +12 dB, Tone 50 (slightly dark),
  Mix 127, Output −6 dB. Hard clip eats headroom — use Output to bring
  it back. Hysteresis 40 takes the edges off without softening the
  attack.

- **Vinyl-style lo-fi.** Tube, Drive +9 dB, Bias 80, Hysteresis 40,
  Dust 80, Output −3 dB. Modest tube grit with prominent vinyl-style
  noise tracking the signal. Bypass-toggle to A/B against the raw
  source — the difference is exactly the kind of imperfection that
  digital recordings lack.

- **Resonant wavefold lead.** Fold, Drive +15 dB, Tone 80, Mix 127.
  At extreme drive the fold cycle produces dense overtones — automate
  Drive for a building/falling lead.

- **Lo-fi drum bus.** Crush, Drive 0 dB or slightly positive, Mix
  64..96, Output 0 dB. Keep Mix below full wet to preserve transient
  clarity from the dry path.

- **Spooky cinema texture (v1.1).** Crush, Drive 0 dB, Hysteresis 100,
  Dust 60, Mix 64. Heavy tape lag on top of a quantized signal sounds
  like a damaged sampler — sustained pads become unrecognisable
  textural material.

## Installation

Build the project; the deploy target copies `Pedal Shaper.NET.dll` to
`C:\Program Files\ReBuzz\Gear\Effects\` automatically. Close any
running instance of ReBuzz first — it holds an open handle on every
loaded machine DLL, and the post-build Copy will fail silently
(`ContinueOnError`) otherwise.

## Future work

Not in v1.1; tracked here so future iterations don't have to
rediscover what was deliberately deferred.

- **Oversampling.** All shapes alias to some extent — Hard and Crush
  most aggressively. 2× or 4× oversampling around the shaper stage
  would reduce alias products at the cost of CPU. A `Quality` switch
  parameter is the natural surface.

- **Parameter smoothing.** Drive, Output, Mix change at block rate.
  Audio-rate automation may produce zipper noise; one-pole smoothing
  on these three would fix it without affecting steady-state CPU.

- **More texture options.** v1.1 covers tape-feel (Hysteresis) and
  vinyl-feel (Dust). The brainstorm from the v1.1 design discussion
  also flagged envelope-coupled shape morph ("louder = grittier"),
  asymmetric per-channel drive (stereo decorrelation through shape
  rather than just noise), and crackle-impulse layers — each would
  append cleanly after Dust per Build §3.3.

- **More shapes.** Sine fold variants (asymmetric, partial), polynomial
  clippers, foldback with adjustable threshold, RC-coupled tube models,
  diode-clipper emulations. Append-only per Build §3.3 — old presets
  keep working.

- **Drive metering.** A volatile float for input peak and another for
  post-shape RMS would feed a future GUI showing how hard the shaper
  is being pushed.

## Changelog

- **v1.2** — Dust amplitude detector revised. The v1.1 implementation
  scaled noise by √|drL| (the instantaneous post-drive, post-bias
  signal magnitude), which had two problems: noise tracked the audio
  waveform per-sample rather than feeling like a layer over it, and
  non-default Bias settings caused dust to remain audible at silence
  (since `drL` retained a DC offset from Bias). v1.2 replaces this
  with an asymmetric envelope follower (≈5 ms attack / 200 ms
  release) on the *dry* input magnitude — dust now rides on top of
  the program material with a natural noise-tail after notes, and is
  silent in true silence regardless of Bias. No parameter changes;
  v1.1 presets load unchanged but those with Dust > 0 will sound
  different (less waveform-correlated, more layer-like).
- **v1.1** — added Hysteresis (asymmetric one-pole lag, tape-style
  release smear) and Dust (signal-correlated PRNG noise, independently
  seeded per channel). Both default to 0, so v1.0 presets load
  unchanged.
- **v1.0** — initial release: 5 shapes, tone tilt, bias, mix, output.
