namespace PedalShaper
{
    // Per-channel DSP state for the wet signal path:
    //   - Pre-shaper tone tilt (one-pole low-pass split, v1.0)
    //   - Post-shaper hysteresis (asymmetric one-pole, v1.1)
    //   - Post-hysteresis DC blocker (one-pole high-pass, v1.0)
    //   - Dust noise source (xorshift32 PRNG, v1.1)
    //
    // One instance per audio channel (L, R).  Each instance is seeded
    // differently so the dust streams on L and R are decorrelated —
    // gives free stereo width on mono sources without delay-based tricks.
    //
    // No SDK usings — pure DSP, per Build §6.3.
    internal sealed class ChannelFilters
    {
        // ── Tone tilt (one-pole low-pass split) ───────────────────────────
        // The tilt is built from a single one-pole low-pass and the
        // complementary high-pass (input − low).  The tone knob mixes
        // the two with gains that sum to 1.0 at Tone=64 (flat), and
        // re-weights toward one band at the extremes.
        //
        // Reference cutoff is ~1 kHz — chosen because it sits near the
        // ear's mid-band sensitivity and gives a perceptually balanced
        // tilt rather than an "all bass / all treble" feel.
        //
        // The coefficient is computed once per buffer in
        // PedalShaperMachine.UpdateCoefficients() and pushed in via
        // `SetToneCoef`.
        float _lpState;
        float _toneLpCoef = 0.1f;   // updated per-buffer

        // ── DC blocker (one-pole high-pass at ~30 Hz) ─────────────────────
        // y[n] = x[n] − x[n−1] + R · y[n−1]
        // R near 1.0 gives a steep low-frequency rolloff.  This removes
        // the DC offset that asymmetric shaping + Bias introduces, plus
        // any sub-audible rumble below the band of interest.
        float _dcLastIn;
        float _dcLastOut;
        const float DC_R = 0.997f;   // ≈ 30 Hz at 44.1 kHz

        // ── Hysteresis state (v1.1) ───────────────────────────────────────
        // Asymmetric one-pole lag applied to the shaper output.  The
        // coefficients differ for rising vs falling output, which is what
        // makes this hysteresis-like rather than just a low-pass:
        //
        //   diff = target − state
        //   coef = (diff ≥ 0) ? upCoef : downCoef
        //   state += diff · coef
        //
        // At Hysteresis=0 the machine passes upCoef = downCoef = 1.0 so
        // each sample snaps state straight to target — mathematically
        // transparent, no fast-path branch needed in the hot loop.
        //
        // At higher Hysteresis settings, upCoef stays moderately fast
        // (so transients still come through with shape) but downCoef
        // grows much slower, producing the "release smear" / tape-lag
        // character that the feature is named for.  Coefficient mapping
        // is done buffer-rate in the machine — see UpdateCoefficients().
        float _hystState;

        // ── Dust envelope follower (v1.2) ─────────────────────────────────
        // Asymmetric one-pole AR follower applied to |dryInput|.  Fast
        // attack so transients pull the dust level up quickly; slow
        // release so the noise tail lingers after notes end.  That
        // combination is what makes the v1.2 Dust feel like a layer
        // riding on top of the program material — vinyl-style — rather
        // than the v1.1 behaviour of per-sample |x| tracking that gave
        // the dust an audible zero-crossing modulation tied to the
        // signal waveform.
        //
        // Time constants are buffer-rate (computed from sample rate in
        // PedalShaperMachine.UpdateCoefficients()) and pushed in per
        // sample as the upCoef/downCoef arguments below.
        //
        // The follower is driven from `dryInput` (pre-drive, pre-bias)
        // upstream so that Drive/Bias settings don't affect when dust
        // appears — silence with non-default Bias no longer leaks noise.
        float _dustEnv;

        // ── Dust PRNG (v1.1) ──────────────────────────────────────────────
        // xorshift32 — fast, no allocations, deterministic.  Each
        // ChannelFilters instance is constructed with a different seed
        // (see ctor) so the L and R dust streams are statistically
        // independent and produce natural stereo width on mono input.
        //
        // The seed is also kept around to support Reset() — a panic-style
        // reset rewinds the PRNG to its initial state, which is useful
        // for reproducible offline render comparisons even though the
        // production audio path never calls Reset mid-render.
        readonly uint _seed;
        uint _rng;

