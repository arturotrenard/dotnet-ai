# Final design: conversation store (DaprSessionStore-style) and model reasoning

This plan is based on `dev` @ 02b5e3a. I re-read `AgentRunWorkflow.cs`, `CallLlmActivity.cs`, `SessionWorkflow.cs`, `WorkflowChatMessage(Converter).cs`, `DaprAgentInvocation.cs`, `AgentRunResult.cs`, `WorkflowContextExtensions.cs`, `DaprSessionExtensions.cs`, `DaprAgentInvoker.cs`, `DaprAgentsBuilderExtensions.cs:560-600`, `dapr_session_store.py` and the Dapr.StateManagement / Dapr.Workflow 1.19 XML docs. Nothing was edited.

**Base design.** The judges picked *faithful* twice and *workflow-first* once, so faithful is the base, with these grafts:
- From workflow-first:
  - sequence-numbered chunk descriptors;
  - per-turn dedupe keyed on a `NewGuid` turn id;
  - the dedupe check runs before any conflict check;
  - a golden test of the activity sequence;
  - the reasoning strip toward context providers.
- From minimal:
  - parity with `TurnMessages` on max-iterations (no synthetic message);
  - `AgentRunOptions` fields override only when non-null;
  - never delete after an ambiguous outcome.

**Fatal flaws fixed:**
1. **Deleting chunks after an ambiguous commit outcome could make a conversation permanently unloadable.** This flaw is inherited from Python and appears in both faithful and workflow-first. It is replaced by a single provable deletion rule (§1.5).
2. **A turn id built from `InstanceId` alone silently drops a legitimate turn when a purged instance id is reused.** The turn id is now `InstanceId:NewGuid`.
3. **Per-entry dedupe could be evicted by a few concurrent turns.** Dedupe is now per turn, with its own window.
4. **Replay of a run in a process without `WithConversationStore` found no activity to call.** The activity now always exists (§2.6).
5. **Faithful stored a synthetic message on max-iterations.** That is dropped; parity with `TurnMessages` is kept.
6. **workflow-first's detached overload put two strings side by side.** The overload is deferred.
7. **The public surface was too large (judge 2).** `ConflictMode`, `ReasoningReplay`, per-run effort and the detached overload are all deferred.

---

## 0. Facts the design depends on

**Verified in code:**
- **`AgentRunWorkflow`**
  - Activity order: `ResolveAgentContext` → loop of up to 20 × (`CallLlm` → n × `ExecuteTool`) → `CompleteAgentContext`. `CompleteAgentContext` also runs in the `catch`.
  - Tool results are added only after every call in an iteration has run. `ExecuteToolActivity` turns exceptions into results (`catch … when not OperationCanceledException`, line 127). So the in-run list never holds a tool call without its result.
  - The max-iterations path returns `TurnMessages` that end with tool results, and the synthetic text is not in `messages`.
  - A failed run returns nothing, and `SessionWorkflow` adds only successful turns.
- **`CallLlmActivity`**
  - `BuildChatOptions` (172-193) builds a fresh `ChatOptions`. `ResponseFormat = agentRunOptions?.ResponseFormat` overwrites everything with null.
  - `ParseResponse` uses only `Messages[^1]`, which throws an unhelpful `IndexOutOfRange` when the list is empty, and it drops reasoning.
- **`RegisterAgentComponents`** (`DaprAgentsBuilderExtensions.cs:~575-590`) keeps only `GetAgentChatOptions(cca)?.Tools`.
- **Activity registration.** `src/` has no explicit `RegisterActivity` calls; built-in activities are discovered from the assembly. **Confirm in PR 4** that a new `WorkflowActivity` type in the assembly is registered automatically. If it is not, register it next to the others unconditionally.
- **`DaprStateManagementClient` 1.19** provides:
  - `GetStateAndETagAsync`, `GetBulkStateAsync`, `SaveStateAsync`, `DeleteStateAsync`, `DeleteBulkStateAsync`, `ExecuteStateTransactionAsync`;
  - `TrySaveStateAsync(store, key, value, etag, StateOptions?, metadata?, ct)`, which returns `false` on an ETag mismatch.
- **Current usage and serialization.** `AddDaprAgents` already registers the client. `WorkflowContext.NewGuid()` exists and is replay-safe.

**Not verified** (each one is pinned by a test, see §7): the ones that need code to settle are listed in §8.

---

## 1. The store

### 1.1 Files

There is a new folder, `src/Diagrid.AI.Microsoft.AgentFramework/Conversations/`. Every file gets the BSL header and four-space indentation.

| File | Visibility | Contents |
|---|---|---|
| `IConversationStore.cs` | public | the abstraction |
| `ConversationTypes.cs` | public | `ConversationTurn`, `ConversationEntry`, `ConversationSnapshot`, `ConversationAppendResult`, `ConversationAppendStatus` |
| `DaprConversationStoreOptions.cs` | public | options + `Validate()` |
| `ConversationStoreException.cs` | public | `ConversationStoreException`, plus the derived `ConversationNotFoundException` |
| `DaprConversationStore.cs` | **internal** sealed | the port. Its doc comment says: "Port of dapr-agents `DaprSessionStore` (`dapr_agents/agents/executors/dapr_session_store.py`)" |
| `Internal/IConversationStateBackend.cs`, `Internal/DaprConversationStateBackend.cs` | internal | a thin seam over the 5 client calls, so tests can inject ETag conflicts, lost replies and clock changes |
| `Internal/ConversationManifest.cs`, `Internal/ConversationChunk.cs`, `Internal/StoredConversationEntry.cs`, `Internal/StoredChatMessage.cs` | internal | storage DTOs, kept separate from the public `WorkflowChatMessage` |
| `Internal/ConversationStoreJsonContext.cs` | internal | source-generated, `JsonNamingPolicy.SnakeCaseLower` |

