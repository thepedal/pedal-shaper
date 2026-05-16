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
    //     input (±32768)        ── multiply by SCALE  ─→  in (±1.0)
    //                            ↓
    //         pre-tone tilt (one-pole split, mix low/high bands)
    //                            ↓
    //         drive_lin gain  +  Bias DC offset
    //                            ↓
    //         waveshaper (Soft / Hard / Tube / Fold / Crush)
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

        // ── Host & per-channel state ──────────────────────────────────────

        readonly IBuzzMachineHost _host;
        readonly ChannelFilters _chL = new ChannelFilters();
        readonly ChannelFilters _chR = new ChannelFilters();

        // ── Cached / dirty-checked coefficients (PedalComp §6) ────────────
        int   _cachedSr     = 0;
        int   _cachedDrive  = -1;
        int   _cachedOutput = -1;
        int   _cachedTone   = -1;
        int   _cachedBias   = -1;
        int   _cachedMix    = -1;

        float _driveLin   = 1f;       // amplitude multiplier from Drive
        float _outputLin  = 1f;       // amplitude multiplier from Output
        float _biasLin    = 0f;       // additive DC offset in ±1.0 scale
        float _toneLowGain  = 1f;     // tilt low-band weight
        float _toneHighGain = 1f;     // tilt high-band weight
        float _mixDry     = 0f;       // 0..1 dry coefficient
        float _mixWet     = 1f;       // 0..1 wet coefficient
        float _toneLpCoef = 0.1f;     // pushed into both ChannelFilters

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

        // ───────────────────────────────────────────────────────────────────
        // Coefficient cache (PedalComp §6)
        //
        // Recompute only when any input changes; the per-buffer no-op
        // path is just a handful of integer comparisons.
        // ───────────────────────────────────────────────────────────────────
        void UpdateCoefficients(int sr)
        {
            if (sr == _cachedSr
                && Drive  == _cachedDrive
                && Output == _cachedOutput
                && Tone   == _cachedTone
                && Bias   == _cachedBias
                && Mix    == _cachedMix)
                return;

            bool srChanged = (sr != _cachedSr);

            _cachedSr     = sr;
            _cachedDrive  = Drive;
            _cachedOutput = Output;
            _cachedTone   = Tone;
            _cachedBias   = Bias;
            _cachedMix    = Mix;

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
            if (srChanged && sr > 0)
            {
                _toneLpCoef = 1f - MathF.Exp(-2f * MathF.PI * TONE_REF_HZ / sr);
                _chL.SetToneCoef(_toneLpCoef);
                _chR.SetToneCoef(_toneLpCoef);
            }
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

            for (int i = 0; i < n; i++)
            {
                // ── 1. ±32768 → ±1.0 ───────────────────────────────────
                float dryL = input[i].L * SCALE;
                float dryR = input[i].R * SCALE;

                // ── 2. Pre-tone tilt ───────────────────────────────────
                float toL = _chL.ApplyTone(dryL, toneLow, toneHigh);
                float toR = _chR.ApplyTone(dryR, toneLow, toneHigh);

                // ── 3. Drive + Bias ────────────────────────────────────
                float drL = toL * driveLin + biasLin;
                float drR = toR * driveLin + biasLin;

                // ── 4. Waveshape ───────────────────────────────────────
                float shL = Shapers.Apply(shape, drL);
                float shR = Shapers.Apply(shape, drR);

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
