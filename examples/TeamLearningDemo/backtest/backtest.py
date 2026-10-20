#!/usr/bin/env python3
"""Deterministic backtest of the btc-support-resistance skill (stdlib only).

Kept outside the skill directory on purpose: MAF exposes every script in a skill to the agent.

Data is 2026 only: candles from 2026-01-01T00:00Z to the last UTC midnight. Each daily cutoff T
from 2026-01-08 (the first with a full 7-day window) recommends one zone per side; the next 48
candles (24h) decide the outcome:

  support     touched = some low  <= zoneHigh   hit = touched and no close < zoneLow
  resistance  touched = some high >= zoneLow    hit = touched and no close > zoneHigh
  precision = hits / touched      coverage = touched / windows
  (an abstention with nothing carried over counts as a window that was not touched)

Cutoffs are split chronologically: the first 70% (train) may be used to choose anything, the
last 30% (test) is only used for the verdict. A side is READY when, on test,
  1. coverage >= 70%,
  2. precision beats the random-zone base rate (one-sided binomial test, p < 0.05), and
  3. with a baseline, precision beats it (paired bootstrap, 95% lower bound > 0).
When m plugins are judged in one run, each uses alpha = 0.05 / m (Bonferroni) for 2 and 3.
Precision >= 90% is reported as the aspirational target; it does not gate.

Params files have the skill's params.json shape: {"support": {...}, "resistance": {...}}.

  python3 backtest.py --prefetch                       # download/cache 2026 candles (network)
  python3 backtest.py [--params P] [--baseline B] [--json verdict.json] [--report report.json]
  python3 backtest.py --search 1500                    # random search on train only
  python3 backtest.py --sandbox --check-feature NAME   # validate + smoke-run a plugin
  python3 backtest.py --sandbox --experiment NAME --baseline B   # train-only measurement for engineers
  python3 backtest.py --sandbox --evaluate-feature NAME --baseline B --json result.json

--sandbox makes the process offline (cached candles only) and applies CPU/memory limits; run it
with `python3 -I` and BTC_LEVELS_FEATURE_PATH pointing at the candidate's directory.
"""

import argparse
import json
import math
import os
import random
import sys
import time
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "skills", "btc-support-resistance", "scripts"))
import levels as L  # noqa: E402

DAY_MS = 24 * 3600 * 1000
DATA_START = int(datetime(2026, 1, 1, tzinfo=timezone.utc).timestamp() * 1000)
TRAIN_FRACTION = 0.7
MIN_COVERAGE = 0.70
TARGET_PRECISION = 0.90
ALPHA = 0.05
BOOTSTRAP = 2000
SEED = 7
FEATURE_WEIGHTS = (0.5, 1.0, 2.0, 4.0)
STRUCTURAL = ("swing_k", "cluster_tol_pct", "touch_tol_pct", "vp_bin_pct")


def sandbox():
    """Offline, CPU- and memory-limited process for evaluating untrusted plugins."""
    os.environ["BTC_LEVELS_OFFLINE"] = "1"
    try:
        import resource
        resource.setrlimit(resource.RLIMIT_CPU, (600, 600))
        resource.setrlimit(resource.RLIMIT_FSIZE, (50 * 2 ** 20, 50 * 2 ** 20))
        if sys.platform.startswith("linux"):  # macOS does not enforce RLIMIT_AS
            resource.setrlimit(resource.RLIMIT_AS, (2 * 2 ** 30, 2 * 2 ** 30))
    except (ImportError, ValueError, OSError):
        pass


def load_history():
    now = int(time.time() * 1000)
    end = now - now % DAY_MS  # last midnight UTC, so every candle is closed
    if os.environ.get("BTC_LEVELS_OFFLINE"):
        # Offline runs use the newest cached 2026 range instead of guessing today's file name.
        prefix = "%s_%s_%d_" % (L.SYMBOL, L.INTERVAL, DATA_START)
        cached = sorted(f for f in os.listdir(L._cache_dir()) if f.startswith(prefix) and f.endswith(".json"))
        if not cached:
            raise RuntimeError("no cached 2026 candles; run backtest.py --prefetch first")
        end = int(cached[-1][len(prefix):-len(".json")])
    return L.load_candles(DATA_START, end)


def cutoffs(candles):
    """Indexes of daily-midnight cutoffs with a full window behind and a full horizon ahead."""
    return [i for i, c in enumerate(candles)
            if c["t"] % DAY_MS == 0 and i >= L.BARS and i + L.DAY_BARS <= len(candles)]