The interface is public so a host can plug in its own store; registration uses `TryAddSingleton`. The implementation stays internal.

### 1.2 Public API (XML-doc-ready)

```csharp
/// <summary>Durable, append-only store of agent conversation history, kept outside workflow payloads.
/// Each committed turn is all-or-nothing; a failed agent run never contributes entries.</summary>
public interface IConversationStore
{
    /// <summary>Loads the committed history of a conversation.</summary>
    /// <param name="conversationId">Opaque conversation identifier chosen by the caller.</param>
    /// <param name="upToSequence">When set, returns only entries with <c>Sequence &lt; upToSequence</c>
    /// (a stable prefix); <see langword="null"/> returns everything committed so far.</param>
    /// <returns>The snapshot, or an empty snapshot (<c>NextSequence = 0</c>) if the conversation does not exist
    /// and <paramref name="upToSequence"/> is null or 0.</returns>
    /// <exception cref="ConversationNotFoundException">The requested prefix no longer exists (deleted or expired).</exception>
    Task<ConversationSnapshot> LoadAsync(string conversationId, long? upToSequence = null, CancellationToken cancellationToken = default);

    /// <summary>Appends one turn atomically and idempotently: re-appending the same <see cref="ConversationTurn.TurnId"/>
    /// is a no-op that returns <see cref="ConversationAppendStatus.AlreadyAppended"/>.</summary>
    Task<ConversationAppendResult> AppendTurnAsync(ConversationTurn turn, CancellationToken cancellationToken = default);

    /// <summary>Deletes a conversation and its chunks. Returns <see langword="false"/> if it did not exist.</summary>
    Task<bool> DeleteAsync(string conversationId, CancellationToken cancellationToken = default);
}

/// <summary>One agent run's messages, committed as a unit.</summary>
/// <param name="TurnId">Idempotency key; unique per run and stable across workflow replay.</param>
/// <param name="BaseSequence">The <see cref="ConversationSnapshot.NextSequence"/> the run was produced against.</param>
public sealed record ConversationTurn(string ConversationId, string TurnId, long BaseSequence,
    string AgentName, string? ChatClientKey, IReadOnlyList<WorkflowChatMessage> Messages);

/// <summary>A committed message with its position and provenance.</summary>
public sealed record ConversationEntry(long Sequence, string TurnId, string AgentName, string? ChatClientKey,
    DateTimeOffset CreatedAt, WorkflowChatMessage Message);

/// <summary>A stable prefix of a conversation.</summary>
public sealed record ConversationSnapshot(long NextSequence, IReadOnlyList<ConversationEntry> Entries);

/// <summary>Outcome of <see cref="IConversationStore.AppendTurnAsync"/>.</summary>
public enum ConversationAppendStatus { Appended, AlreadyAppended }

/// <param name="FirstSequence">Sequence of the turn's first entry.</param>
/// <param name="NextSequence">Conversation's next sequence after the commit.</param>
/// <param name="BaseMoved">True when other turns were committed after <see cref="ConversationTurn.BaseSequence"/>,
/// i.e. this turn was produced without seeing them.</param>
public sealed record ConversationAppendResult(ConversationAppendStatus Status, long FirstSequence, long NextSequence, bool BaseMoved);

public sealed class DaprConversationStoreOptions
{
    /// <summary>Dapr state store component name. Must support ETags (and TTL when <see cref="TimeToLive"/> is set).</summary>
    public string StateStoreName { get; set; } = "";
    /// <summary>Prefix for every key written, on top of the component's own keyPrefix (default: app id).</summary>
    public string KeyPrefix { get; set; } = "agent-conversation:";
    /// <summary>Upper bound for one chunk's serialized entries. Keep below the store's value limit (DynamoDB 400 KB).</summary>
    public int MaxChunkBytes { get; set; } = 256 * 1024;
    /// <summary>Number of most recent turn ids remembered for idempotent appends. Minimum 16.</summary>
    public int TurnDedupeWindow { get; set; } = 128;
    /// <summary>Manifest commit attempts under contention before <see cref="ConversationStoreException"/>.</summary>
    public int MaxCommitAttempts { get; set; } = 10;
    /// <summary>Optional sliding TTL: a conversation expires this long after its last append.</summary>
    public TimeSpan? TimeToLive { get; set; }
}
```

`Validate()` checks the following:
- `StateStoreName` is not whitespace;
- `MaxChunkBytes > 0`;
- `TurnDedupeWindow >= 16`;
- `MaxCommitAttempts > 0`;
- `TimeToLive` is null or at least 1 s, and is converted with `(int)Math.Ceiling(TotalSeconds)`.

### 1.3 Key layout

- **Manifest:** `{KeyPrefix}{escape(conversationId)}`, where `escape` replaces `||` with `__`, as `_session_key_string` does. The id is checked with `ThrowIfNullOrWhiteSpace` and must be at most 256 characters.
- **Chunk:** `{manifestKey}#{chunkId}`, where `chunkId = Guid.NewGuid().ToString("N")`. It is generated in the activity and does not need to be deterministic.
- The component's default `keyPrefix: appid` still applies, so conversations are per app. This goes in the docs. Sharing the workflow's actor `statestore` is fine because keys are prefixed. A dedicated component is recommended when a TTL is used.

### 1.4 Document formats

Fields are snake_case and set explicitly through the source-generated context. Each document is serialized once to a `JsonElement`, and that element is handed to `SaveStateAsync<JsonElement>` / `TrySaveStateAsync<JsonElement>`. The client's Web-default reflection serializer writes a `JsonElement` unchanged. The same UTF-8 bytes are used for chunk sizing. Every save carries metadata `contentType=application/json`.

