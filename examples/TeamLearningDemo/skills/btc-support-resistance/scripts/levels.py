#!/usr/bin/env python3
"""Deterministic support/resistance toolkit for BTCUSDT on Binance (stdlib only, Python 3.9+).

Every number an agent reports must come from this script, never from the model's own
arithmetic: the same arguments always produce the same output, because only *closed*
candles of a fixed [start, end) range are used and each range is cached on disk.

Subcommands (all print one JSON object to stdout):
  fetch    --end T            summary of the analysis window (no raw candles)
  analyze  --end T            market context plus every raw level found on both sides
  levels   --end T [--side support|resistance|both] [--top N]
                              the recommended zone per side (fresh, carried over, or none)
                              and the ranked fresh candidates

T is the analysis cutoff (exclusive): an ISO-8601 UTC time ("2026-10-12T00:00:00Z") or epoch
milliseconds, floored to the 30-minute grid. The window is the 336 closed candles before T.
--params FILE selects the published parameters: {"support": {...}, "resistance": {...}}, each an
override of PARAMS below (unknown keys are errors). Without it, ../params.json is used when present.

Feature plugins: a side's "features" param maps a plugin name to a score weight. The plugin is
features/<name>.py in the skill (or a directory in BTC_LEVELS_FEATURE_PATH) and is validated before
it runs: see load_feature().
"""

import argparse
import ast
import builtins
import json
import math
import os
import signal
import sys
import tempfile
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone

SYMBOL = "BTCUSDT"
INTERVAL = "30m"
INTERVAL_MS = 30 * 60 * 1000
BARS = 336        # analysis window: 7 days of 30m candles
DAY_BARS = 48     # 24h: the evaluation horizon and the carry-over step
API = "https://api.binance.com/api/v3/klines"
PAGE = 1000       # Binance maximum candles per request
SIDES = ("support", "resistance")
SKILL_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Constraints every returned zone satisfies (see references/methodology.md).
MAX_WIDTH_PCT = 0.5     # (zoneHigh - zoneLow) / zoneHigh <= 0.5%
MAX_DISTANCE_PCT = 3.0  # support: zoneLow >= close * 0.97; resistance: zoneHigh <= close * 1.03

# Tunable parameters. The values are the ones the backtest selected on its training split.
PARAMS = {
    # structure
    "swing_k": 4,              # a swing is the extreme of the 2k+1 candles around it
    "cluster_tol_pct": 0.25,   # swings within this distance merge into one level
    "touch_tol_pct": 0.15,     # a candle "tests" a level if its low or high is this close
    "vp_bin_pct": 0.10,        # volume-profile bin size
    # zone placement
    "zone_width_pct": 0.50,    # capped by MAX_WIDTH_PCT
    "zone_anchor": 0.5,        # share of the zone on the price side of the level (0..1)
    "min_distance_pct": 0.0,   # skip levels closer than this to the close ...
    "min_distance_atr": 0.0,   # ... or closer than this many ATR(14)s
    # scoring
    "w_touches": 1.0,          # how often price reacted at the level
    "w_volume": 1.0,           # volume-profile weight at the level's price
    "w_reaction": 0.5,         # relative volume of the swing candles that formed the level
    "w_recency": 0.5,
    "w_confluence": 1.0,       # other methods agreeing on the same price
    "w_flip": 1.0,             # broken resistance acting as support (or the reverse)
    "w_distance": 0.0,         # >0 prefers deeper levels, <0 nearer ones
    # abstention and carry-over
    "min_score": 0.0,          # below this the side abstains from a fresh zone
    "trend_gate_pct": 99.0,    # abstain from supports after a 7d fall larger than this
                               # (and from resistances after a 7d rise larger than this)
    "carry_days": 1,           # on abstention, keep the previous day's zone if still valid
    # plugins
    "features": {},            # {"plugin_name": weight}: extra level generators, see load_feature()
}


# ---------------------------------------------------------------- data access

def _cache_dir():
    path = os.environ.get("BTC_LEVELS_CACHE") or os.path.join(tempfile.gettempdir(), "btc-levels-cache")
    os.makedirs(path, exist_ok=True)
    return path