class Contexts:
    """context() per (cutoff, structural params, plugins); scoring weights do not invalidate it."""

    def __init__(self, candles):
        self.candles = candles
        self.cache = {}

    def at(self, params):
        key = tuple(params[k] for k in STRUCTURAL) + tuple(sorted(params["features"]))

        def get(i):
            k = (i, key)
            if k not in self.cache:
                self.cache[k] = L.context(self.candles[i - L.BARS:i], params)
            return self.cache[k]
        return get


def outcome(zone, fut, side):
    if side == "support":
        touched = any(c["l"] <= zone["zoneHigh"] for c in fut)
        broken = any(c["c"] < zone["zoneLow"] for c in fut)
    else:
        touched = any(c["h"] >= zone["zoneLow"] for c in fut)
        broken = any(c["c"] > zone["zoneHigh"] for c in fut)
    return touched, touched and not broken


def evaluate(candles, idxs, params, side, ctxs):
    ctx_at = ctxs.at(params)
    rows = []
    for i in idxs:
        zone, reason = L.recommend(candles, i, side, params, ctx_at)
        ctx = ctx_at(i)
        row = {"t": L.iso(candles[i]["t"]), "zone": zone, "abstainReason": reason,
               "return7dPct": round(ctx["return_7d_pct"], 3), "atrPct": round(ctx["atr_pct"], 3),
               "touched": False, "hit": False}
        if zone:
            row["touched"], row["hit"] = outcome(zone, candles[i:i + L.DAY_BARS], side)
        rows.append(row)
    return rows


def summarize(rows):
    n = len(rows)
    touched = sum(r["touched"] for r in rows)
    hits = sum(r["hit"] for r in rows)
    return {"windows": n, "touched": touched, "hits": hits,
            "abstained": sum(1 for r in rows if r["zone"] is None),
            "carried": sum(1 for r in rows if r["zone"] and r["zone"]["source"] == "carried"),
            "coverage": touched / n if n else 0.0,
            "precision": hits / touched if touched else 0.0}


def base_rate(candles, idxs, side):
    """Precision of a zone placed uniformly at random among all valid zones (0.05% grid)."""
    hits = touched = 0
    for i in idxs:
        last = candles[i - 1]["c"]
        fut = candles[i:i + L.DAY_BARS]
        for step in range(0, 51):
            if side == "support":
                zh = last * (1 - step * 0.0005)
                zl = zh * (1 - L.MAX_WIDTH_PCT / 100)
            else:
                zl = last * (1 + step * 0.0005)
                zh = zl / (1 - L.MAX_WIDTH_PCT / 100)
            if not L.within_constraints(zl, zh, side, last):
                continue
            t, h = outcome({"zoneLow": zl, "zoneHigh": zh}, fut, side)
            touched += t
            hits += h
    return hits / touched if touched else 0.0


def binom_sf(k, n, p):
    """P(X >= k) for X ~ Binomial(n, p)."""
    return sum(math.comb(n, j) * p ** j * (1 - p) ** (n - j) for j in range(k, n + 1))


def bootstrap_lower(cand_rows, base_rows, alpha=ALPHA):
    """One-sided (1 - alpha) lower bound of precision(candidate) - precision(baseline), paired by window."""
    rng = random.Random(SEED)
    n = len(cand_rows)
    resamples = max(BOOTSTRAP, int(math.ceil(20 / alpha)))  # enough draws for the alpha quantile
    diffs = []
    for _ in range(resamples):
        pick = [rng.randrange(n) for _ in range(n)]
        a = summarize([cand_rows[j] for j in pick])["precision"]
        b = summarize([base_rows[j] for j in pick])["precision"]
        diffs.append(a - b)
    diffs.sort()
    return diffs[int(alpha * resamples)]


def side_verdict(candles, test, params, baseline, side, ctxs, alpha=ALPHA):
    rows = evaluate(candles, test, params, side, ctxs)
    s = summarize(rows)
    base = base_rate(candles, test, side)
    p_value = binom_sf(s["hits"], s["touched"], base) if s["touched"] else 1.0
    checks = {"coverage": s["coverage"] >= MIN_COVERAGE, "beatsRandom": p_value < alpha}
    out = dict(s, baseRate=base, pValueVsRandom=p_value, alpha=alpha)
    if baseline is not None:
        brows = evaluate(candles, test, baseline, side, ctxs)
        out["baselinePrecision"] = summarize(brows)["precision"]
        out["improvementLowerBound"] = bootstrap_lower(rows, brows, alpha)
        checks["beatsBaseline"] = out["improvementLowerBound"] > 0
    out["checks"] = checks
    out["ready"] = all(checks.values())
    out["meetsAspirationalTarget"] = s["precision"] >= TARGET_PRECISION
    return out