```jsonc
// manifest — same roles as the Python manifest; chunk ids became descriptors (deliberately not Python-wire-compatible)
{ "version": 1,
  "next_seq": 57,
  "chunks": [ { "id": "a1…", "first_seq": 0,  "count": 40, "bytes": 190312 },
              { "id": "b2…", "first_seq": 40, "count": 17, "bytes": 30211 } ],
  "turns":  [ { "id": "run-123:9f…", "first_seq": 52, "count": 5, "base_seq": 52 } ],  // last TurnDedupeWindow
  "mtime": 1767000000000,
  "chunks_written_ms": 1767000000000 }

// chunk (immutable once written; TTL refresh rewrites identical bytes)
{ "entries": [ { "seq": 52, "turn_id": "run-123:9f…", "agent": "Planner", "client_key": null,
    "created_ms": 1767000000000,
    "message": { "role": "assistant", "content": "…",
                 "reasoning": [ { "text": "…", "protected_data": "…" } ],
                 "function_calls": [ { "call_id": "…", "name": "…", "arguments_json": "{}" } ],
                 "function_results": null } } ] }
```

`tail_bytes` is `chunks[^1].bytes`. The `first_seq`/`count` descriptors let a pinned load fetch only the chunks it needs.

### 1.5 `AppendTurnAsync`: the ambiguous-outcome flaw fixed

**Deletion rule.** A chunk written by this call may be deleted only when both of these hold:
- **(a)** the manifest just read does not reference it;
- **(b)** the commit that would have referenced it can no longer land, because the manifest's current ETag differs from the ETag that commit was conditioned on. For a first write there was no ETag, so the condition is that the key now exists.

An ETag-conditioned save can only land while the store still holds that ETag, and store versions do not go back. The one exception is a conversation deleted and recreated while the append was in flight, which is documented. So the rule never removes a chunk that a manifest references now or could reference later. This holds whether the reply was lost, the sidecar's resiliency layer retried, or the request arrived late. Every other unreferenced chunk is left for TTL. Without a TTL it leaks, as it does in Python. The README recommends a TTL.

```
AppendTurnAsync(turn):
  validate turn (messages non-empty, ids non-blank)
  key = ManifestKey(turn.ConversationId)
  pending = []                                   // list of (chunkIds, conditionedOnEtag | NoKey)
  for attempt in 1..MaxCommitAttempts:
     (m, etag) = backend.GetManifest(key, Consistency.Strong)        // null,null when absent
     Settle(pending, m, etag)                    // apply the deletion rule; drop settled items
     if m?.turns contains turn.TurnId as t:      // dedupe BEFORE anything else
         return AlreadyAppended(t.first_seq, m.next_seq, BaseMoved: t.base_seq != t.first_seq)
     next = m?.next_seq ?? 0
     entries = turn.Messages.Select((msg,i) => Entry(seq: next+i, turn.TurnId, agent, clientKey, now, msg))
     // plan (Python _plan_append)
     kept = m?.chunks ?? []; superseded = null; toWrite = entries
     if kept.Any && kept[^1].bytes < MaxChunkBytes/2:
         superseded = kept[^1]; kept = kept[..^1]
         tail = backend.GetChunk(superseded) ?? → treat as conflict: continue (a concurrent fold moved it)
         toWrite = tail.entries ++ entries
     chunksWrittenMs = m?.chunks_written_ms ?? now
     if TTL && kept.Any && now - chunksWrittenMs >= ttl:
         rewrite each kept chunk in place (same key, same bytes, ttlInSeconds = 2*ttl); chunksWrittenMs = now
     // write
     newChunks = split toWrite greedily by serialized size (oversized entry → own chunk);
                 SaveState each under a fresh id, ttlInSeconds = 2*ttl when TTL
     pending.Add((newChunks.ids, etag ?? NoKey))
     m' = { version 1, next_seq: next + entries.Count,
            chunks: kept ++ newChunks.descriptors,
            turns: (m?.turns ++ {turn.TurnId, next, entries.Count, turn.BaseSequence})[^TurnDedupeWindow..],
            mtime: max(now, m?.mtime + 1), chunks_written_ms: chunksWrittenMs }
     try:
        ok = etag is null
             ? backend.CreateIfAbsent(key, m', ttl)          // see §8.1
             : backend.TrySave(key, m', etag, FirstWrite+Strong, ttl)
     catch transient: ok = unknown
     if ok == true:
        pending.RemoveLast()                                  // now referenced
        best-effort delete superseded (warning on failure)    // no longer referenced; pinned readers retry
        best-effort Settle(pending, m', etagUnknown→skip)     // only deletes when rule provably holds; else leave to TTL
        return Appended(next, m'.next_seq, BaseMoved: next != turn.BaseSequence)
     // ok == false or unknown: do NOT delete anything here — the next iteration's read settles it
     backoff(attempt)   // min(50ms·attempt, 500ms)·(1+U[0,0.25])
  throw ConversationStoreException("could not commit after N attempts")   // pending left to TTL
```

`Settle` is the deletion rule applied to every pending entry: delete an entry's chunks only if `m` does not reference them and `etag != entry.conditionedOnEtag` (or, for `NoKey`, `m` exists). If the read itself throws, nothing is deleted and the exception propagates, the same choice Python's `_committed` makes.

Each piece and why it is correct:
- **Lost reply, resiliency retry, or late landing:** the next read finds `TurnId` in `turns` and returns `AlreadyAppended`. Nothing that was committed is ever deleted.
- **The ETag race was lost:** the manifest moved, so the ETag differs and the chunks are not referenced. They are deleted, and the next attempt rebuilds on the new manifest.
- **The tail was folded by a concurrent writer:** the old tail is deleted only once the new manifest no longer references it. A reader holding the old manifest gets a missing chunk and retries (§1.6). Entries keep their `seq`, so a pinned prefix is unchanged.
- **`TurnDedupeWindow = 128` turns.** A redelivery of the append activity happens only within its retry span (§2.4, about 2 minutes). Duplicating a turn would need 128 other turns committed to the same conversation inside that span. Documented as the bound.