def parse_time(value):
    """ISO-8601 (Z or offset) or epoch ms -> epoch ms floored to the 30m grid."""
    value = str(value).strip()
    if value.isdigit():
        ms = int(value)
    else:
        dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        ms = int(dt.timestamp() * 1000)
    return ms - ms % INTERVAL_MS


def iso(ms):
    return datetime.fromtimestamp(ms / 1000, tz=timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _get(params):
    url = API + "?" + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={"User-Agent": "btc-support-resistance/2.0"})
    last_error = None
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=20) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as exc:  # network hiccup or 429: back off and retry
            last_error = exc
            time.sleep(1.5 * (attempt + 1))
    raise RuntimeError("Binance request failed: %s" % last_error)


def load_candles(start_ms, end_ms):
    """Closed candles with open time in [start_ms, end_ms). Cached per exact range."""
    now_ms = int(time.time() * 1000)
    if end_ms > now_ms - now_ms % INTERVAL_MS:
        raise ValueError("end %s is in the future or inside the open candle; use a cutoff "
                         "at or before %s" % (iso(end_ms), iso(now_ms - now_ms % INTERVAL_MS)))
    cache = os.path.join(_cache_dir(), "%s_%s_%d_%d.json" % (SYMBOL, INTERVAL, start_ms, end_ms))
    if os.path.exists(cache):
        with open(cache) as fh:
            return json.load(fh)
    if os.environ.get("BTC_LEVELS_OFFLINE"):
        raise RuntimeError("offline mode and no cached candles for %s..%s" % (iso(start_ms), iso(end_ms)))

    candles = []
    cursor = start_ms
    while cursor < end_ms:
        rows = _get({"symbol": SYMBOL, "interval": INTERVAL, "startTime": cursor,
                     "endTime": end_ms - 1, "limit": PAGE})
        if not rows:
            break
        for r in rows:
            if start_ms <= r[0] < end_ms:
                candles.append({"t": r[0], "o": float(r[1]), "h": float(r[2]), "l": float(r[3]),
                                "c": float(r[4]), "v": float(r[5])})
        cursor = rows[-1][0] + INTERVAL_MS
        if len(rows) < PAGE:
            break

    tmp = cache + ".tmp"
    with open(tmp, "w") as fh:
        json.dump(candles, fh)
    os.replace(tmp, cache)  # atomic, so a concurrent reader never sees a partial file
    return candles


def merge_params(overrides):
    unknown = sorted(set(overrides) - set(PARAMS))
    if unknown:
        raise ValueError("unknown params: %s" % ", ".join(unknown))
    return dict(PARAMS, **overrides)


def side_params(doc):
    """{"support": {...}, "resistance": {...}} overrides -> full PARAMS per side."""
    unknown = sorted(set(doc) - set(SIDES) - {"version", "notes"})
    if unknown:
        raise ValueError("unknown params sections: %s" % ", ".join(unknown))
    return {side: merge_params(doc.get(side, {})) for side in SIDES}


def default_params():
    path = os.path.join(SKILL_DIR, "params.json")
    if os.path.exists(path):
        with open(path) as fh:
            return side_params(json.load(fh))
    return {side: dict(PARAMS) for side in SIDES}


# ---------------------------------------------------------------- feature plugins
#
# A plugin is Python written by the team's Engineer agent from an idea found on the web, so it is
# untrusted code. It must be a single module of this shape:
#
#   NAME = "anchored_vwap"            # == the file name
#   DESCRIPTION = "one line"
#   SOURCE = "https://..."            # where the idea came from
#   def levels(candles):              # closed 30m candles, oldest first: dicts t,o,h,l,c,v
#       return [{"level": 64250.0}]   # prices; the side is decided by where they sit vs the close
#
# Before it runs, the AST is checked: only `import math`, no name starting with "_", attributes
# only from a whitelist (math functions, list and dict methods), no classes or global/nonlocal.
# It then runs with a builtins table of pure functions only (no open, eval, exec, getattr, ...),
# an __import__ that yields nothing but math, and a per-call time limit. This blocks file,
# network and process access from plugin code; it is not a substitute for an OS-level sandbox,
# which is why the backtest also runs candidates in a separate offline, resource-limited process
# (backtest.py --sandbox).