def verdict(candles, test, by_side, baseline_by_side, ctxs):
    sides = {side: side_verdict(candles, test, by_side[side],
                                baseline_by_side[side] if baseline_by_side else None, side, ctxs)
             for side in L.SIDES}
    return {"sides": sides, "ready": all(v["ready"] for v in sides.values())}


def bucket(value, edges, labels):
    for edge, label in zip(edges, labels):
        if value < edge:
            return label
    return labels[-1]


def regime_report(candles, idxs, by_side, ctxs):
    """Precision and coverage by regime, so the team can see where the skill fails."""
    report = {}
    for side in L.SIDES:
        rows = evaluate(candles, idxs, by_side[side], side, ctxs)
        groups = {}
        for r in rows:
            z = r["zone"]
            keys = {
                "trend7d": bucket(r["return7dPct"], (-3, 3), ("down<-3%", "flat", "up>3%")),
                "atrPct": bucket(r["atrPct"], (0.25, 0.45), ("low<0.25", "mid", "high>0.45")),
                "method": z["method"] if z else "none",
                "source": z["source"] if z else "abstained:" + str(r["abstainReason"]),
                "distancePct": bucket(z["distancePct"], (0.5, 1.5), ("<0.5", "0.5-1.5", ">1.5"))
                if z else "none",
            }
            for dim, label in keys.items():
                groups.setdefault(dim, {}).setdefault(label, []).append(r)
        report[side] = {"overall": summarize(rows),
                        "byRegime": {dim: {label: summarize(rs) for label, rs in sorted(by.items())}
                                     for dim, by in groups.items()}}
    return report


def best_on_train(candles, train, side, candidates, ctxs):
    """Highest train precision among candidates that meet the coverage floor, else None."""
    best = None
    for params in candidates:
        s = summarize(evaluate(candles, train, params, side, ctxs))
        if s["coverage"] >= MIN_COVERAGE and (best is None or (s["precision"], s["coverage"]) >
                                               (best[0]["precision"], best[0]["coverage"])):
            best = (s, params)
    return best


def evaluate_feature(candles, train, test, name, baseline, ctxs, alpha=ALPHA):
    """Try one plugin at a few weights per side (train), then judge the best on test.

    alpha is the per-plugin significance level. A run that judges m plugins against the same test
    split passes ALPHA / m (Bonferroni), so that one of them passing by luck stays unlikely.
    """
    out = {"feature": name, "sides": {}}
    for side in L.SIDES:
        base = baseline[side]
        tries = [dict(base, features=dict(base["features"], **{name: w})) for w in FEATURE_WEIGHTS]
        best = best_on_train(candles, train, side, tries, ctxs)
        if best is None:
            out["sides"][side] = {"promote": False, "reason": "no weight keeps train coverage >= 70%"}
            continue
        s, params = best
        v = side_verdict(candles, test, params, base, side, ctxs, alpha)
        out["sides"][side] = {"promote": v["ready"], "weight": params["features"][name],
                              "train": s, "baselineTrain": summarize(evaluate(candles, train, base, side, ctxs)),
                              "test": v,
                              "reason": "ready" if v["ready"] else "failed: " + ", ".join(
                                  k for k, ok in v["checks"].items() if not ok)}
    out["promote"] = any(s["promote"] for s in out["sides"].values())
    return out


def regime_deltas(candles, idxs, params, baseline, side, ctxs):
    """Precision of plugin vs baseline per regime: where the idea helps and where it hurts."""
    rows = evaluate(candles, idxs, params, side, ctxs)
    brows = evaluate(candles, idxs, baseline, side, ctxs)
    out = {}
    for dim, value, edges, labels in (
            ("trend7d", "return7dPct", (-3, 3), ("down<-3%", "flat", "up>3%")),
            ("atrPct", "atrPct", (0.25, 0.45), ("low<0.25", "mid", "high>0.45"))):
        groups = {}
        for r, b in zip(rows, brows):
            groups.setdefault(bucket(r[value], edges, labels), ([], []))
            groups[bucket(r[value], edges, labels)][0].append(r)
            groups[bucket(r[value], edges, labels)][1].append(b)
        out[dim] = {}
        for label, (rs, bs) in sorted(groups.items()):
            a, b = summarize(rs), summarize(bs)
            out[dim][label] = {"windows": a["windows"], "precision": round(a["precision"], 4),
                               "baselinePrecision": round(b["precision"], 4),
                               "coverage": round(a["coverage"], 4), "baselineCoverage": round(b["coverage"], 4)}
    return out