### 1.6 `LoadAsync(conversationId, upToSequence)`

```
for attempt in 1..3:
   m = GetManifest(key)
   if m is null:
       if upToSequence is null or 0: return Snapshot(0, [])
       throw ConversationNotFoundException            // never silently start from zero
   upTo = upToSequence ?? m.next_seq
   if upTo > m.next_seq: throw ConversationNotFoundException   // deleted and recreated
   needed = m.chunks.Where(c => c.first_seq < upTo)
   docs = GetBulkState(needed keys)
   if any doc missing: continue                        // concurrent fold deleted it; re-read manifest
   entries = flatten(docs).Where(e => e.seq < upTo)
   assert entries are contiguous 0..upTo-1, else throw ConversationStoreException (corruption)
   return Snapshot(upTo, entries)
throw ConversationStoreException("chunk missing after 3 attempts")
```

With a TTL, either the manifest expires (the whole conversation is gone) or the chunks outlive it (2× TTL plus the refresh). A partial history is therefore impossible. A run whose pinned prefix expires mid-run fails loudly.

### 1.7 `DeleteAsync`

Read the manifest. If it is absent, return `false`. Otherwise delete the manifest, then `DeleteBulkStateAsync` the chunks best-effort, and return `true`. A running run with a pinned prefix then fails on its next load, which is intended.

### 1.8 Registration

In `Hosting/DaprAgentsBuilderExtensions.cs`:

```csharp
/// <summary>Stores agent conversation history (including model reasoning) in a Dapr state store, so runs started
/// with a conversation id continue from earlier turns without carrying history in workflow payloads.</summary>
/// <param name="stateStoreName">Dapr state store component; must support ETags, and TTL if configured.</param>
/// <param name="configure">Optional tuning.</param>
public static IAgentsBuilder WithConversationStore(this IAgentsBuilder builder, string stateStoreName,
    Action<DaprConversationStoreOptions>? configure = null)
```

It runs the guard clauses, builds the options, sets `StateStoreName`, calls `Validate()`, and registers the options singleton. Then:
- `TryAddSingleton<IConversationStoreBackend → Dapr>`;
- `TryAddSingleton<IConversationStore, DaprConversationStore>()`. A host-registered store wins, the same convention as `IToolApprovalHandler`;
- `TryAddSingleton(TimeProvider.System)`.

The method registers no activities (§2.6).

---

## 2. Who loads, who appends, and when

### 2.1 Changes to workflow and activity DTOs

Every change below is an additive nullable `init` property. Payloads from instances already in flight deserialize the new fields as null under the Web defaults.

| Type | New members |
|---|---|
| `DaprAgentInvocation` (public) | `string? ConversationId` |
| `CallLlmInput` (internal) | `string? ConversationId`, `long? HistoryUpToSequence` |
| `CallLlmOutput` (internal) | `long? HistorySequence`, `List<WorkflowReasoningContent>? Reasoning` |
| `AgentRunResult` (public) | `string? ConversationId`, `long? ConversationSequence` (the `NextSequence` after commit) |
| `SessionWorkflowInput` (public) | `string? ConversationId` |
| new `Runtime/AppendConversationTurnActivity.cs` | `internal sealed record AppendConversationTurnInput(string ConversationId, string TurnId, long BaseSequence, string AgentName, string? ChatClientKey, List<WorkflowChatMessage> Messages)` → `ConversationAppendResult` |
| new `Runtime/DeleteConversationActivity.cs` | `string` → `bool` |

### 2.2 `AgentRunWorkflow` in store mode (`input.ConversationId is not null`)

```
store = input.ConversationId is not null
if store && input.PriorMessages is {Count:>0}: throw ArgumentException   // deterministic (from input)
turnId = store ? $"{context.InstanceId}:{context.NewGuid():N}" : null      // only called in store mode
messages = store ? [] : [..PriorMessages]; turnStart = messages.Count; messages.Add(user)
long? pinned = null
ResolveAgentContextActivity(...)                                            // unchanged
try:
  loop i < 20:
     out = CallLlmActivity(llmInput with { ConversationId, HistoryUpToSequence = pinned })
     if store: pinned ??= out.HistorySequence                               // first recorded output fixes the snapshot
     assistant message now carries Reasoning = out.Reasoning                // (both the tool-call and final variants)
     … unchanged tool loop …
  on final OR max-iterations:
     turnMessages = messages[turnStart..]                                    // exactly today's TurnMessages
     if store:
        r = CallActivityAsync<ConversationAppendResult>(nameof(AppendConversationTurnActivity),
              new AppendConversationTurnInput(id, turnId, pinned ?? 0, AgentName, ChatClientKey, turnMessages),
              new WorkflowTaskOptions(CommitRetryPolicy))
     NotifyAgentContextCompleteAsync(...)    // after the commit: providers only see committed turns
     return AgentRunResult { Response, TurnMessages = turnMessages, ConversationId, ConversationSequence = r?.NextSequence }
catch: NotifyAgentContextCompleteAsync(error); throw                        // NO commit
```

- `CommitRetryPolicy` is a `WorkflowRetryPolicy` with 5 attempts, a 1 s first interval, ×2 backoff and a 30 s maximum interval. The constructor shape is checked in §8.3. The append is idempotent, so retrying it is always safe.
- When `ConversationId` is null, the `NewGuid()` call, the append activity and the extra fields are all skipped. **The activity sequence is identical to today**, so replay of existing instances is unchanged. A golden test (§7) pins this.
- On max-iterations the run commits exactly `TurnMessages`, which end with tool results. That keeps today's semantics, which team_learning_agents relies on for "Stop now…" (`IdeaWorkflow.cs:95`, `MarketConditionsWorkflow.cs:83`).

