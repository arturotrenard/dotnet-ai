// Copyright (c) 2026-present Diagrid Inc
//
// Licensed under the Business Source License 1.1 (BSL 1.1).
// You may not use this file except in compliance with the License.
//
// The full license terms, including the Additional Use Grant,
// are available in the LICENSE.md file at the root of this repository.
//
// Change Date: March 1, 2030
// On the Change Date, this software will be available under
// the Apache License, Version 2.0.

using System.Text.Json;
using Diagrid.AI.Microsoft.AgentFramework.Conversations.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diagrid.AI.Microsoft.AgentFramework.Conversations;

/// <summary>
/// <see cref="IConversationStore"/> kept in a Dapr state store. Port of dapr-agents <c>DaprSessionStore</c>
/// (<c>dapr_agents/agents/executors/dapr_session_store.py</c>).
/// </summary>
/// <remarks>
/// <para>
/// A conversation is a manifest at <c>{KeyPrefix}{conversationId}</c> plus immutable chunks of entries at
/// <c>{manifestKey}#{chunkId}</c>. An append writes new chunks first and then commits a new manifest,
/// conditioned on the ETag it read, so a turn becomes visible all at once or not at all. A small last
/// chunk is folded into the next append's chunk, so chunks stay near <see cref="DaprConversationStoreOptions.MaxChunkBytes"/>.
/// </para>
/// <para>
/// Unlike the Python store, an append whose commit outcome is unknown (a lost reply, a sidecar retry) never
/// deletes the chunks it wrote until that commit provably cannot land: the manifest exists and no longer
/// has the ETag the commit was conditioned on. An absent manifest proves nothing, because Redis accepts a
/// save conditioned on any ETag when the key does not exist. Deleting chunks earlier could leave a committed
/// manifest pointing at missing chunks, which would make the conversation impossible to load. Chunks that
/// cannot be proven safe to delete are left to the TTL.
/// </para>
/// <para>
/// For the same reason <see cref="DeleteAsync"/> never removes the manifest key: it replaces the manifest
/// with a tombstone, conditioned on the ETag it read, and only then deletes the chunks. A commit conditioned
/// on an older ETag can no longer land on it. Each conversation also gets a random incarnation id when it is
/// created, so a load pinned to an earlier incarnation fails instead of returning a newer conversation's
/// history under the same id.
/// </para>
/// </remarks>
internal sealed partial class DaprConversationStore : IConversationStore
{
    private const int MaxLoadAttempts = 3;

    // Bytes a chunk document adds around its entries: {"entries":[...]}.
    private const int ChunkEnvelopeBytes = 14;

    private readonly IConversationStateBackend _backend;
    private readonly DaprConversationStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public DaprConversationStore(
        IConversationStateBackend backend,
        DaprConversationStoreOptions options,
        TimeProvider timeProvider,
        ILogger<DaprConversationStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _backend = backend;
        _options = options;
        _timeProvider = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Delay = (delay, ct) => Task.Delay(delay, _timeProvider, ct);
    }

    /// <summary>
    /// Waits between commit attempts. Replaced by tests, so retries do not wait.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; }

    private int? ChunkTtlSeconds => _options.TimeToLiveSeconds * 2;

    /// <summary>The <c>ttlInSeconds</c> value that removes an expiry a key was written with.</summary>
    private const int PersistTtl = -1;

    /// <inheritdoc />
    public async Task<ConversationSnapshot> LoadAsync(
        string conversationId,
        long? upToSequence = null,
        string? incarnation = null,
        CancellationToken cancellationToken = default)
    {
        var key = ManifestKey(conversationId);
        if (upToSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(upToSequence), upToSequence, "The sequence cannot be negative.");
        }