SAFE_BUILTINS = {name: getattr(builtins, name) for name in (
    "abs", "all", "any", "bool", "dict", "enumerate", "filter", "float", "int", "isinstance", "len",
    "list", "map", "max", "min", "range", "reversed", "round", "set", "sorted", "sum", "tuple", "zip",
    "ValueError", "ZeroDivisionError")}
# Attributes are whitelisted, not blacklisted: names such as gi_frame / f_globals / format reach
# the interpreter without a leading underscore.
SAFE_ATTRIBUTES = frozenset(n for n in dir(math) if not n.startswith("_")) | frozenset((
    "append", "extend", "insert", "pop", "sort", "index", "count", "reverse", "copy",
    "get", "items", "keys", "values", "setdefault", "update"))
FORBIDDEN_NODES = (ast.ClassDef, ast.AsyncFunctionDef, ast.Await, ast.Global, ast.Nonlocal,
                   ast.AsyncFor, ast.AsyncWith, ast.With, ast.Yield, ast.YieldFrom, ast.Starred)
MAX_PLUGIN_BYTES = 20000
MAX_PLUGIN_LEVELS = 50
PLUGIN_CALL_SECONDS = 2.0
_plugins = {}


def validate_feature_source(source, name):
    """Raise ValueError naming the first rule the plugin source breaks."""
    if len(source.encode("utf-8")) > MAX_PLUGIN_BYTES:
        raise ValueError("plugin is larger than %d bytes" % MAX_PLUGIN_BYTES)
    tree = ast.parse(source, filename=name + ".py")
    for node in ast.walk(tree):
        where = "line %s" % getattr(node, "lineno", "?")
        if isinstance(node, FORBIDDEN_NODES):
            raise ValueError("%s: %s is not allowed" % (where, type(node).__name__))
        if isinstance(node, ast.Import):
            if [a.name for a in node.names] != ["math"] or node.names[0].asname:
                raise ValueError("%s: only `import math` is allowed" % where)
        if isinstance(node, ast.ImportFrom):
            if node.module != "math" or node.level:
                raise ValueError("%s: only imports from math are allowed" % where)
        if isinstance(node, ast.Attribute) and node.attr not in SAFE_ATTRIBUTES:
            raise ValueError("%s: attribute %r is not allowed" % (where, node.attr))
        if isinstance(node, ast.Name) and node.id.startswith("_"):
            raise ValueError("%s: name %r is not allowed" % (where, node.id))
    top = {}
    for node in tree.body:
        if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name):
            top[node.targets[0].id] = node.value
        if isinstance(node, ast.FunctionDef):
            top[node.name] = node
    for key in ("NAME", "DESCRIPTION", "SOURCE"):
        if not (isinstance(top.get(key), ast.Constant) and isinstance(top[key].value, str)):
            raise ValueError("%s must be a string literal" % key)
    if top["NAME"].value != name:
        raise ValueError("NAME %r does not match the file name %r" % (top["NAME"].value, name))
    fn = top.get("levels")
    if not isinstance(fn, ast.FunctionDef) or [a.arg for a in fn.args.args] != ["candles"]:
        raise ValueError("define `def levels(candles):` at module level")
    return tree


class _PluginTimeout(Exception):
    pass


def _on_alarm(signum, frame):
    raise _PluginTimeout()


def _import_math_only(name, *args, **kwargs):
    if name != "math":
        raise ImportError("only math can be imported")
    return math


def compile_feature(source, name):
    tree = validate_feature_source(source, name)
    namespace = {"__builtins__": dict(SAFE_BUILTINS, __import__=_import_math_only)}
    exec(compile(tree, name + ".py", "exec"), namespace)  # noqa: S102 - validated above
    return namespace