### 2.3 Load in `CallLlmActivity`

`CallLlmActivity` gains an `IConversationStore? conversationStore = null` constructor parameter. When `input.ConversationId` is set:

1. If `conversationStore` is null, throw `InvalidOperationException("Agent run uses conversation '…' but no conversation store is registered. Call WithConversationStore(...) on the agents builder.")`.
2. Load `LoadAsync(id, input.HistoryUpToSequence)` and return `HistorySequence = snapshot.NextSequence`.
3. Build the messages in this order: system, then `AdditionalMessages`, then **stored history**, then the in-run `input.Messages`. That is today's order, with stored history in the place of `PriorMessages`.
4. Reasoning in stored history is kept only for entries where `entry.AgentName` equals `input.AgentName` (case-insensitive) **and** `entry.ChatClientKey == input.ChatClientKey`. Signed `ProtectedData` from another model or provider is rejected by the provider, and reasoning belongs to the agent that produced it. Other entries' text, tool calls and results are sent unchanged. In-run messages always keep their reasoning.

Every iteration reloads the pinned prefix. The cost is about the same as today's `PriorMessages` payload, but it is spent on state reads instead of workflow history. A chunk cache is deferred (§6).

### 2.4 Failure, retry and replay

| Event | Outcome | Why |
|---|---|---|
| `CallLlm` or `ExecuteTool` fails, or the run is cancelled or terminated | nothing committed | the commit only happens after the final or max-iterations point. No dangling tool call is possible. |
| `CallLlm` attempt retried before its output is recorded | it may pin a newer sequence | only the recorded output counts. Later iterations load `< pinned`, an identical prefix. |
| Crash or replay before the commit | the store is untouched | completed activities replay from history. |
| Crash after chunks are written, before the manifest commit | redelivery writes new chunks | the old ones are unreferenced. The deletion rule removes them when it can prove they cannot land; otherwise TTL does. |
| Commit landed, reply lost or sidecar retried | `AlreadyAppended` | the `TurnId` is in `turns`. |
| Commit landed, worker crashed before recording | redelivery → `AlreadyAppended` | same as above. |
| Replay after the commit was recorded | no store call | the output comes from history. |
| Commit exhausts its retries | the run fails | the turn is either fully stored or not at all. Very rarely it is stored even though the run reported failure (only when the final reply is lost on every attempt). A rerun by the caller then adds a second turn. Documented. |
| Purged instance id reused | new `NewGuid`, so a new turn | the turn id is not derived from the instance id alone. |
| Caller retries the turn (`RunAgentGuardedAsync`) | new child run, new turn id | the failed run stored nothing, so the user message is not duplicated. |
| Pinned prefix expired or deleted mid-run | `ConversationNotFoundException`, run fails | never silently starts from zero. |

### 2.5 Two runs on one conversation

Each turn is self-contained: user message, then assistant/tool pairs, then the final message or tool results. Commits are serialized by the ETag. So any interleaving yields well-formed history, ordered by commit. The later run did not see the earlier one, and `BaseMoved = true` records that. The activity logs a warning naming both sequences.

`SessionWorkflow` and sequential `await`s are always serialized. Strict rejection (`Reject` mode) would waste a paid LLM run, so it is deferred. If added later, it should come back as a status rather than an exception, so the retry policy does not repeat it.

### 2.6 Replay fragility fixed

`AppendConversationTurnActivity` and `DeleteConversationActivity` live in the assembly, so they are always registered (§0). They resolve `IConversationStore` optionally and fail with the same `WithConversationStore` message. A replay in a misconfigured process therefore gets a clear error instead of "activity not registered".

---

## 3. Public API and backward compatibility

### 3.1 Inside workflows (`Runtime/WorkflowContextExtensions.cs`)

```csharp
/// <summary>Runs an agent as a child workflow against a stored conversation: the agent sees every turn previously
/// committed to <paramref name="conversationId"/> (including its own reasoning) and, if the run succeeds, this turn is
/// appended atomically. A failed run appends nothing, so retrying with the same id is safe.</summary>
/// <param name="conversationId">Caller-chosen id. Inside a workflow derive it deterministically, e.g. from
/// <c>context.InstanceId</c> or <c>context.NewGuid()</c>, never <c>Guid.NewGuid()</c>.</param>
/// <remarks>Requires <c>WithConversationStore(...)</c>. Concurrent runs on one id are ordered by commit.</remarks>
public static Task<AgentRunResult> RunAgentInConversationAsync(this WorkflowContext context, IDaprAIAgent agent,
    string conversationId, string? message = null, AgentRunOptions? options = null,
    IReadOnlyDictionary<string, string?>? telemetryBaggage = null);

/// <summary>Deletes a stored conversation (as an activity). Returns false if it did not exist.</summary>
public static Task<bool> DeleteConversationAsync(this WorkflowContext context, string conversationId);
```

`RunAgentInConversationAsync` is a new name rather than an overload of `RunAgentAsync`. Two adjacent `string` parameters would compile with the arguments swapped, and adding parameters to 1.0.10 methods is a binary break. A detached overload is deferred, which also avoids the same string/string ambiguity.

### 3.2 Outside workflows

