namespace PedalShaper
{
    // Per-channel filter state for tone tilt + DC blocker.
    // One instance per audio channel (L, R).
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

        // Wipe state — useful on machine construction or for a future
        // "panic" command.  v1.0 calls it only from the ctor.
        public void Reset()
        {
            _lpState   = 0f;
            _dcLastIn  = 0f;
            _dcLastOut = 0f;
        }
    }
}