def load_feature(name):
    if name in _plugins:
        return _plugins[name]
    if not name.replace("_", "").isalnum() or not name.islower():
        raise ValueError("plugin name %r must be lower_snake_case" % name)
    dirs = [d for d in os.environ.get("BTC_LEVELS_FEATURE_PATH", "").split(os.pathsep) if d]
    dirs.append(os.path.join(SKILL_DIR, "features"))
    for d in dirs:
        path = os.path.join(d, name + ".py")
        if os.path.exists(path):
            with open(path) as fh:
                _plugins[name] = compile_feature(fh.read(), name)
            return _plugins[name]
    raise ValueError("plugin %r not found in %s" % (name, dirs))


def run_feature(namespace, candles):
    """Call a plugin's levels() under a time limit and check what it returns."""
    view = [dict(c) for c in candles]  # the plugin cannot mutate the caller's candles
    old = signal.signal(signal.SIGALRM, _on_alarm)
    signal.setitimer(signal.ITIMER_REAL, PLUGIN_CALL_SECONDS)
    try:
        out = namespace["levels"](view)
    except _PluginTimeout:
        raise ValueError("plugin %s exceeded %.1fs" % (namespace["NAME"], PLUGIN_CALL_SECONDS))
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, old)
    if not isinstance(out, list) or len(out) > MAX_PLUGIN_LEVELS:
        raise ValueError("plugin %s must return a list of at most %d levels" % (namespace["NAME"], MAX_PLUGIN_LEVELS))
    levels = []
    for item in out:
        level = item.get("level") if isinstance(item, dict) else None
        if not isinstance(level, (int, float)) or isinstance(level, bool) or not math.isfinite(level) or level <= 0:
            raise ValueError("plugin %s returned an invalid level: %r" % (namespace["NAME"], item))
        levels.append(float(level))
    return levels


# ---------------------------------------------------------------- analysis

def _median(xs):
    s = sorted(xs)
    n = len(s)
    return (s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2) if n else 0.0


def atr(candles, period=14):
    trs = []
    for i in range(1, len(candles)):
        h, l, pc = candles[i]["h"], candles[i]["l"], candles[i - 1]["c"]
        trs.append(max(h - l, abs(h - pc), abs(l - pc)))
    tail = trs[-period:]
    return sum(tail) / len(tail) if tail else 0.0


def swing_points(candles, k, key):
    """Indexes of swing lows (key "l") or swing highs (key "h")."""
    out = []
    for i in range(k, len(candles) - k):
        x = candles[i][key]
        span = range(i - k, i + k + 1)
        if (key == "l" and all(x <= candles[j]["l"] for j in span)) or \
           (key == "h" and all(x >= candles[j]["h"] for j in span)):
            out.append(i)
    return out