- `DaprSessionConstants.ConversationIdKey = "dapr.conversation.id"`.
- `DaprSessionExtensions.AttachConversation(this IDaprAgentInvoker invoker, string conversationId) → AgentSession`. It sets only the conversation key and creates no workflow.
- `DaprSessionExtensions.GetConversationId(this AgentSession? session) → string?`.
- `DaprAgentInvoker.RunStatelessAsync` sets `invocation.ConversationId = session.GetConversationId()`. `IDaprAgentInvoker` gets no new members.
- **Session workflows:**
  - `DaprSessionOptions { uint? MaxTurns; bool UseConversationStore; }`, plus a new overload `CreateSessionAsync(this IDaprAgentInvoker, DaprWorkflowClient, DaprSessionOptions options, CancellationToken cancellationToken = default)`. The old signature stays unchanged.
  - With `UseConversationStore`, the overload sets `SessionWorkflowInput.ConversationId = instanceId`.
  - In `SessionWorkflow`, when `input.ConversationId` is set, the workflow keeps no `conversationLog` and sets `invocation.ConversationId` instead of `PriorMessages`. Its state then stays constant, so a later `ContinueAsNew` can lift `MaxTurns`.

### 3.3 Guarantees

- **Nothing changes without opt-in.** `RunAgentAsync`, `RunAgentWithHistoryAsync`, `PriorMessages`, the existing `CreateSessionAsync`, and instances in flight keep their behavior and their activity sequence. Registering a store changes no existing call.
- **The only visible changes on legacy paths:**
  - PR 1: `TurnMessages` and `AgentResponse` carry reasoning when the client emits it.
  - PR 2: the agent's own `ChatOptions` are applied.
  Both go in the release notes.

### 3.4 Migrating team_learning_agents

- **Until PR 4:** PR 1 already resends reasoning across turns through `TurnMessages` → `RunAgentWithHistoryAsync`. No code change is needed beyond the Ollama client (below).
- **After PR 4:**
  - Register `.WithConversationStore("statestore", o => o.TimeToLive = TimeSpan.FromDays(14))`.
  - Change `RunAgentGuardedAsync(context, agentName, message, string conversationId)` to call `RunAgentInConversationAsync`. The retry loop stays as it is.
  - `AskAsync`: `var conv = $"{context.InstanceId}/ask/{agentName}/{context.NewGuid():N}"`. The JSON-repair retry reuses `conv` instead of `history.AddRange(turn.TurnMessages)`.
  - `IdeaWorkflow` / `MarketConditionsWorkflow`: the main turn and the "Stop now…" follow-up share one id.
  - Delete with `DeleteConversationAsync` at the end of the workflow, or rely on the TTL.
- **The direct Ollama `IChatClient` must handle both directions:**
  - read `message.reasoning` into `TextReasoningContent`;
  - write the `TextReasoningContent` of assistant history back as the outgoing `reasoning` field;
  - send `reasoning_effort` from `ChatOptions.Reasoning`.
  Without the outgoing half, a resend reaches nothing.

---

## 4. Reasoning

### 4.1 Representation (`Runtime/WorkflowChatMessage.cs`)

```csharp
/// <summary>A block of model reasoning. <see cref="ProtectedData"/> carries provider-signed or encrypted reasoning
/// that must be sent back verbatim; <see cref="Text"/> may be empty when only protected data exists.</summary>
public sealed record WorkflowReasoningContent
{
    /// <summary>Human-readable reasoning text, or <see langword="null"/>.</summary>
    public string? Text { get; init; }
    /// <summary>Opaque provider data (e.g. Anthropic thinking signature), or <see langword="null"/>.</summary>
    public string? ProtectedData { get; init; }
}
// WorkflowChatMessage:
/// <summary>Reasoning produced with this assistant message, in order, or <see langword="null"/> if none.</summary>
public List<WorkflowReasoningContent>? Reasoning { get; init; }
```

### 4.2 Capture (`CallLlmActivity.ParseResponse`)

- If `response.Messages` is empty, throw `InvalidOperationException("LLM returned no messages")`.
- Collect `TextReasoningContent` from **all** messages, in order. Keep a block if it has non-empty text or non-null `ProtectedData`.
- Text and function calls keep coming from `[^1]`, as today.
- The result is returned in `CallLlmOutput.Reasoning`.

### 4.3 Resend (`WorkflowChatMessageConverter`)

- `ToChatMessage(message, bool includeReasoning = true)` emits `new TextReasoningContent(r.Text ?? "") { ProtectedData = r.ProtectedData }` **first**, then text, then function calls. Anthropic requires thinking blocks before `tool_use`.
- `FromChatMessage` maps reasoning back.
- `CompleteAgentContextActivity` and `ResolveAgentContextActivity` call it with `includeReasoning: false`. Providers never saw reasoning before, and this keeps their view unchanged.

### 4.4 Exposure

- In `AgentRunWorkflow`, the final `AgentResponse` message is `new ChatMessage(Assistant, [..reasoning blocks, new TextContent(text)])`.
- `AgentResponse.Text` is unchanged, because `ChatMessage.Text` concatenates only `TextContent`.
- `SessionTurnStatus` is unchanged.

### 4.5 Effort and the agent's `ChatOptions`

- **New file `Runtime/ReasoningRegistration.cs`,** following the `LlmTimeoutRegistration` pattern: `internal sealed record ReasoningRegistration(string? AgentName, ReasoningOptions Options)` and `static ReasoningOptions? Resolve(IEnumerable<ReasoningRegistration>, string agentName)`. Resolve uses the last named match (case-insensitive), else the last default, else null.
- **Builder methods:**
  ```csharp
  /// <summary>Sets the reasoning effort sent to the model for every agent (ChatOptions.Reasoning). Ignored by
  /// clients that do not support it, including Dapr Conversation (DaprChatClient).</summary>
  public static IAgentsBuilder WithReasoning(this IAgentsBuilder builder, ReasoningEffort effort, ReasoningOutput? output = null);
  /// <summary>Sets the reasoning effort for one agent; overrides the agent's own ChatOptions.Reasoning and the default.</summary>
  public static IAgentsBuilder WithReasoning(this IAgentsBuilder builder, string agentName, ReasoningEffort effort, ReasoningOutput? output = null);
  ```
  The setting lives only in DI and is never serialized, so the problem of the enum being written as a number does not arise.