def experiment(candles, train, name, baseline, ctxs):
    """An engineer's experiment: validate the plugin and measure it on TRAIN ONLY.

    Engineers may run as many experiments as they like, so they must never see the test split:
    it is used once, when they submit (evaluate_feature). Here they get, per side, the best train
    weight, the change against the published version, and where (by regime) the plugin helps.
    """
    check = check_feature(candles, train, name)
    if not check["ok"]:
        return check
    out = {"ok": True, "name": name, "samples": check["samples"], "sides": {}}
    for side in L.SIDES:
        base = baseline[side]
        bsum = summarize(evaluate(candles, train, base, side, ctxs))
        tries = []
        for w in FEATURE_WEIGHTS:
            params = dict(base, features=dict(base["features"], **{name: w}))
            tries.append((summarize(evaluate(candles, train, params, side, ctxs)), params, w))
        eligible = [t for t in tries if t[0]["coverage"] >= MIN_COVERAGE] or tries
        s, params, w = max(eligible, key=lambda t: (t[0]["precision"], t[0]["coverage"]))
        out["sides"][side] = {
            "bestWeight": w,
            "train": {k: round(v, 4) if isinstance(v, float) else v for k, v in s.items()},
            "baselineTrain": {k: round(v, 4) if isinstance(v, float) else v for k, v in bsum.items()},
            "deltaPrecisionPts": round(100 * (s["precision"] - bsum["precision"]), 2),
            "deltaCoveragePts": round(100 * (s["coverage"] - bsum["coverage"]), 2),
            "meetsCoverage": s["coverage"] >= MIN_COVERAGE,
            "changedWindows": sum(1 for a, b in zip(evaluate(candles, train, params, side, ctxs),
                                                   evaluate(candles, train, base, side, ctxs))
                                  if (a["zone"] or {}).get("level") != (b["zone"] or {}).get("level")),
            "byRegime": regime_deltas(candles, train, params, base, side, ctxs),
        }
    return out