def cluster_swings(candles, idx, key, tol_pct, med_vol):
    """Merge swings within tol_pct of the running cluster mean into levels."""
    points = sorted((candles[i][key], i) for i in idx)
    groups = []
    for price, i in points:
        if groups and abs(price - groups[-1]["mean"]) / groups[-1]["mean"] * 100 <= tol_pct:
            g = groups[-1]
            g["prices"].append(price)
            g["idx"].append(i)
            g["mean"] = sum(g["prices"]) / len(g["prices"])
        else:
            groups.append({"prices": [price], "idx": [i], "mean": price})
    out = []
    for g in groups:
        prices = sorted(g["prices"])
        reaction = sum(candles[i]["v"] for i in g["idx"]) / len(g["idx"]) / med_vol if med_vol else 0.0
        out.append({"level": prices[len(prices) // 2], "swings": len(prices),
                    "last_idx": max(g["idx"]), "reaction_vol": reaction})
    return out


def touches(candles, level, tol_pct):
    lo, hi = level * (1 - tol_pct / 100), level * (1 + tol_pct / 100)
    return sum(1 for c in candles if lo <= c["l"] <= hi or lo <= c["h"] <= hi)


def breakout_volume(candles, level, after_idx, upward, med_vol):
    """Relative volume of the first close through `level` after `after_idx`."""
    for c in candles[after_idx + 1:]:
        if (upward and c["c"] > level) or (not upward and c["c"] < level):
            return c["v"] / med_vol if med_vol else 0.0
    return 0.0


def volume_profile(candles, bin_pct, ref):
    lo = min(c["l"] for c in candles)
    hi = max(c["h"] for c in candles)
    size = ref * bin_pct / 100
    n = max(1, int(math.ceil((hi - lo) / size)))
    bins = [0.0] * n
    for c in candles:
        a = min(n - 1, int((c["l"] - lo) / size))
        b = min(n - 1, int((c["h"] - lo) / size))
        share = c["v"] / (b - a + 1)
        for k in range(a, b + 1):
            bins[k] += share
    mean = sum(bins) / n
    nodes = []
    for k in range(n):
        left = bins[k - 1] if k > 0 else -1
        right = bins[k + 1] if k < n - 1 else -1
        if bins[k] >= left and bins[k] >= right and bins[k] > 1.5 * mean:
            nodes.append({"level": lo + (k + 0.5) * size, "volume_ratio": bins[k] / mean})

    def at(level):
        k = int((level - lo) / size)
        return bins[k] / mean if 0 <= k < n and mean > 0 else 0.0

    return nodes, at


def pivots(candles):
    day = candles[-DAY_BARS:]
    h = max(c["h"] for c in day)
    l = min(c["l"] for c in day)
    p = (h + l + day[-1]["c"]) / 3
    return {"P": p, "S1": 2 * p - h, "S2": p - (h - l), "R1": 2 * p - l, "R2": p + (h - l)}


def context(candles, params):
    """Everything about one window that does not depend on the scoring weights."""
    n = len(candles)
    last = candles[-1]["c"]
    med_vol = _median([c["v"] for c in candles])
    lows = cluster_swings(candles, swing_points(candles, params["swing_k"], "l"), "l",
                          params["cluster_tol_pct"], med_vol)
    highs = cluster_swings(candles, swing_points(candles, params["swing_k"], "h"), "h",
                           params["cluster_tol_pct"], med_vol)
    nodes, vol_at = volume_profile(candles, params["vp_bin_pct"], last)
    piv = pivots(candles)
    tol = params["touch_tol_pct"]

    raw = []  # every level, tagged with the side it serves *now*
    for s in lows:
        side = "support" if s["level"] < last else "resistance"
        flip = side == "resistance"  # a swing low above the close is a broken support
        raw.append(dict(s, side=side, method="flip_support" if flip else "swing_low",
                        breakout_vol=breakout_volume(candles, s["level"], s["last_idx"], False, med_vol)
                        if flip else 0.0,
                        recency=s["last_idx"] / (n - 1)))
    for s in highs:
        side = "resistance" if s["level"] > last else "support"
        flip = side == "support"  # a swing high below the close is a broken resistance
        raw.append(dict(s, side=side, method="flip_resistance" if flip else "swing_high",
                        breakout_vol=breakout_volume(candles, s["level"], s["last_idx"], True, med_vol)
                        if flip else 0.0,
                        recency=s["last_idx"] / (n - 1)))
    for v in nodes:
        raw.append({"level": v["level"], "side": "support" if v["level"] < last else "resistance",
                    "method": "volume_node", "swings": 0, "reaction_vol": 0.0, "breakout_vol": 0.0,
                    "recency": 0.0})
    for name in ("S1", "S2", "R1", "R2"):
        side = "support" if piv[name] < last else "resistance"
        raw.append({"level": piv[name], "side": side, "method": "pivot_" + name, "swings": 0,
                    "reaction_vol": 0.0, "breakout_vol": 0.0, "recency": 1.0})
    for name in sorted(params["features"]):
        for level in run_feature(load_feature(name), candles):
            raw.append({"level": level, "side": "support" if level < last else "resistance",
                        "method": "feature:" + name, "swings": 0, "reaction_vol": 0.0,
                        "breakout_vol": 0.0, "recency": 1.0})

    for r in raw:
        r["touches"] = touches(candles, r["level"], tol)
        r["volume"] = vol_at(r["level"])
        r["confluence"] = sum(1 for o in raw if o["method"] != r["method"] and
                              abs(o["level"] - r["level"]) / last * 100 <= params["cluster_tol_pct"])

    a = atr(candles)
    return {
        "last_close": last,
        "atr14": a,
        "atr_pct": a / last * 100,
        "return_7d_pct": (last / candles[0]["o"] - 1) * 100,
        "return_24h_pct": (last / candles[-DAY_BARS]["o"] - 1) * 100,
        "window_low": min(c["l"] for c in candles),
        "window_high": max(c["h"] for c in candles),
        "median_volume": med_vol,
        "pivots": piv,
        "levels": raw,
    }


def place(level, side, last, params):
    """Zone around a level; width is a share of zoneHigh so it does not drift with price."""
    w = min(params["zone_width_pct"], MAX_WIDTH_PCT) / 100
    a = params["zone_anchor"]
    if side == "support":
        zh = min(level * (1 + w * a), last)
        zl = zh * (1 - w)
    else:
        zl = max(level * (1 - w * a), last)
        zh = zl / (1 - w)
    return zl, zh


def within_constraints(zl, zh, side, last):
    if (zh - zl) / zh * 100 > MAX_WIDTH_PCT + 1e-9:
        return False
    if side == "support":
        return zl < last and zl >= last * (1 - MAX_DISTANCE_PCT / 100)
    return zh > last and zh <= last * (1 + MAX_DISTANCE_PCT / 100)


def candidates(ctx, side, params, top=5):
    last = ctx["last_close"]
    raw = [r for r in ctx["levels"] if r["side"] == side]
    max_touch = max([r["touches"] for r in raw] + [1])
    min_dist = max(params["min_distance_pct"], params["min_distance_atr"] * ctx["atr_pct"])
    zones = []
    for r in raw:
        distance = abs(last - r["level"]) / last * 100
        if distance < min_dist:
            continue
        zl, zh = place(r["level"], side, last, params)
        if not within_constraints(zl, zh, side, last):
            continue
        flip = r["method"].startswith("flip_")
        plugin = params["features"].get(r["method"][len("feature:"):], 0.0) \
            if r["method"].startswith("feature:") else 0.0
        score = (plugin
                 + params["w_touches"] * r["touches"] / max_touch
                 + params["w_volume"] * min(r["volume"], 3.0) / 3.0
                 + params["w_reaction"] * min(r["reaction_vol"], 3.0) / 3.0
                 + params["w_recency"] * r["recency"]
                 + params["w_confluence"] * min(r["confluence"], 3) / 3.0
                 + (params["w_flip"] * (0.5 + 0.5 * min(r["breakout_vol"], 3.0) / 3.0) if flip else 0.0)
                 + params["w_distance"] * distance / MAX_DISTANCE_PCT)
        zones.append({
            "side": side, "level": round(r["level"], 2), "zoneLow": round(zl, 2), "zoneHigh": round(zh, 2),
            "method": r["method"], "touches": r["touches"], "swings": r["swings"],
            "confluence": r["confluence"], "volumeRatio": round(r["volume"], 3),
            "reactionVolume": round(r["reaction_vol"], 3), "breakoutVolume": round(r["breakout_vol"], 3),
            "distancePct": round(distance, 3), "widthPct": round((zh - zl) / zh * 100, 3),
            "score": round(score, 4),
        })
    # Nearest first on ties, so equal scores always resolve the same way.
    zones.sort(key=lambda z: (-z["score"], z["distancePct"]))
    distinct = []  # drop near-duplicates so the agent sees genuinely different alternatives
    for z in zones:
        if all(abs(z["level"] - d["level"]) / last * 100 > params["cluster_tol_pct"] for d in distinct):
            distinct.append(z)
    return distinct[:top]


def fresh_zone(ctx, side, params):
    """(zone, None) or (None, reason) for one window and side, without carry-over."""
    trend = ctx["return_7d_pct"]
    gate = params["trend_gate_pct"]
    if (side == "support" and trend < -gate) or (side == "resistance" and trend > gate):
        return None, "trend_gate"
    best = candidates(ctx, side, params, top=1)
    if not best:
        return None, "no_valid_candidate"
    if best[0]["score"] < params["min_score"]:
        return None, "below_min_score"
    return best[0], None


def still_valid(zone, since, side, last):
    """A carried zone survives if no close broke it since it was issued and it still fits."""
    if side == "support" and any(c["c"] < zone["zoneLow"] for c in since):
        return False
    if side == "resistance" and any(c["c"] > zone["zoneHigh"] for c in since):
        return False
    return within_constraints(zone["zoneLow"], zone["zoneHigh"], side, last)


def recommend(candles, i, side, params, ctx_at, depth=None):
    """Zone for the cutoff at index i (window candles[i-BARS:i]), with carry-over.

    ctx_at(i) returns context() of that window; the backtest passes a cached one.
    """
    if depth is None:
        depth = params["carry_days"]
    zone, reason = fresh_zone(ctx_at(i), side, params)
    if zone:
        return dict(zone, source="fresh", issuedAt=iso(candles[i - 1]["t"] + INTERVAL_MS)), None
    if depth <= 0 or i - DAY_BARS < BARS:
        return None, reason
    prev, _ = recommend(candles, i - DAY_BARS, side, params, ctx_at, depth - 1)
    if prev and still_valid(prev, candles[i - DAY_BARS:i], side, candles[i - 1]["c"]):
        return dict(prev, source="carried"), None
    return None, reason


# ---------------------------------------------------------------- CLI

def _round(obj):
    if isinstance(obj, float):
        return round(obj, 3)
    if isinstance(obj, dict):
        return {k: _round(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [_round(v) for v in obj]
    return obj


def main(argv):
    ap = argparse.ArgumentParser(prog="levels.py", description=__doc__.split("\n\n")[0])
    ap.add_argument("command", choices=["fetch", "analyze", "levels"])
    ap.add_argument("--end", required=True, help="analysis cutoff (exclusive), ISO-8601 UTC or epoch ms")
    ap.add_argument("--side", choices=["support", "resistance", "both"], default="both")
    ap.add_argument("--top", type=int, default=5, help="levels: fresh candidates per side")
    ap.add_argument("--params", help="JSON file overriding PARAMS")
    args = ap.parse_args(argv)

    if args.params:
        with open(args.params) as fh:
            by_side = side_params(json.load(fh))
    else:
        by_side = default_params()

    end_ms = parse_time(args.end)
    extra = max(p["carry_days"] for p in by_side.values()) * DAY_BARS if args.command == "levels" else 0
    candles = load_candles(end_ms - (BARS + extra) * INTERVAL_MS, end_ms)
    if len(candles) < BARS + extra:
        raise ValueError("only %d of %d candles available before %s" % (len(candles), BARS + extra, iso(end_ms)))
    win = candles[-BARS:]
    head = {"symbol": SYMBOL, "interval": INTERVAL, "windowStart": iso(win[0]["t"]),
            "windowEnd": iso(end_ms), "bars": len(win)}

    if args.command == "fetch":
        out = dict(head, lastClose=win[-1]["c"], windowLow=min(c["l"] for c in win),
                   windowHigh=max(c["h"] for c in win))
    elif args.command == "analyze":
        sides = SIDES if args.side == "both" else (args.side,)
        out = dict(head)
        for side in sides:
            ctx = context(win, by_side[side])
            ctx["levels"] = [r for r in ctx["levels"] if r["side"] == side]
            out[side] = _round(ctx)
    else:
        n = len(candles)
        out = dict(head, lastClose=win[-1]["c"],
                   constraints={"maxWidthPct": MAX_WIDTH_PCT, "maxDistancePct": MAX_DISTANCE_PCT})
        sides = SIDES if args.side == "both" else (args.side,)
        for side in sides:
            params = by_side[side]
            ctx_at = lambda i, p=params: context(candles[i - BARS:i], p)  # noqa: E731
            ctx = ctx_at(n)
            out.update(atrPct=round(ctx["atr_pct"], 3), return7dPct=round(ctx["return_7d_pct"], 3))
            zone, reason = recommend(candles, n, side, params, ctx_at)
            out[side] = {"recommended": zone, "abstainReason": reason,
                         "candidates": candidates(ctx, side, params, args.top)}
    print(json.dumps(out, indent=2))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except Exception as exc:  # report errors as JSON so the agent can read them
        print(json.dumps({"error": str(exc)}))
        sys.exit(1)
