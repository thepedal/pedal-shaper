using System;

// Both SDK usings are mandatory on the main machine file (Build §6.2).
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl,
                               // ParameterDecl, Sample, WorkModes
using BuzzGUI.Interfaces;      // IMachine, IBuzz (reached through .Graph.Buzz)

namespace PedalShaper
{
    // Pedal Shaper — stereo waveshaping distortion.
    //
    // Effect machine: bool Work(Sample[] output, Sample[] input, int n,
    // WorkModes mode).  Signal flow per sample (PedalComp §1):
    //
    //     input (±32768)        ── multiply by SCALE  ─→  dry (±1.0)
    //                            ↓                          ↓
    //                            ↓        dust env follower (v1.2)
    //                            ↓               ↓
    //         pre-tone tilt (one-pole split, mix low/high bands)
    //                            ↓
    //         drive_lin gain  +  Bias DC offset
    //                            ↓
    //         + √env · dust noise          ← v1.1 (off when Dust=0)
    //                            ↓             (v1.2 — env-driven; was |x|-driven)
    //         waveshaper (Soft / Hard / Tube / Fold / Crush)
    //                            ↓
    //         hysteresis (asymmetric one-pole lag)  ← v1.1 (off when Hysteresis=0)
    //                            ↓
    //         DC blocker (removes Bias + asymmetric-shape DC drift)
    //                            ↓
    //         output_lin gain
    //                            ↓
    //         dry/wet mix against the original input
    //                            ↓
    //     out (±1.0)            ── divide by SCALE ────→ output (±32768)
    //
    // All DSP runs at the normalised ±1.0 scale per PedalComp §1.
    // Coefficient updates are gated on the dirty-check pattern from
    // PedalComp §6 so Exp/Pow/Log calls don't enter the per-sample hot
    // path on unchanged buffers.
    //
    // All bipolar / signed parameters are encoded with the non-negative
    // offset trick per PedalComp §2 — every property setter receives a
    // value in [MinValue..MaxValue] with the signed reading reconstructed
    // by subtracting an offset.
    //
    // v1.1 texture additions (Hysteresis, Dust) appended to the parameter
    // list per Build §3.3 — v1.0 preset bundles load with both at their
    // DefValue of 0, producing v1.0-identical output bit-for-bit.
    //
    // v1.2 revises the Dust amplitude detector: an envelope follower on
    // the dry-input magnitude (fast attack, slow release) replaces v1.1's
    // instantaneous √|drL|.  Audible result is dust that rides on top of
    // the program material with a natural noise-tail after notes end,
    // rather than v1.1's per-sample modulation that hugged the waveform
    // and leaked through silence when Bias was non-default.  No parameter
    // changes — v1.1 presets load unchanged, but presets with Dust > 0
    // sound different (less waveform-correlated, more layer-like).
    [MachineDecl(
        Name        = "Pedal Shaper",
        ShortName   = "Shaper",
        Author      = "ReBuzz / Pedal series",
        MaxTracks   = 0,           // no per-track parameters
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalShaperMachine : IBuzzMachine
    {
        // ── Conventions / constants ───────────────────────────────────────

        // ReBuzz delivers samples at ±32768 (PedalComp §1).  All internal
        // DSP runs at ±1.0; SCALE in / SCALE out converts.
        const float SCALE     = 1f / 32768f;
        const float UNSCALE   = 32768f;

        // Tilt-filter reference frequency.  Roughly mid-band; tunes the
        // perceived "darker / brighter" balance.
        const float TONE_REF_HZ = 1000f;

        // Drive / Output offset (PedalComp §2 pattern).  48 = 0 dB,
        // each step = 0.5 dB ⇒ ±24 dB range over 0..96.
        const int   GAIN_OFFSET    = 48;
        const float GAIN_DB_PER_STEP = 0.5f;

        // Bias offset.  64 = zero bias, each step = 1/64 of full scale ⇒
        // ±1.0 range (in ±1.0-scale samples) over 0..127.  In practice
        // anything beyond ±0.5 pushes most signal hard against the shaper
        // ceiling, so the useful range is much narrower than the raw knob.
        const int   BIAS_OFFSET    = 64;
        const float BIAS_PER_STEP  = 1f / 64f;

        // Tone offset.  64 = flat (low_gain == high_gain == 1.0).
        const int   TONE_OFFSET    = 64;

        // Dust envelope-follower time constants (v1.2).  The follower
        // smooths the dry-input magnitude into the level the dust gain
        // tracks against, so dust feels like a layer over the program
        // material rather than per-sample-|x| modulation tied to the
        // waveform.  Attack is short enough to keep transients audible
        // through the noise; release is long enough that the noise tail
        // outlives short notes (the "ADSR overlap" character).
        // These are fixed — not user-facing parameters — to keep the
        // pedal surface small.  Future v1.x could expose them if needed.
        const float DUST_ATTACK_MS  = 5f;
        const float DUST_RELEASE_MS = 200f;

        // ── Host & per-channel state ──────────────────────────────────────

        readonly IBuzzMachineHost _host;
        // Distinct seeds so the L/R xorshift32 streams (Filters.cs) are
        // statistically independent.  Two arbitrary non-zero 32-bit
        // constants — any pair with high Hamming distance works.
        readonly ChannelFilters _chL = new ChannelFilters(0xC0DEC0DEu);
        readonly ChannelFilters _chR = new ChannelFilters(0xDEADBEEFu);

        // ── Cached / dirty-checked coefficients (PedalComp §6) ────────────
        int   _cachedSr         = 0;
        int   _cachedDrive      = -1;
        int   _cachedOutput     = -1;
        int   _cachedTone       = -1;
        int   _cachedBias       = -1;
        int   _cachedMix        = -1;
        int   _cachedHysteresis = -1;   // v1.1
        int   _cachedDust       = -1;   // v1.1

        float _driveLin   = 1f;       // amplitude multiplier from Drive
        float _outputLin  = 1f;       // amplitude multiplier from Output
        float _biasLin    = 0f;       // additive DC offset in ±1.0 scale
        float _toneLowGain  = 1f;     // tilt low-band weight
        float _toneHighGain = 1f;     // tilt high-band weight
        float _mixDry     = 0f;       // 0..1 dry coefficient
        float _mixWet     = 1f;       // 0..1 wet coefficient
        float _toneLpCoef = 0.1f;     // pushed into both ChannelFilters
        float _hystUpCoef   = 1f;     // v1.1 — 1.0 at Hysteresis=0 (transparent)
        float _hystDownCoef = 1f;     // v1.1 — 1.0 at Hysteresis=0 (transparent)
        float _dustGain     = 0f;     // v1.1 — 0   at Dust=0       (transparent)
        float _dustAttackCoef  = 0f;  // v1.2 — buffer-rate; depends only on sr
        float _dustReleaseCoef = 0f;  // v1.2 — buffer-rate; depends only on sr

        // ── Constructor — store the host, init filter state ───────────────
        // Per Core §15, ParameterGroups is NOT yet populated here.  Stick
        // to plain field init and host capture.
        public PedalShaperMachine(IBuzzMachineHost host)
        {
            _host = host;
            _chL.Reset();
            _chR.Reset();
        }

        // ───────────────────────────────────────────────────────────────────
        // Parameter declarations
        //
        // Order matters — preset-bundle compatibility keys off
        // (Group, Index) per Build §3.3.  Append-only for any v1.x.
        // ───────────────────────────────────────────────────────────────────

        // 1. Drive — input gain feeding the shaper.  Stored 0..96, 48 = 0 dB.
        [ParameterDecl(
            Name         = "Drive",
            Description  = "Input drive into shaper (48 = 0 dB, range ±24 dB)",
            MinValue     = 0,
            MaxValue     = 96,
            DefValue     = 48)]
        public int Drive { get; set; } = 48;

        // 2. Shape — selects the waveshaping curve.
        [ParameterDecl(
            Name              = "Shape",
            Description       = "Waveshaping curve",
            MinValue          = 0,
            MaxValue          = 4,
            DefValue          = 0,
            ValueDescriptions = new[] { "Soft", "Hard", "Tube", "Fold", "Crush" })]
        public int Shape { get; set; } = 0;

        // 3. Tone — pre-shaper tilt EQ.  64 = flat, <64 darker, >64 brighter.
        [ParameterDecl(
            Name         = "Tone",
            Description  = "Pre-shaper tilt EQ (64 = flat)",
            MinValue     = 0,
            MaxValue     = 127,
            DefValue     = 64)]
        public int Tone { get; set; } = 64;

        // 4. Bias — DC offset injected before the shaper, for asymmetric
        //    harmonic content.  64 = zero, ±64 = ±1.0 in normalised scale.
        [ParameterDecl(
            Name         = "Bias",
            Description  = "Pre-shaper DC bias (64 = none, ±64 = ±1.0)",
            MinValue     = 0,
            MaxValue     = 127,
            DefValue     = 64)]
        public int Bias { get; set; } = 64;

        // 5. Mix — dry/wet.  0 = full dry (effect bypassed musically), 127 = full wet.
        [ParameterDecl(
            Name         = "Mix",
            Description  = "Dry/wet mix (0 = full dry, 127 = full wet)",
            MinValue     = 0,
            MaxValue     = 127,
            DefValue     = 127)]
        public int Mix { get; set; } = 127;

        // 6. Output — post-shaper makeup gain.  Stored 0..96, 48 = 0 dB.
        [ParameterDecl(
            Name         = "Output",
            Description  = "Output makeup gain (48 = 0 dB, range ±24 dB)",
            MinValue     = 0,
            MaxValue     = 96,
            DefValue     = 48)]
        public int Output { get; set; } = 48;

        // ── v1.1 texture controls ─────────────────────────────────────────
        // Both default to 0 (off).  v1.0 preset bundles that don't mention
        // these parameters load with their DefValue, which yields output
        // bit-identical to v1.0 behaviour.  Per Build §3.3, the append-
        // only rule preserves the preset contract; future v1.x texture
        // additions should follow the same pattern below these.

        // 7. Hysteresis — asymmetric one-pole lag on the shaper output.
        //    Fast on rising, slow on falling — models the tape/transformer
        //    "release smear" character.  At 0, both up/down coefs are 1.0
        //    and the stage is mathematically transparent (no audible LP).
        [ParameterDecl(
            Name         = "Hysteresis",
            Description  = "Tape-like asymmetric release smear (0 = off)",
            MinValue     = 0,
            MaxValue     = 127,
            DefValue     = 0)]
        public int Hysteresis { get; set; } = 0;

        // 8. Dust — signal-correlated white noise injected before the
        //    shaper.  Amplitude is scaled by √|x| so silence stays silent
        //    and louder passages get progressively more grain.  The L/R
        //    PRNG streams are independently seeded (see field init) so
        //    the dust gives free stereo decorrelation even on mono input.
        //    At 0, the dust gain is exactly 0 — no contribution, transparent.
        [ParameterDecl(
            Name         = "Dust",
            Description  = "Signal-correlated noise grain (0 = off, tracks |x|)",
            MinValue     = 0,
            MaxValue     = 127,
            DefValue     = 0)]
        public int Dust { get; set; } = 0;

        // ───────────────────────────────────────────────────────────────────
        // Coefficient cache (PedalComp §6)
        //
        // Recompute only when any input changes; the per-buffer no-op
        // path is just a handful of integer comparisons.
        // ───────────────────────────────────────────────────────────────────
        void UpdateCoefficients(int sr)
        {
            if (sr == _cachedSr
                && Drive       == _cachedDrive
                && Output      == _cachedOutput
                && Tone        == _cachedTone
                && Bias        == _cachedBias
                && Mix         == _cachedMix
                && Hysteresis  == _cachedHysteresis
                && Dust        == _cachedDust)
                return;

            bool srChanged       = (sr != _cachedSr);
            bool hystChanged     = (Hysteresis != _cachedHysteresis);

            _cachedSr         = sr;
            _cachedDrive      = Drive;
            _cachedOutput     = Output;
            _cachedTone       = Tone;
            _cachedBias       = Bias;
            _cachedMix        = Mix;
            _cachedHysteresis = Hysteresis;
            _cachedDust       = Dust;

            // Drive / Output dB → linear.  Done at coefficient time, so
            // the cost of MathF.Pow is paid once per parameter change,
            // not per sample.
            float driveDb  = (Drive  - GAIN_OFFSET) * GAIN_DB_PER_STEP;
            float outputDb = (Output - GAIN_OFFSET) * GAIN_DB_PER_STEP;
            _driveLin  = MathF.Pow(10f, driveDb  / 20f);
            _outputLin = MathF.Pow(10f, outputDb / 20f);

            // Bias.  Signed-around-64.  ±1.0 max excursion in
            // normalised-sample-scale terms.
            _biasLin = (Bias - BIAS_OFFSET) * BIAS_PER_STEP;

            // Tone tilt gains.  At Tone=64 both gains are 1.0 (flat).
            // The 1.5× spread chosen so the extremes give a clearly
            // audible tilt but never go fully silent in either band —
            // 0.25 / 1.75 at the extremes is ≈ −12 dB / +5 dB.
            float toneN = (Tone - TONE_OFFSET) / (float)TONE_OFFSET;   // −1..+1
            _toneLowGain  = 1f - toneN * 0.75f;
            _toneHighGain = 1f + toneN * 0.75f;

            // Mix.  Linear crossfade; equal-power isn't needed because
            // the dry and wet are correlated (wet is derived from dry).
            float mixN = Mix / 127f;
            _mixDry = 1f - mixN;
            _mixWet = mixN;

            // Tone filter coefficient — only recompute on actual SR change.
            // The cutoff is fixed; only its discretisation depends on sr.
            // Dust envelope-follower coefs (v1.2) live in the same block
            // for the same reason: they're sample-rate-dependent only.
            if (srChanged && sr > 0)
            {
                _toneLpCoef = 1f - MathF.Exp(-2f * MathF.PI * TONE_REF_HZ / sr);
                _chL.SetToneCoef(_toneLpCoef);
                _chR.SetToneCoef(_toneLpCoef);

                float attSamps = DUST_ATTACK_MS  * sr * 0.001f;
                float relSamps = DUST_RELEASE_MS * sr * 0.001f;
                _dustAttackCoef  = (attSamps > 0.001f) ? (1f - MathF.Exp(-1f / attSamps)) : 1f;
                _dustReleaseCoef = (relSamps > 0.001f) ? (1f - MathF.Exp(-1f / relSamps)) : 1f;
            }

            // ── v1.1 ───────────────────────────────────────────────────────
            // Hysteresis — map the 0..127 knob to up/down time constants.
            //
            //   upMs ranges 0..1 ms      (transients stay relatively crisp)
            //   downMs ranges 0..10 ms   (release smear grows perceptibly)
            //
            // Translate ms → one-pole coefficient via the standard
            //   coef = 1 − exp(−1/samples_per_tc).
            // At Hysteresis=0 both ms are 0 and we explicitly set the
            // coefs to 1.0 (the limit case) — this guarantees bit-exact
            // transparency rather than depending on Exp(−∞)→0 behaviour.
            //
            // The recompute is gated on hystChanged || srChanged because
            // the conversion is sample-rate-dependent; we'd otherwise
            // silently get the wrong time constant if SR changed without
            // the user touching Hysteresis.
            if (hystChanged || srChanged)
            {
                if (Hysteresis <= 0 || sr <= 0)
                {
                    _hystUpCoef   = 1f;
                    _hystDownCoef = 1f;
                }
                else
                {
                    float hystN     = Hysteresis / 127f;
                    float upSamps   = (hystN *  1f) * sr * 0.001f;
                    float downSamps = (hystN * 10f) * sr * 0.001f;
                    _hystUpCoef   = (upSamps   > 0.001f) ? (1f - MathF.Exp(-1f / upSamps))   : 1f;
                    _hystDownCoef = (downSamps > 0.001f) ? (1f - MathF.Exp(-1f / downSamps)) : 1f;
                }
            }

            // Dust gain.  Knob is linear and very gentle — peak per-sample
            // contribution is dustGain · √|x| · 0.5 (PRNG returns ±0.5),
            // so at Dust=127 and full-scale input the per-sample dust
            // peaks at 0.4 in normalised scale, i.e. clearly audible
            // grain without overwhelming the signal.  At Dust=0 the gain
            // is exactly 0 so the unconditional mul-add in the hot loop
            // is mathematically transparent.
            _dustGain = (Dust / 127f) * 0.8f;
        }

        // ───────────────────────────────────────────────────────────────────
        // Work — the effect entry point.
        //
        // Signature is `bool Work(Sample[] output, Sample[] input,
        // int n, WorkModes mode)` per PedalComp §1.
        //
        // Return semantics (Buzz convention): true if the output buffer
        // contains non-silent samples; false if we want the host to
        // treat the output as silent (cheap downstream skip).
        // ───────────────────────────────────────────────────────────────────
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // No input connection or zero samples → nothing to do.
            // ChannelFilters state stays where it is; not flushed
            // here because next-buffer-with-input may want continuity.
            if (input == null || output == null || n <= 0)
                return false;

            // Sample rate from the host's MasterInfo (Core §13).
            // MasterInfo is valid in Work() and in setters.
            int sr = _host?.MasterInfo?.SamplesPerSec ?? 44100;

            // Pull cached coefficients, recompute only if dirty.
            UpdateCoefficients(sr);

            // Local copies for the hot loop — these become tighter
            // codegen than repeated field reads through `this`.
            int   shape    = Shape;
            float driveLin = _driveLin;
            float biasLin  = _biasLin;
            float outLin   = _outputLin;
            float toneLow  = _toneLowGain;
            float toneHigh = _toneHighGain;
            float mixDry   = _mixDry;
            float mixWet   = _mixWet;
            float hystUp   = _hystUpCoef;     // v1.1; 1.0 when Hysteresis=0
            float hystDown = _hystDownCoef;   // v1.1; 1.0 when Hysteresis=0
            float dustG    = _dustGain;       // v1.1; 0   when Dust=0
            float dustAtt  = _dustAttackCoef;  // v1.2; sample-rate-dependent
            float dustRel  = _dustReleaseCoef; // v1.2; sample-rate-dependent

            for (int i = 0; i < n; i++)
            {
                // ── 1. ±32768 → ±1.0 ───────────────────────────────────
                float dryL = input[i].L * SCALE;
                float dryR = input[i].R * SCALE;

                // ── 1b. Dust envelope follower (v1.2) ─────────────────
                // Asymmetric AR follower on the dry input magnitude.
                // The env state stays well-defined per channel and is
                // bumped every sample regardless of Dust setting — same
                // rationale as the unconditional PRNG advance below
                // (keeps the env continuous if Dust is dialled in
                // mid-render).  Tracked off dryL/dryR, NOT off drL/drR,
                // so Bias and Drive don't bleed noise into silence —
                // the v1.1 bug where non-default Bias kept dust audible
                // at zero input is fixed by this choice.
                float envL = _chL.DustEnv(MathF.Abs(dryL), dustAtt, dustRel);
                float envR = _chR.DustEnv(MathF.Abs(dryR), dustAtt, dustRel);

                // ── 2. Pre-tone tilt ───────────────────────────────────
                float toL = _chL.ApplyTone(dryL, toneLow, toneHigh);
                float toR = _chR.ApplyTone(dryR, toneLow, toneHigh);

                // ── 3. Drive + Bias ────────────────────────────────────
                float drL = toL * driveLin + biasLin;
                float drR = toR * driveLin + biasLin;

                // ── 3b. Dust (v1.1, scaling revised in v1.2) ──────────
                // Inject signal-correlated noise pre-shaper.  Amplitude
                // scales with √env (the smoothed dry-input level) so the
                // noise sits as a layer on top of the program material
                // rather than tracking the instantaneous waveform.  At
                // Dust=0 dustG is exactly 0 and the contribution
                // collapses to zero without a branch; the PRNG advance
                // is unconditional to keep the noise pattern continuous
                // when Dust is dialled in mid-render.
                drL += _chL.NextDust() * dustG * MathF.Sqrt(envL);
                drR += _chR.NextDust() * dustG * MathF.Sqrt(envR);

                // ── 4. Waveshape ───────────────────────────────────────
                float shL = Shapers.Apply(shape, drL);
                float shR = Shapers.Apply(shape, drR);

                // ── 4b. Hysteresis (v1.1) — asymmetric one-pole lag on
                // the shaper output.  At Hysteresis=0 both coefs are 1.0
                // and the call is `state = target` — transparent, no
                // branch needed in the caller (the branch on diff sign
                // inside Hysteresis is unavoidable and well-predicted).
                shL = _chL.Hysteresis(shL, hystUp, hystDown);
                shR = _chR.Hysteresis(shR, hystUp, hystDown);

                // ── 5. DC block (removes Bias DC + shape asymmetry) ───
                float dcL = _chL.DcBlock(shL);
                float dcR = _chR.DcBlock(shR);

                // ── 6. Output gain + dry/wet mix ──────────────────────
                float wetL = dcL * outLin;
                float wetR = dcR * outLin;

                float outL = dryL * mixDry + wetL * mixWet;
                float outR = dryR * mixDry + wetR * mixWet;

                // ── 7. ±1.0 → ±32768 ───────────────────────────────────
                output[i] = new Sample(outL * UNSCALE, outR * UNSCALE);
            }

            return true;
        }
    }
}