- **Capture the agent's options.** `ChatClientRegistry.AgentChatConfig` gets `ChatOptions? BaseOptions`. `RegisterAgentComponents` stores `GetAgentChatOptions(cca)?.Clone()` with `Tools`, `Instructions` and `ConversationId` set to null:
  - `Tools` are merged separately;
  - `Instructions` are already sent as the system message;
  - a server-side `ConversationId` would send the conversation twice.
  The explicit `(instructions, tools)` registrations get `BaseOptions = null`.
- **`BuildChatOptions(config, additionalTools, runOptions, reasoningRegistrations)`:**
  1. Start from `o = config.BaseOptions?.Clone() ?? new()`. This keeps Temperature, TopP, MaxOutputTokens, ToolMode, ModelId, StopSequences and the rest.
  2. Set `o.Reasoning` with this precedence, highest first:
     - `WithReasoning(agentName)`;
     - the agent's own `ChatOptions.Reasoning`;
     - `WithReasoning(default)`.
  3. Apply run options only when non-null: `ResponseFormat`, `ContinuationToken` and `AllowBackgroundResponses`. This fixes the agent's `ResponseFormat` being overwritten with null today.
  4. Merge `AdditionalProperties`; the run's values win per key.
  5. Set `Tools = CombineTools(...)`.
  6. Return null when the result is equivalent to an empty `new ChatOptions()`, which preserves today's null.
- **Warning.** Log once per agent when the resolved `Reasoning` is non-null and `config.ChatClient.GetService<DaprChatClient>() is not null`.
- **Per-run effort is deferred** (`ChatClientAgentRunOptions.ChatOptions` is lost at the workflow boundary). It would need a string DTO on `DaprAgentInvocation`.

---

## 5. File map

| File | PR |
|---|---|
| `Runtime/WorkflowChatMessage.cs`, `WorkflowChatMessageConverter.cs`, `CallLlmOutput.cs`, `CallLlmActivity.cs` (`ParseResponse`), `AgentRunWorkflow.cs` (reasoning on assistant messages, `AgentResponse` contents), `CompleteAgentContextActivity.cs`, `ResolveAgentContextActivity.cs` (`includeReasoning: false`) | 1 |
| `Runtime/ReasoningRegistration.cs` (new), `ChatClientRegistry.cs`, `CallLlmActivity.cs` (`BuildChatOptions`), `Hosting/DaprAgentsBuilderExtensions.cs` (`WithReasoning`, `BaseOptions` capture) | 2 |
| `Conversations/**` (new), `Hosting/DaprAgentsBuilderExtensions.cs` (`WithConversationStore`) | 3 |
| `Runtime/DaprAgentInvocation.cs`, `CallLlmInput.cs`, `CallLlmOutput.cs`, `AgentRunResult.cs`, `AgentRunWorkflow.cs`, `CallLlmActivity.cs` (load), `AppendConversationTurnActivity.cs`, `DeleteConversationActivity.cs` (new), `WorkflowContextExtensions.cs`, `Hosting/DaprSessionExtensions.cs` (`AttachConversation`, `GetConversationId`), `DaprAgentInvoker.cs`, `Abstractions/DaprSessionConstants.cs` | 4 |
| `Runtime/SessionWorkflow.cs`, `SessionWorkflowInput.cs`, `Hosting/DaprSessionOptions.cs` (new), `DaprSessionExtensions.cs` (overload) | 5 |
| `README.md` **and** `src/…/README.md` (hand-synced), `docs/roadmap.md`, `AGENTS.md` (one paragraph: store key layout, commits only via `AppendConversationTurnActivity`, the deletion rule) | each API PR |

## 6. PR split, smallest useful first

1. **`feat: capture model reasoning and resend it`** (§4.1–4.4).
   - Fixes Anthropic thinking and tool loops within a run.
   - **team_learning_agents keeps reasoning across turns immediately** through `TurnMessages` / `RunAgentWithHistoryAsync`, once its Ollama client maps reasoning both ways.
   - Its client can set effort itself until PR 2.
2. **`feat: apply agent ChatOptions and add WithReasoning`** (§4.5). A behavior change for agents that set Temperature and similar options; needs a release note.
3. **`feat: Dapr conversation store`** (§1). The store alone, not wired into any workflow. It must settle §8.1 and §8.2 before PR 4.
4. **`feat: run agents against a stored conversation`** (§2, §3.1, `AttachConversation`). Contains the golden replay test.
5. **`feat: session workflows backed by the conversation store`** (§3.2 sessions).
6. **Later, each measured or demand-driven:**
   - an in-process LRU cache of immutable chunks keyed by chunk key;
   - an `IChatReducer` / max-turns window applied after load. The context window otherwise grows without bound. It must cut at turn boundaries and never mutate the store;
   - `Reject` conflict mode;
   - per-run effort;
   - a conversation-aware detached overload;
   - `ContinueAsNew` in `SessionWorkflow`;
   - `Usage` / `FinishReason` / `ModelId` on `CallLlmOutput` and `AgentResponse`.
7. **team_learning_agents** (its own repo): the Ollama client after PR 1, and the conversation-id migration after PR 4.

Every PR must:
- add the BSL header to new files;
- give every public member XML docs;
- keep `LangVersion 12` (no `field` keyword, no `params` collections);
- build locally with `-f net8.0` and `-f net9.0`, because CI only runs `net10.0`.

## 7. Tests per PR

**PR 1** (unit tests in `test/…Test/Runtime/`):
- `ParseResponse`:
  - reasoning across several messages;
  - `ProtectedData`-only blocks kept;
  - empty `Messages` gives a clear error.
- Converter:
  - reasoning comes first, before text and calls;
  - round-trip;
  - `includeReasoning: false` strips it.