        for (var attempt = 1; attempt <= MaxLoadAttempts; attempt++)
        {
            var (manifest, _) = await ReadManifestAsync(key, cancellationToken).ConfigureAwait(false);
            if (manifest is not { Deleted: false } live)
            {
                if (upToSequence is null or 0)
                {
                    return new ConversationSnapshot(0, []);
                }

                throw new ConversationNotFoundException(conversationId,
                    $"Conversation '{conversationId}' does not exist, so its first {upToSequence} entries cannot be loaded. It was deleted or expired.");
            }

            if (incarnation is not null && upToSequence is not 0
                && !string.Equals(incarnation, live.Incarnation, StringComparison.Ordinal))
            {
                throw new ConversationNotFoundException(conversationId,
                    $"Conversation '{conversationId}' was deleted or expired and created again since its history was first read.");
            }

            var upTo = upToSequence ?? live.NextSequence;
            if (upTo > live.NextSequence)
            {
                throw new ConversationNotFoundException(conversationId,
                    $"Conversation '{conversationId}' has {live.NextSequence} entries, fewer than the {upTo} requested. It was deleted and created again.");
            }

            if (upTo == 0)
            {
                return new ConversationSnapshot(0, [], live.Incarnation);
            }

            var needed = live.Chunks.Where(c => c.FirstSequence < upTo).ToList();
            var keys = needed.Select(c => ChunkKey(key, c.Id)).ToList();
            var documents = await _backend.GetBulkAsync(keys, cancellationToken).ConfigureAwait(false);
            if (keys.Any(k => !documents.ContainsKey(k)))
            {
                // A concurrent append folded the last chunk and deleted it after this manifest was read.
                LogChunkMissingOnLoad(conversationId, attempt);
                continue;
            }

            var entries = new List<ConversationEntry>((int)Math.Min(upTo, int.MaxValue));
            foreach (var chunkKey in keys)
            {
                foreach (var stored in ReadChunk(documents[chunkKey], chunkKey).Entries)
                {
                    if (stored.Sequence < upTo)
                    {
                        entries.Add(ToEntry(stored));
                    }
                }
            }

            entries.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Sequence != i)
                {
                    throw new ConversationStoreException(
                        $"Conversation '{conversationId}' is corrupt: expected entry {i}, found {entries[i].Sequence}.");
                }
            }

            if (entries.Count != upTo)
            {
                throw new ConversationStoreException(
                    $"Conversation '{conversationId}' is corrupt: expected {upTo} entries, found {entries.Count}.");
            }

            return new ConversationSnapshot(upTo, entries, live.Incarnation);
        }

        throw new ConversationStoreException(
            $"Could not load conversation '{conversationId}': a chunk it references was still missing after {MaxLoadAttempts} attempts.");
    }

    /// <inheritdoc />
    public async Task<ConversationAppendResult> AppendTurnAsync(
        ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var key = ManifestKey(turn.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.TurnId, nameof(turn));
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.AgentName, nameof(turn));
        if (turn.Messages is not { Count: > 0 })
        {
            throw new ArgumentException("A turn needs at least one message.", nameof(turn));
        }

        if (turn.Messages.Any(m => m is null))
        {
            throw new ArgumentException("A turn's messages cannot be null.", nameof(turn));
        }

        if (turn.BaseSequence < 0)
        {
            throw new ArgumentException("A turn's base sequence cannot be negative.", nameof(turn));
        }

        var pending = new List<PendingCommit>();
        Exception? lastError = null;

        for (var attempt = 1; attempt <= _options.MaxCommitAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await Delay(Backoff(attempt - 1), cancellationToken).ConfigureAwait(false);
            }

            var (read, etag) = await ReadManifestAsync(key, cancellationToken).ConfigureAwait(false);
            await SettleAsync(key, pending, read, p => CanStillLand(p, read, etag), cancellationToken).ConfigureAwait(false);

            // A tombstone is an empty conversation, replaced by a commit conditioned on its ETag.
            var manifest = IsLive(read) ? read : null;

            // Checked before anything else: a commit whose reply was lost has already landed.
            var stored = manifest?.Turns.FindLast(t => t.Id == turn.TurnId);
            if (stored is not null)
            {
                return new ConversationAppendResult(ConversationAppendStatus.AlreadyAppended,
                    stored.FirstSequence, manifest!.NextSequence, stored.BaseMoved);
            }

            // A turn produced against stored history needs that history to still exist: otherwise the turn
            // would start a new conversation that depends on messages nobody can load any more.
            if (turn.BaseIncarnation is not null
                && !string.Equals(turn.BaseIncarnation, manifest?.Incarnation, StringComparison.Ordinal))
            {
                throw new ConversationNotFoundException(turn.ConversationId,
                    $"Conversation '{turn.ConversationId}' was deleted or expired while turn '{turn.TurnId}' was produced from its history, so the turn is not stored.");
            }

            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var next = manifest?.NextSequence ?? 0;
            var incarnation = manifest?.Incarnation ?? Guid.NewGuid().ToString("N");
            var baseMoved = next != turn.BaseSequence;
            var entries = turn.Messages.Select((message, i) => new StoredConversationEntry
            {
                Sequence = next + i,
                TurnId = turn.TurnId,
                AgentName = turn.AgentName,
                ChatClientKey = turn.ChatClientKey,
                CreatedMs = now,
                Message = StoredChatMessage.From(message),
            }).ToList();

            // Plan: keep the full chunks, and fold a small last chunk into this append's chunks.
            var kept = manifest?.Chunks.ToList() ?? [];
            string? superseded = null;
            var toWrite = entries;
            if (kept.Count > 0 && kept[^1].Bytes < _options.MaxChunkBytes / 2)
            {
                var tail = kept[^1];
                var tailKey = ChunkKey(key, tail.Id);
                var tailDocuments = await _backend.GetBulkAsync([tailKey], cancellationToken).ConfigureAwait(false);
                if (!tailDocuments.TryGetValue(tailKey, out var tailDocument))
                {
                    // Another append folded and deleted it after this manifest was read, so the manifest moved.
                    LogCommitConflict(turn.ConversationId, attempt);
                    continue;
                }

                superseded = tail.Id;
                kept.RemoveAt(kept.Count - 1);
                toWrite = [.. ReadChunk(tailDocument, tailKey).Entries, .. entries];
            }

            // TTLs: the manifest expires TimeToLive after the last append and chunks get twice that, so they
            // outlive it. Kept chunks are written again once half their margin is used (leaving room for clock
            // skew between writers), or when the configured TTL differs from the one they were written with.
            // Turning the TTL off removes the expiry once, with PersistTtl; otherwise no TTL is sent at all, so
            // stores without TTL support keep working.
            var chunkTtl = ChunkTtlSeconds;
            var removeExpiry = chunkTtl is null && manifest?.ChunkTtlSeconds is not null;
            var chunkWriteTtl = chunkTtl ?? (removeExpiry ? PersistTtl : null);
            var manifestWriteTtl = _options.TimeToLiveSeconds ?? (removeExpiry ? PersistTtl : null);
            var chunksWrittenMs = kept.Count > 0 ? manifest!.ChunksWrittenMs : now;
            var refresh = kept.Count > 0
                && (manifest!.ChunkTtlSeconds != chunkTtl
                    || (_options.TimeToLive is { } ttl && now - chunksWrittenMs >= (long)ttl.TotalMilliseconds / 2));
            if (refresh)
            {
                if (!await RefreshChunksAsync(key, kept, chunkWriteTtl, cancellationToken).ConfigureAwait(false))
                {
                    LogCommitConflict(turn.ConversationId, attempt);
                    continue;
                }

                chunksWrittenMs = now;
            }

            var chunks = BuildChunks(toWrite);
            var commit = new PendingCommit(chunks.Select(c => c.Descriptor.Id).ToList(), etag, superseded);
            await WriteChunksAsync(key, chunks, chunkWriteTtl, cancellationToken).ConfigureAwait(false);
            pending.Add(commit);

            var updated = new ConversationManifest
            {
                NextSequence = next + entries.Count,
                Chunks = [.. kept, .. chunks.Select(c => c.Descriptor)],
                Turns = TrimTurns(manifest?.Turns, new TurnRecord
                {
                    Id = turn.TurnId,
                    FirstSequence = next,
                    Count = entries.Count,
                    BaseSequence = turn.BaseSequence,
                    BaseMoved = baseMoved,
                }),
                ModifiedMs = Math.Max(now, (read?.ModifiedMs ?? 0) + 1),
                ChunksWrittenMs = chunksWrittenMs,
                ChunkTtlSeconds = chunkTtl,
                Incarnation = incarnation,
            };

            var document = JsonSerializer.SerializeToElement(updated, ConversationStoreJsonContext.Default.ConversationManifest);
            bool? committed;
            try
            {
                committed = etag is null
                    ? await _backend.TryCreateAsync(key, document, manifestWriteTtl, cancellationToken).ConfigureAwait(false)
                    : await _backend.TrySaveAsync(key, document, etag, manifestWriteTtl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The write may or may not have landed. Nothing is deleted here; the next read settles it.
                lastError = ex;
                committed = null;
                LogCommitOutcomeUnknown(ex, turn.ConversationId, attempt);
            }

            if (committed == true)
            {
                pending.Remove(commit);

                // This commit moved the ETag, so no earlier attempt of this call can land any more.
                await SettleAsync(key, pending, updated, _ => false, cancellationToken).ConfigureAwait(false);
                if (superseded is not null)
                {
                    await TryDeleteAsync([ChunkKey(key, superseded)], cancellationToken).ConfigureAwait(false);
                }

                return new ConversationAppendResult(ConversationAppendStatus.Appended,
                    next, updated.NextSequence, baseMoved);
            }

            if (committed == false)
            {
                LogCommitConflict(turn.ConversationId, attempt);
            }
        }

        throw new ConversationStoreException(
            $"Could not commit turn '{turn.TurnId}' to conversation '{turn.ConversationId}' after {_options.MaxCommitAttempts} attempts.",
            lastError);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var key = ManifestKey(conversationId);
        Exception? lastError = null;
        ConversationManifest? tombstoned = null;

        for (var attempt = 1; attempt <= _options.MaxCommitAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await Delay(Backoff(attempt - 1), cancellationToken).ConfigureAwait(false);
            }

            var (manifest, etag) = await ReadManifestAsync(key, cancellationToken).ConfigureAwait(false);
            if (!IsLive(manifest))
            {
                // Either there was nothing to delete, or an earlier attempt's tombstone landed without a reply.
                if (tombstoned is null || manifest is null || manifest.Incarnation != tombstoned.Incarnation)
                {
                    return false;
                }

                await TryDeleteAsync(tombstoned.Chunks.Select(c => ChunkKey(key, c.Id)).ToList(), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            // The key is never removed: a commit conditioned on an older ETag could land on an absent key.
            var tombstone = new ConversationManifest
            {
                Deleted = true,
                Incarnation = manifest!.Incarnation,
                ModifiedMs = Math.Max(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), manifest.ModifiedMs + 1),
            };
            var document = JsonSerializer.SerializeToElement(tombstone, ConversationStoreJsonContext.Default.ConversationManifest);
            tombstoned = manifest;
            bool committed;
            try
            {
                committed = await _backend.TrySaveAsync(key, document, etag!, _options.TimeToLiveSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                continue;
            }

            if (committed)
            {
                // No commit can reference these chunks any more: every one is conditioned on an older ETag.
                await TryDeleteAsync(manifest.Chunks.Select(c => ChunkKey(key, c.Id)).ToList(), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        throw new ConversationStoreException(
            $"Could not delete conversation '{conversationId}' after {_options.MaxCommitAttempts} attempts.", lastError);
    }

    internal string ManifestKey(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (conversationId.Length > DaprConversationStoreOptions.MaxConversationIdLength)
        {
            throw new ArgumentException(
                $"A conversation id can have at most {DaprConversationStoreOptions.MaxConversationIdLength} characters.",
                nameof(conversationId));
        }

        return _options.KeyPrefix + EscapeKey(conversationId);
    }

    /// <summary>
    /// Percent-escapes the characters with a meaning in keys: <c>|</c> (Dapr separates the component's key
    /// prefix with <c>||</c>), <c>#</c> (separates a chunk id) and <c>%</c> itself, so that two conversation
    /// ids never share a key and an id never reaches another conversation's chunks.
    /// </summary>
    private static string EscapeKey(string conversationId)
    {
        if (conversationId.AsSpan().IndexOfAny("%|#") < 0)
        {
            return conversationId;
        }

        return conversationId
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("#", "%23", StringComparison.Ordinal);
    }

    private static bool IsLive(ConversationManifest? manifest) => manifest is { Deleted: false };

    internal static string ChunkKey(string manifestKey, string chunkId) => $"{manifestKey}#{chunkId}";

    private async Task<(ConversationManifest? Manifest, string? ETag)> ReadManifestAsync(
        string key, CancellationToken cancellationToken)
    {
        var (value, etag) = await _backend.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return (null, null);
        }

        ConversationManifest? manifest;
        try
        {
            manifest = value.Value.Deserialize(ConversationStoreJsonContext.Default.ConversationManifest);
        }
        catch (JsonException ex)
        {
            throw new ConversationStoreException($"The conversation manifest at '{key}' is not valid.", ex);
        }

        if (manifest is null)
        {
            throw new ConversationStoreException($"The conversation manifest at '{key}' is not valid.");
        }

        if (manifest.Version > ConversationManifest.CurrentVersion)
        {
            throw new ConversationStoreException(
                $"The conversation at '{key}' was written by a newer version of the store (format {manifest.Version}).");
        }

        if (string.IsNullOrEmpty(etag))
        {
            throw new ConversationStoreException(
                $"State store '{_options.StateStoreName}' returned no ETag for '{key}'. The conversation store needs a state store that supports ETags.");
        }

        return (manifest, etag);
    }

    private static ConversationChunk ReadChunk(JsonElement document, string key)
    {
        try
        {
            return document.Deserialize(ConversationStoreJsonContext.Default.ConversationChunk)
                ?? throw new ConversationStoreException($"The conversation chunk at '{key}' is not valid.");
        }
        catch (JsonException ex)
        {
            throw new ConversationStoreException($"The conversation chunk at '{key}' is not valid.", ex);
        }
    }

    private static ConversationEntry ToEntry(StoredConversationEntry stored) => new(
        stored.Sequence,
        stored.TurnId,
        stored.AgentName,
        stored.ChatClientKey,
        DateTimeOffset.FromUnixTimeMilliseconds(stored.CreatedMs),
        stored.Message.ToWorkflowChatMessage());

    /// <summary>
    /// Splits entries into chunks greedily by serialized size. An entry larger than the limit gets a chunk of its own.
    /// </summary>
    private List<(ChunkDescriptor Descriptor, JsonElement Document)> BuildChunks(List<StoredConversationEntry> entries)
    {
        var chunks = new List<(ChunkDescriptor, JsonElement)>();
        var current = new List<StoredConversationEntry>();
        var currentBytes = ChunkEnvelopeBytes;

        foreach (var entry in entries)
        {
            var entryBytes = JsonSerializer.SerializeToUtf8Bytes(entry, ConversationStoreJsonContext.Default.StoredConversationEntry).Length;
            var separator = current.Count > 0 ? 1 : 0;
            if (current.Count > 0 && currentBytes + separator + entryBytes > _options.MaxChunkBytes)
            {
                chunks.Add(CreateChunk(current));
                current = [];
                currentBytes = ChunkEnvelopeBytes;
                separator = 0;
            }

            current.Add(entry);
            currentBytes += separator + entryBytes;
        }

        if (current.Count > 0)
        {
            chunks.Add(CreateChunk(current));
        }

        return chunks;
    }

    private static (ChunkDescriptor, JsonElement) CreateChunk(List<StoredConversationEntry> entries)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ConversationChunk { Entries = entries },
            ConversationStoreJsonContext.Default.ConversationChunk);
        using var parsed = JsonDocument.Parse(bytes);
        var descriptor = new ChunkDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            FirstSequence = entries[0].Sequence,
            Count = entries.Count,
            Bytes = bytes.Length,
        };
        return (descriptor, parsed.RootElement.Clone());
    }

    private async Task WriteChunksAsync(
        string key,
        List<(ChunkDescriptor Descriptor, JsonElement Document)> chunks,
        int? ttlSeconds,
        CancellationToken cancellationToken)
    {
        var written = new List<string>(chunks.Count);
        try
        {
            foreach (var (descriptor, document) in chunks)
            {
                var chunkKey = ChunkKey(key, descriptor.Id);
                written.Add(chunkKey);
                await _backend.SaveAsync(chunkKey, document, ttlSeconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // No manifest commit referencing these chunks was attempted, so they can be deleted right away.
            await TryDeleteAsync(written, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Rewrites the kept chunks unchanged with a fresh TTL, so they outlive the manifest. Returns false when one
    /// of them is gone, which means a concurrent append has moved the manifest.
    /// </summary>
    private async Task<bool> RefreshChunksAsync(
        string key, List<ChunkDescriptor> kept, int? ttlSeconds, CancellationToken cancellationToken)
    {
        var keys = kept.Select(c => ChunkKey(key, c.Id)).ToList();
        var documents = await _backend.GetBulkAsync(keys, cancellationToken).ConfigureAwait(false);
        if (keys.Any(k => !documents.ContainsKey(k)))
        {
            return false;
        }

        foreach (var chunkKey in keys)
        {
            await _backend.SaveAsync(chunkKey, documents[chunkKey], ttlSeconds, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private List<TurnRecord> TrimTurns(List<TurnRecord>? turns, TurnRecord added)
    {
        var all = new List<TurnRecord>((turns?.Count ?? 0) + 1);
        if (turns is not null)
        {
            all.AddRange(turns);
        }

        all.Add(added);
        return all.Count > _options.TurnDedupeWindow ? all.GetRange(all.Count - _options.TurnDedupeWindow, _options.TurnDedupeWindow) : all;
    }

    /// <summary>
    /// Whether an earlier commit attempt of this call could still land. A create can land while the key is
    /// absent. A save conditioned on an ETag can land while the store still holds that ETag, and also while the
    /// key is absent, because Redis accepts a conditioned save on a missing key. A tombstone is a manifest
    /// like any other here: it exists, with its own ETag.
    /// </summary>
    private static bool CanStillLand(PendingCommit commit, ConversationManifest? manifest, string? etag) =>
        manifest is null
        || (commit.ConditionETag is not null && string.Equals(commit.ConditionETag, etag, StringComparison.Ordinal));

    /// <summary>
    /// The deletion rule: an attempt's chunks are deleted only once its commit can no longer land and the
    /// current manifest does not reference them. Settled attempts are removed from <paramref name="pending"/>.
    /// </summary>
    private async Task SettleAsync(
        string manifestKey,
        List<PendingCommit> pending,
        ConversationManifest? manifest,
        Func<PendingCommit, bool> canStillLand,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var referenced = manifest?.Chunks.Select(c => c.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        var toDelete = new List<string>();
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var commit = pending[i];
            if (canStillLand(commit))
            {
                continue;
            }

            pending.RemoveAt(i);
            toDelete.AddRange(commit.ChunkIds.Where(id => !referenced.Contains(id)));

            // A folded tail is never added back by any commit, so it can go once no manifest references it.
            if (commit.SupersededId is { } superseded && !referenced.Contains(superseded))
            {
                toDelete.Add(superseded);
            }
        }

        await TryDeleteAsync(toDelete.Select(id => ChunkKey(manifestKey, id)).ToList(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryDeleteAsync(List<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            await _backend.DeleteBulkAsync(keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreferenced chunks are harmless; the TTL removes them when one is set.
            LogDeleteFailed(ex, keys.Count);
        }
    }

    private static TimeSpan Backoff(int failedAttempts)
    {
        var baseMs = Math.Min(50 * failedAttempts, 500);
        return TimeSpan.FromMilliseconds(baseMs * (1 + (Random.Shared.NextDouble() * 0.25)));
    }

    /// <summary>
    /// A commit this call attempted: the chunks it wrote, the ETag it was conditioned on (null for a first
    /// write), and the last chunk it folded.
    /// </summary>
    private sealed record PendingCommit(List<string> ChunkIds, string? ConditionETag, string? SupersededId);

    [LoggerMessage(LogLevel.Debug, "Append to conversation '{ConversationId}' lost a race on attempt {Attempt}; retrying.")]
    private partial void LogCommitConflict(string conversationId, int attempt);

    [LoggerMessage(LogLevel.Warning, "Append to conversation '{ConversationId}' failed on attempt {Attempt} and may or may not have been stored; checking before retrying.")]
    private partial void LogCommitOutcomeUnknown(Exception exception, string conversationId, int attempt);

    [LoggerMessage(LogLevel.Debug, "A chunk of conversation '{ConversationId}' was replaced while loading it (attempt {Attempt}); reading it again.")]
    private partial void LogChunkMissingOnLoad(string conversationId, int attempt);

    [LoggerMessage(LogLevel.Warning, "Could not delete {Count} unreferenced conversation chunk(s); they are left for the state store TTL.")]
    private partial void LogDeleteFailed(Exception exception, int count);
}
