# Methodology

## Levels

Every level comes from the 336 closed 30m candles before the cutoff:

| Method | What it is |
|---|---|
| `swing_low` / `swing_high` | the extreme of the 2k+1 candles around it; swings within `cluster_tol_pct` merge into one level |
| `flip_resistance` | a swing-high level now below the close: a broken resistance that may act as support |
| `flip_support` | a swing-low level now above the close: a broken support that may act as resistance |
| `volume_node` | a local peak of the volume profile (bins of `vp_bin_pct`) above 1.5x the mean |
| `pivot_S1` ... `pivot_R2` | classic pivots from the last 24h |
| `feature:<name>` | a learned plugin, see "Learned changes" |

Each level is scored per side with weights from `params.json`:

- touches (candles whose low or high came within `touch_tol_pct`);
- volume-profile weight at the price;
- reaction volume (volume of the swing candles relative to the window median);
- recency;
- confluence with other methods;
- a flip bonus that grows with the breakout candle's relative volume;
- distance;
- the plugin weight.

The best level becomes a zone that is at most 0.5% wide and lies within 3% of the close.

**Abstention and carry-over.** A side abstains when:

- the 7-day trend runs against it (`trend_gate_pct`), or
- its best score is below `min_score`.

It then keeps the previous day's zone if no close has broken it since and the zone still fits
the constraints (`carry_days`).

## Evaluation

`backtest/backtest.py` replays every daily cutoff of 2026 (from 2026-01-08 on). The first 70% of
cutoffs (train) may be used to choose parameters or plugin weights. The last 30% (test) is used
only for the verdict.

| | Touched | Hit |
|---|---|---|
| support | a low reaches `zoneHigh` within 24h | touched and no 30m close below `zoneLow` |
| resistance | a high reaches `zoneLow` within 24h | touched and no 30m close above `zoneHigh` |

- precision = hits / touched
- coverage = touched / windows; an abstention counts as not touched

A side is **ready** when, on test:

1. coverage is at least 70%;
2. precision beats a zone placed at random among all valid zones (one-sided binomial test,
   p < 0.05);
3. precision beats the previously published version (paired bootstrap, 95% lower bound > 0).

90% precision is the aspirational target. It is reported, but it does not gate.

## Learned changes

Each entry below was added automatically when the team's learning workflow promoted a change
that passed the readiness checks.

### v1 (2026-10-20): baseline

- **Source:** per-side parameters chosen by random search on the train split.
- **Support (test):** 54.8% precision, 84.9% coverage. The random base rate is 48.5%, so this is
  not significant (p = 0.17).
- **Resistance (test):** 40.3% precision, 72.1% coverage. The random base rate is 43.4%.
- **Verdict:** not ready. The zones are no better than chance yet.
