---
name: btc-support-resistance
description: Computes BTCUSDT support and resistance zones on Binance 30-minute candles for a given cutoff time, with deterministic scripts. Use this skill whenever a task mentions Bitcoin or BTC support, resistance, key levels, price zones, where price may bounce or stall, or broken levels turning into support/resistance, even if the user does not say "support" or names another BTC pair.
---

# BTCUSDT support and resistance zones

This skill answers "where is the nearest reliable support and resistance for BTCUSDT right
now (or at time T)?" with one zone per side, computed by `scripts/levels.py` from closed
Binance 30m candles. The script is deterministic: the same cutoff always gives the same zones,
so every number you report can be checked and backtested.

## Always use the script for numbers

Do not compute levels, averages, distances or percentages yourself, and do not quote prices
from memory. Language models are unreliable at arithmetic over hundreds of candles, and a zone
nobody can reproduce cannot be evaluated. Run the script and report what it returns.

```
levels.py levels --end <CUTOFF> [--side support|resistance|both] [--top 5]
levels.py analyze --end <CUTOFF> [--side ...]     # every raw level and the market context
levels.py fetch --end <CUTOFF>                    # window summary only
```

`<CUTOFF>` is an ISO-8601 UTC time such as `2026-10-19T00:00:00Z`. It must not be in the
future or inside the still-open candle; for "now", use the most recent full half hour or the
cutoff the task gives you. The window is the 336 closed candles (7 days) before the cutoff.
When the run needs the published parameters it reads `params.json` next to this file, so do not
pass `--params` unless the task tells you to.

## What the output means

For each side, `recommended` is the zone to report, or `null`:

- `zoneLow` / `zoneHigh`: the zone, at most 0.5% wide. A support lies at most 3% below the last
  close and a resistance at most 3% above it.
- `method`: what produced the level. `swing_low`/`swing_high` are repeated turning points,
  `volume_node` is a price with heavy traded volume, `pivot_*` are classic daily pivots,
  `flip_resistance` is a broken resistance now acting as support (`flip_support` is the reverse),
  and `feature:<name>` comes from a plugin the team learned (see `references/methodology.md`).
- `source`: `fresh` means today's analysis found it; `carried` means today had no reliable level,
  so yesterday's zone is kept because no close has broken it since (`issuedAt` says when).
- `abstainReason` (when `recommended` is null): `trend_gate` (the 7-day trend runs against this
  side), `below_min_score` (no level is strong enough) or `no_valid_candidate`. An abstention is
  a valid answer; report it instead of inventing a zone.
- `candidates`: the other ranked fresh zones, useful to explain the choice.

## Report format

Return exactly this JSON, with values copied from the script output:

```json
{
  "cutoff": "2026-10-19T00:00:00Z",
  "lastClose": 0.0,
  "support":    {"zoneLow": 0.0, "zoneHigh": 0.0, "method": "", "source": "", "abstainReason": null},
  "resistance": {"zoneLow": 0.0, "zoneHigh": 0.0, "method": "", "source": "", "abstainReason": null},
  "notes": "one or two sentences: why these zones, and any flip or carry-over worth knowing"
}
```

Use `null` for the zone fields of a side that abstained.

## How good is it

A zone counts as a hit when price touches it within 24h and no 30m candle closes through it.
`references/methodology.md` explains the method, how it is evaluated, and the current published
version's verdict. Do not describe the zones as reliable beyond what that verdict supports.
