using System;

namespace PedalShaper
{
    // Pure waveshaping functions.  All take a pre-driven input sample
    // (already scaled by drive_lin) and return the shaped output in
    // roughly the ±1.0 range.  No state, no allocations.
    //
    // None of these are "perfectly" bounded to ±1.0 for very large
    // inputs — that's the DC blocker / output gain's problem.  In
    // practice with the Bias offset and asymmetric shapes the absolute
    // output can drift slightly above 1.0; the post-shaper chain
    // (DC blocker + output gain + ±32768 scale) absorbs this without
    // clipping unless the user dials in extreme settings.
    //
    // No `Buzz.MachineInterface` / `BuzzGUI.Interfaces` usings here —
    // per Build §6.3, helper files that don't touch SDK types stay clean.
    internal static class Shapers
    {
        // Shape enum values — must match PedalShaperMachine.Shape encoding
        public const int SHAPE_SOFT  = 0;
        public const int SHAPE_HARD  = 1;
        public const int SHAPE_TUBE  = 2;
        public const int SHAPE_FOLD  = 3;
        public const int SHAPE_CRUSH = 4;

        // Crush quantization step.  Roughly 4-bit resolution over ±1.0
        // (8 levels per side).  Smaller = harsher; the value was chosen
        // so the effect is musically obvious without being a one-trick
        // pony — finer detail still survives at moderate drive levels.
        const float CRUSH_LEVELS = 8f;
        const float CRUSH_INV    = 1f / CRUSH_LEVELS;

        // Dispatcher.  Branches on the shape, not on input — branch
        // predictor handles this trivially because `shape` is buffer-
        // constant.  Inlined by the JIT in practice; the explicit
        // switch survives as a single jump-table dispatch.
        public static float Apply(int shape, float x)
        {
            switch (shape)
            {
                case SHAPE_HARD:  return Hard(x);
                case SHAPE_TUBE:  return Tube(x);
                case SHAPE_FOLD:  return Fold(x);
                case SHAPE_CRUSH: return Crush(x);
                default:          return Soft(x);
            }
        }

        // Soft — tanh.  Smooth knee, gentle even at high drive.  MathF.Tanh
        // is single-instruction on modern x64 hardware (one libm call,
        // ~10ns).  Could be replaced with a polynomial approximation
        // (e.g. x * (27 + x²) / (27 + 9·x²) — the Padé(3,4) approximant)
        // if profiling ever shows it dominates; v1.0 keeps the exact form
        // for fidelity at extreme drive levels.
        public static float Soft(float x)
        {
            return MathF.Tanh(x);
        }

        // Hard — pure clamp.  Brick-wall clipping.  All the high-order
        // harmonics, lots of buzz character.  No smoothing at the knee
        // because the harshness IS the sound — anyone reaching for "Hard"
        // wants the edge.  For a smoother middle ground there's Soft.
        public static float Hard(float x)
        {
            if (x >  1f) return  1f;
            if (x < -1f) return -1f;
            return x;
        }

        // Tube — asymmetric tanh, modelling the asymmetric saturation of
        // a vacuum-tube amp stage.  Positive half gets compressed harder
        // (1.5× pre-gain into tanh) than the negative half (0.6×), which
        // gives the characteristic even-order harmonic content that ear
        // training calls "tube warmth".  The DC blocker downstream
        // removes the DC offset this asymmetry produces; we don't
        // pre-zero-mean the output here.
        public static float Tube(float x)
        {
            return x >= 0f ? MathF.Tanh(1.5f * x) : MathF.Tanh(0.6f * x);
        }

        // Fold — sine-based wavefolder.  At small inputs sin(πx/2) ≈ πx/2
        // (gentle linear gain).  As input grows past 1, sin oscillates,
        // creating the characteristic folded overtones that climb and
        // descend.  Bounded to ±1 by construction.  The π/2 factor sets
        // the first fold-point exactly at |x|=1 — at lower drive levels
        // it's almost transparent; above ±1 input the folding kicks in
        // immediately.  No clamping needed.
        public static float Fold(float x)
        {
            return MathF.Sin(x * (MathF.PI * 0.5f));
        }

        // Crush — bit-quantization.  Drive amplifies into the quantizer,
        // levels stay fixed at ±CRUSH_LEVELS per side (≈4 bits).
        // At low drive the signal lives in 1–2 quantization steps near
        // zero (most input quantized to 0) — silence punctuated by
        // coarse "ticks".  At high drive the signal saturates against
        // the outer levels and clips.  Sweet spot is around 0..+12 dB
        // drive depending on input level.
        //
        // Output is also clamped to ±1 because rounding × inverse can
        // exceed ±1 by a step at the limits.
        public static float Crush(float x)
        {
            float q = MathF.Round(x * CRUSH_LEVELS) * CRUSH_INV;
            if (q >  1f) return  1f;
            if (q < -1f) return -1f;
            return q;
        }
    }
}