- `AgentRunWorkflowTests`:
  - the assistant tool-call and final messages carry reasoning;
  - `AgentResponse.Text` is unchanged and its contents start with reasoning.
- Serialization:
  - `WorkflowChatMessage` with and without `Reasoning` under Web defaults;
  - old JSON deserializes;
  - `AgentRunResult` holding a `TextReasoningContent` round-trips through the workflow serializer (§8.4).

Integration test: a mock `IChatClient` emits reasoning with a tool call. The next iteration's request contains it first. Run 2 through `RunAgentWithHistoryAsync` receives run 1's reasoning.

**PR 2:**
- Unit tests:
  - `ReasoningRegistration.Resolve`;
  - the `BuildChatOptions` precedence matrix (named > agent > default);
  - `Instructions`, `ConversationId` and `Tools` cleared on the clone;
  - the shared `BaseOptions` is not mutated;
  - `ResponseFormat` is not overwritten with null;
  - `AdditionalProperties` merge;
  - still returns null when empty;
  - the `DaprChatClient` warning is logged once.
- Integration test: `ChatOptionsRecorder` sees `Reasoning.Effort` and `Temperature`.

**PR 3:**
- Unit tests over an in-memory backend with real ETag semantics, a fake `TimeProvider` and fault hooks. They port the Python suite:
  - order;
  - chunking, and an oversized entry getting its own chunk;
  - tail fold, and a full tail is kept;
  - TTL metadata (`ttl` on the manifest, `2·ttl` on chunks) and refresh once older than `ttl`;
  - ETag conflict with retry;
  - giving up after max attempts;
  - `||` escaping;
  - delete, and delete of a missing key;
  - load retries a vanished chunk, then gives up.
- New unit tests:
  - turn-id idempotency returns the original range;
  - **lost reply**: the save lands then throws, giving `AlreadyAppended` with the chunks intact;
  - **late landing**: the save throws, the manifest is unchanged, a later attempt commits, the late save is rejected, and nothing referenced was deleted;
  - **deletion rule**: no delete while the ETag is unchanged; delete once it moves and the chunks are unreferenced;
  - fold racing a pinned load;
  - pinned load filters by `seq` and fetches only the chunks it needs;
  - contiguity check;
  - `ConversationNotFoundException` for an expired, deleted or recreated conversation;
  - dedupe window eviction;
  - `BaseMoved`;
  - `Validate()`;
  - JSON shape (snake_case; a `JsonElement` passes through Web defaults byte-identical).
- Integration tests (`ConversationStoreRedisTests`, real daprd 1.19 + Redis, a test-specific `KeyPrefix`):
  - **two concurrent first appends: exactly one manifest wins and both turns end up stored** (settles §8.1);
  - an ETag loser gets `false`;
  - `ttlInSeconds` is accepted;
  - how `GetBulkStateAsync` reports a missing key (settles §8.2);
  - a short-TTL expiry.

**PR 4:**
- Unit tests:
  - **golden test:** with `ConversationId == null`, the activity names and order (and no `NewGuid` call) match today's for final, tool-loop, max-iterations and throw paths;
  - store mode:
    - pins the sequence from the first output;
    - commits on final and on max-iterations, with exactly `TurnMessages`;
    - no commit on a throw;
    - the commit comes before `CompleteAgentContext`;
    - `PriorMessages` together with `ConversationId` throws;
    - turn id = `InstanceId:NewGuid`;
  - `CallLlmActivity`:
    - order system → providers → history → run;
    - same-agent/same-key reasoning filter;
    - missing store gives a clear error.
- Integration tests:
  - two runs on one id: the second request contains run 1's user message, reasoning, tool call, result and answer;
  - an LLM failure on iteration 2 commits nothing, and a retry adds exactly one turn;
  - the append activity run twice gives `AlreadyAppended`;
  - concurrent runs give two complete turns, one with `BaseMoved`;
  - `AttachConversation` through the invoker;
  - `DeleteConversationAsync`.

**PR 5:**
- Unit test: `SessionWorkflow` in store mode keeps no log and passes `ConversationId`.
- Integration test: a 3-turn session matches the legacy output, and the session's state and custom status stay constant in size.

## 8. Open questions (each closed by a named test or a decision)

1. **Create-if-absent for the first manifest.** Does `TrySaveStateAsync` accept a null or empty ETag, and does Dapr Redis with `FirstWrite` and no ETag insert only if the key is absent? If it does not, implement `CreateIfAbsent` as `ExecuteStateTransactionAsync` with a single upsert carrying `FirstWrite` and no ETag, mapping the conflict error to `false`. The PR 3 concurrent-first-append test decides; PR 4 must not merge before it.
2. **How `GetBulkStateAsync` represents a missing key** (default value vs an empty element). Missing-chunk detection depends on it (PR 3 integration test).
3. **The `WorkflowRetryPolicy` / `WorkflowTaskOptions` constructor shape in Dapr.Workflow 1.19,** and whether new activities are auto-registered from the assembly (PR 4).
4. **Whether `TextReasoningContent` inside `AgentRunResult.Response` survives `AgentJsonTypeInfoResolver` and the workflow serializer** (the roadmap says yes; the PR 1 test pins it).
5. **Whether glm-5.3 actually uses resent `reasoning`** in request history, and which field name it reads. This needs a live fixture before claiming "continues from earlier reasoning" for that model.
6. **ETag reuse after delete and recreate** while an append is in flight. Redis versions restart, so the deletion rule's assumption breaks. It is accepted and documented as "do not delete a conversation that is being written".
7. **Bounding loaded history against the context window** (reducer or turn window) is deferred. Until then a long conversation eventually exceeds the model's context. Is a `MaxHistoryTurns` option needed in PR 4?
8. **Whether `ProtectedData` should be stored at all** (roadmap decision 5). The design stores it, and the same-agent/same-client-key filter limits where it is resent.