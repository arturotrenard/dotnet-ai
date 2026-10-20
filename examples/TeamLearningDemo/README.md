# TeamLearningDemo

A small team of agents improves a runtime skill by using it. It publishes a new version of the
skill **only when a deterministic backtest says the change is better**.

The task is to compute the BTCUSDT support and resistance zones on Binance 30m candles for a cutoff
time. The skill is [`skills/btc-support-resistance`](skills/btc-support-resistance/SKILL.md): a
`SKILL.md` plus `scripts/levels.py`, which does all the reading and the math deterministically.

## The pattern

`POST /learn` starts `TeamLearningWorkflow`:

| Step | Who | What |
|---|---|---|
| 1 | `PrepareBaselineActivity` | caches the candles and scores the published skill with `backtest/backtest.py` |
| 2 | **AnalystAgent** | does the task with the skill (`levels.py`) and reports the zones |
| 3 | **EngineerAgent** | reads where the skill fails and writes one plugin (`features/<name>.py`) aimed at it |
| 4 | `EvaluatePluginActivity` | validates the plugin in a sandbox and backtests it; a sandbox rejection goes back to the engineer, up to 3 attempts |
| 5 | **SkillCuratorAgent** | writes the lesson |
| 6 | `PublishActivity` | if the backtest promoted the plugin, publishes it as the next skill version |

The split is the point:

- **Agents only propose:** a plugin and a lesson.
- **Activities measure and decide:** the sandbox check, the verdict and the publication.

No model can talk a change into the skill, and the workflow replays to the same decisions.

### When a change is "better"

A side (support or resistance) improves when, on a held-out test split of 2026 data, three
conditions hold:

1. Coverage stays at 70% or more.
2. Precision beats a zone placed at random.
3. Precision beats the published version.

`backtest/backtest.py` and `references/methodology.md` give the details.

### Plugins are untrusted code

The engineer's code is model output, so `levels.py` validates every plugin before it runs. It
allows:

- `import math` only;
- attributes from a whitelist only;
- pure builtins only.

Each call has a time limit, and the backtest runs candidates in an offline,
resource-limited `python3 -I` process with a scrubbed environment. These layers are not an
OS-level sandbox; in production, run the evaluation in a container without network access.

## Going further

This example is the minimal shape of the pattern. It grows naturally in several directions:

- scouts that harvest ideas from curated references into a persistent backlog;
- engineers that experiment in parallel on a shared blackboard (train data only);
- a synthesizer that combines their findings;
- multiple-comparison correction when many candidates share one test split;
- pausing runs when the model provider's usage limit is reached.

## Run

Prerequisites:

- `dapr init`;
- `python3` 3.9 or newer;
- an Ollama Cloud key in the repo's `.env` (see `examples/Components/conversation-ollama.yaml`).

From `examples/`:

```
set -a; . ../.env; set +a
dapr run --app-id teamlearning --app-port 5061 --dapr-grpc-port 50001 --dapr-http-port 3500 --resources-path Components/
```

From `examples/TeamLearningDemo/`, in another shell:

```
dotnet run
curl -s -X POST localhost:5061/learn -H 'content-type: application/json' -d '{"cutoff": "2026-10-19T00:00:00Z"}'
curl -s localhost:5061/learn/<instanceId>
```

Without the agents:

```
python3 backtest/backtest.py --prefetch                   # cache the 2026 candles
python3 backtest/backtest.py --report report.json         # verdict + failures by regime
python3 skills/btc-support-resistance/scripts/levels.py levels --end 2026-10-19T00:00:00Z
```