        // ── Constructor ───────────────────────────────────────────────────
        // The seed should be non-zero (xorshift32 with seed 0 emits zeros
        // forever).  Caller is responsible for passing distinct seeds for
        // L and R.
        public ChannelFilters(uint seed)
        {
            _seed = (seed == 0u) ? 0xC0DEC0DEu : seed;
            Reset();
        }

        // Pre-shaper tone tilt.
        //
        // toneLowGain  is the multiplier on the low-band component.
        // toneHighGain is the multiplier on the high-band component.
        // At Tone=64 the two are both 1.0 and the filter is a pass-
        // through (low + high == input by construction).
        // At Tone extremes one gets boosted and the other cut.
        // Both coefficients are pre-computed buffer-rate in the machine
        // (UpdateCoefficients) so the per-sample path stays branchless.
        public float ApplyTone(float x, float toneLowGain, float toneHighGain)
        {
            // One-pole LP: y[n] = y[n-1] + coef · (x − y[n-1])
            _lpState += _toneLpCoef * (x - _lpState);
            float low  = _lpState;
            float high = x - low;
            return low * toneLowGain + high * toneHighGain;
        }

        // Asymmetric one-pole lag (hysteresis).  upCoef and downCoef
        // are pre-computed buffer-rate by the machine and are 1.0 when
        // the Hysteresis parameter is 0, so this method is mathematically
        // transparent in the off case — no branch needed in the caller.
        //
        // The branch on diff sign is per-sample, but it's the only
        // direction-dependent thing the function does, and modern branch
        // predictors handle audio-signal sign changes around zero
        // crossings cleanly (it's the most predictable pattern they see).
        public float Hysteresis(float target, float upCoef, float downCoef)
        {
            float diff = target - _hystState;
            float coef = (diff >= 0f) ? upCoef : downCoef;
            _hystState += diff * coef;
            return _hystState;
        }

        // xorshift32 step — returns a uniform float in [−0.5, 0.5).
        //
        // Standard 13/17/5 shifts; period 2^32 − 1, fine for audio dust
        // where we draw at most 96 000 samples/s and a 2³² period gives
        // ~12 hours of unique noise before any repetition.  The output
        // is mapped via the top 24 bits of the state divided by 2²⁴, so
        // the resolution is one part in 16 777 216 — well below any
        // perceptual threshold.
        //
        // No locking, no allocations.  Safe on the audio thread (Core §16).
        public float NextDust()
        {
            uint r = _rng;
            r ^= r << 13;
            r ^= r >> 17;
            r ^= r << 5;
            _rng = r;
            return ((r >> 8) & 0xFFFFFFu) * (1f / 16777216f) - 0.5f;
        }

        // Dust envelope follower (v1.2).  Same asymmetric-one-pole shape
        // as Hysteresis above, but applied to a positive level signal
        // (the caller passes MathF.Abs(dryInput) or similar) and tuned
        // with different time constants — fast attackCoef, slow
        // releaseCoef — so the output rides on top of the program
        // material rather than tracking its instantaneous waveform.
        //
        // Coefficients are computed buffer-rate in the machine, depend
        // only on sample rate, and are independent of any user-facing
        // knob.  This keeps the per-sample cost at one comparison and
        // one mul-add per channel.
        public float DustEnv(float level, float attackCoef, float releaseCoef)
        {
            float diff = level - _dustEnv;
            float coef = (diff >= 0f) ? attackCoef : releaseCoef;
            _dustEnv += diff * coef;
            return _dustEnv;
        }

        // Post-shaper DC blocker.  Removes the DC component that
        // asymmetric shaping or Bias-driven offset leaves in the signal.
        public float DcBlock(float x)
        {
            float y = x - _dcLastIn + DC_R * _dcLastOut;
            _dcLastIn  = x;
            _dcLastOut = y;
            return y;
        }

        // Set per-buffer tilt coefficient.  Called from the machine's
        // UpdateCoefficients() when the sample rate changes.  Computed
        // from the tilt reference frequency:
        //
        //   coef = 1 − exp(−2π · fc / sr)
        //
        // standard one-pole low-pass discretisation.
        public void SetToneCoef(float coef)
        {
            _toneLpCoef = coef;
        }

        // Wipe all state — useful on machine construction or for a future
        // "panic" command.  v1.2 calls it only from the ctor.
        // The PRNG resets to its construction seed, not to 0, so a Reset
        // doesn't break xorshift32's non-zero invariant.
        public void Reset()
        {
            _lpState   = 0f;
            _dcLastIn  = 0f;
            _dcLastOut = 0f;
            _hystState = 0f;
            _dustEnv   = 0f;
            _rng       = _seed;
        }
    }
}