def check_feature(candles, idxs, name):
    """Validate a plugin and run it on a few windows; returns what it produced or the error."""
    try:
        plugin = L.load_feature(name)
        samples = []
        for i in (idxs[0], idxs[len(idxs) // 2], idxs[-1]):
            levels = L.run_feature(plugin, candles[i - L.BARS:i])
            samples.append({"cutoff": L.iso(candles[i]["t"]), "lastClose": candles[i - 1]["c"],
                            "levels": levels[:10], "count": len(levels)})
        if all(s["count"] == 0 for s in samples):
            return {"ok": False, "error": "the plugin returned no levels on any sample window"}
        return {"ok": True, "name": name, "description": plugin["DESCRIPTION"],
                "source": plugin["SOURCE"], "samples": samples}
    except Exception as exc:  # report every failure as data for the Engineer to fix
        return {"ok": False, "error": "%s: %s" % (type(exc).__name__, exc)}


SEARCH_SPACE = {
    "swing_k": [3, 4, 6],
    "zone_anchor": [0.0, 0.25, 0.5, 0.75, 1.0],
    "min_distance_pct": [0.0, 0.5, 1.0],
    "min_distance_atr": [0.0, 1.0, 2.0, 3.0],
    "w_touches": [0.0, 1.0],
    "w_volume": [0.0, 1.0, 2.0],
    "w_reaction": [0.0, 0.5, 1.0],
    "w_recency": [0.0, 0.5],
    "w_confluence": [0.0, 1.0],
    "w_flip": [0.0, 1.0, 2.0],
    "w_distance": [-1.0, 0.0, 1.0, 3.0],
    "min_score": [0.0, 1.0, 1.5, 2.0],
    "trend_gate_pct": [99.0, 3.0, 5.0],
    "carry_days": [0, 1, 2],
}


def fmt(s):
    return "precision %5.1f%%  coverage %5.1f%%  (%d hits / %d touched / %d windows, %d abstained, %d carried)" % (
        100 * s["precision"], 100 * s["coverage"], s["hits"], s["touched"], s["windows"],
        s["abstained"], s["carried"])


def load_params(path):
    with open(path) as fh:
        return L.side_params(json.load(fh))


def write_json(path, obj):
    if path:
        with open(path, "w") as fh:
            json.dump(obj, fh, indent=1)
    else:
        print(json.dumps(obj, indent=1))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--prefetch", action="store_true", help="download/cache the 2026 candles and exit")
    ap.add_argument("--sandbox", action="store_true", help="offline, resource-limited process")
    ap.add_argument("--params", help="candidate params JSON (default: the skill's params.json)")
    ap.add_argument("--baseline", help="published params JSON to beat")
    ap.add_argument("--json", help="write the machine-readable result to this file")
    ap.add_argument("--report", help="write the train regime report to this file")
    ap.add_argument("--search", type=int, metavar="TRIALS", help="random search on train")
    ap.add_argument("--check-feature", metavar="NAME", help="validate and smoke-run a plugin")
    ap.add_argument("--evaluate-feature", metavar="NAME", help="judge a plugin against --baseline")
    ap.add_argument("--experiment", metavar="NAME", help="validate a plugin and measure it on train only")
    ap.add_argument("--alpha", type=float, default=ALPHA,
                    help="significance level per plugin (pass 0.05 / plugins judged in the run)")
    args = ap.parse_args()

    if args.sandbox:
        sandbox()
    candles = load_history()
    idxs = cutoffs(candles)
    split = int(len(idxs) * TRAIN_FRACTION)
    train, test = idxs[:split], idxs[split:]
    ctxs = Contexts(candles)
    by_side = load_params(args.params) if args.params else L.default_params()
    baseline = load_params(args.baseline) if args.baseline else None

    if args.prefetch:
        write_json(args.json, {"candles": len(candles), "from": L.iso(candles[0]["t"]),
                               "to": L.iso(candles[-1]["t"] + L.INTERVAL_MS), "windows": len(idxs),
                               "train": len(train), "test": len(test)})
        return
    if args.check_feature:
        write_json(args.json, check_feature(candles, idxs, args.check_feature))
        return
    if args.experiment:
        write_json(args.json, experiment(candles, train, args.experiment, baseline or by_side, ctxs))
        return
    if args.evaluate_feature:
        if not 0 < args.alpha <= ALPHA:
            raise SystemExit("--alpha must be in (0, %s]" % ALPHA)
        write_json(args.json, evaluate_feature(candles, train, test, args.evaluate_feature,
                                               baseline or by_side, ctxs, args.alpha))
        return
    if args.report and args.json:  # machine mode for the team workflow: no console summary
        write_json(args.report, regime_report(candles, train, by_side, ctxs))
        write_json(args.json, verdict(candles, test, by_side, baseline, ctxs))
        return

    print("history: %d candles, %d windows (train %d: %s..%s, test %d: %s..%s)" % (
        len(candles), len(idxs), len(train), L.iso(candles[train[0]]["t"]), L.iso(candles[train[-1]]["t"]),
        len(test), L.iso(candles[test[0]]["t"]), L.iso(candles[test[-1]]["t"])))
    for side in L.SIDES:
        print("\n%s  (random-zone base rate: train %.1f%%, test %.1f%%)" % (
            side, 100 * base_rate(candles, train, side), 100 * base_rate(candles, test, side)))
        print("  train  " + fmt(summarize(evaluate(candles, train, by_side[side], side, ctxs))))
        print("  test   " + fmt(summarize(evaluate(candles, test, by_side[side], side, ctxs))))

    v = verdict(candles, test, by_side, baseline, ctxs)
    print("\nreadiness on test: %s" % ("READY" if v["ready"] else "NOT READY"))
    for side, s in v["sides"].items():
        extra = ""
        if "improvementLowerBound" in s:
            extra = "  vs baseline %.1f%% (lower bound %+.1f pts)" % (
                100 * s["baselinePrecision"], 100 * s["improvementLowerBound"])
        print("  %-10s %-9s p=%.3f vs random%s  checks=%s" % (
            side, "ready" if s["ready"] else "not ready", s["pValueVsRandom"], extra, json.dumps(s["checks"])))
    if args.json:
        write_json(args.json, v)
    if args.report:
        write_json(args.report, regime_report(candles, train, by_side, ctxs))

    if args.search:
        rng = random.Random(SEED)
        trials = [dict(L.PARAMS, **{k: rng.choice(v) for k, v in SEARCH_SPACE.items()})
                  for _ in range(args.search)]
        print("\nrandom search on train (%d trials, coverage >= %d%%):" % (args.search, MIN_COVERAGE * 100))
        for side in L.SIDES:
            best = best_on_train(candles, train, side, trials, ctxs)
            if best is None:
                print("  %s: no config meets the coverage floor" % side)
                continue
            s, p = best
            print("  %s train  %s" % (side, fmt(s)))
            print("  %s test   %s" % (side, fmt(summarize(evaluate(candles, test, p, side, ctxs)))))
            print("  %s params %s" % (side, json.dumps({k: p[k] for k in SEARCH_SPACE})))


if __name__ == "__main__":
    main()
