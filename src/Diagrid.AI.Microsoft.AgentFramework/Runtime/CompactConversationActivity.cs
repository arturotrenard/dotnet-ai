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

using System.Text;
using Dapr.Workflow;
using Diagrid.AI.Microsoft.AgentFramework.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Diagrid.AI.Microsoft.AgentFramework.Runtime;

/// <summary>
/// Input for <see cref="CompactConversationActivity"/>.
/// </summary>
/// <param name="ConversationId">The stored conversation to compact.</param>
/// <param name="SummaryId">The summary's idempotency key, derived from the run's turn id.</param>
/// <param name="AgentName">The agent whose compaction settings apply.</param>
/// <param name="ChatClientKey">The agent's chat client key, or <see langword="null"/>.</param>
/// <param name="Incarnation">The conversation incarnation the run used, so a recreated one is left alone.</param>
internal sealed record CompactConversationInput(
    string ConversationId,
    string SummaryId,
    string AgentName,
    string? ChatClientKey,
    string? Incarnation);

/// <summary>
/// Output of <see cref="CompactConversationActivity"/>.
/// </summary>
internal sealed record CompactConversationOutput
{
    /// <summary>Whether a summary was stored.</summary>
    public bool Compacted { get; init; }

    /// <summary>The sequence below which the stored summary replaces the conversation, when one was stored.</summary>
    public long? CoversUpToSequence { get; init; }

    /// <summary>Why compaction failed, when it did. Compaction is best effort, so the run still succeeds.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Summarizes the older turns of a stored conversation and appends the summary to it
/// (<c>WithConversationCompaction</c>). It runs after a run's commit, only when the run's first LLM call found the
/// history past the trigger, and never fails the run: errors are logged and returned.
/// </summary>
/// <remarks>
/// The summary call is made once and its result is stored, so later runs, retries and replays reuse it. The turns
/// it replaces stay stored, and loads pinned before the summary are unchanged.
/// </remarks>
internal sealed partial class CompactConversationActivity(
    ChatClientRegistry chatClientRegistry,
    AgentRegistry agentRegistry,
    IServiceProvider serviceProvider,
    ILogger<CompactConversationActivity> logger,
    IEnumerable<ConversationCompactionRegistration>? compactions = null,
    IEnumerable<LlmTimeoutRegistration>? llmTimeouts = null,
    IConversationStore? conversationStore = null) : WorkflowActivity<CompactConversationInput, CompactConversationOutput>
{
    /// <summary>The text that introduces a stored summary to the model.</summary>
    internal const string SummaryHeader = "Summary of the conversation before this point:";

    /// <inheritdoc />
    public override async Task<CompactConversationOutput> RunAsync(WorkflowActivityContext context, CompactConversationInput input)
    {
        try
        {
            return await CompactAsync(input).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCompactionFailed(ex, input.ConversationId, input.AgentName);
            return new CompactConversationOutput { Error = ex.Message };
        }
    }

    private async Task<CompactConversationOutput> CompactAsync(CompactConversationInput input)
    {
        if (ConversationCompactionRegistration.Resolve(compactions ?? [], input.AgentName) is not { } registration)
        {
            return new CompactConversationOutput();
        }

        var options = registration.Options;
        var store = ConversationStoreGuard.Require(conversationStore, input.ConversationId);
        var snapshot = await store.LoadAsync(input.ConversationId, incarnation: input.Incarnation).ConfigureAwait(false);
        if (TokenEstimate.Of(snapshot.Entries.Select(e => e.Message)) <= options.TriggerTokens)
        {
            return new CompactConversationOutput();
        }

        var previous = snapshot.Entries.FirstOrDefault(e => e.SummaryOf is not null);
        var turns = SplitTurns(snapshot.Entries.Where(e => e.SummaryOf is null));
        var plan = PlanCompaction(turns, previous, options);
        if (plan is null)
        {
            return new CompactConversationOutput();
        }

        var (older, coversUpTo) = plan.Value;
        var text = await SummarizeAsync(input, options, previous, older).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new CompactConversationOutput { Error = "The summary came back empty." };
        }

        await store.AppendSummaryAsync(new ConversationSummary(
            input.ConversationId,
            input.SummaryId,
            coversUpTo,
            input.AgentName,
            input.ChatClientKey,
            new WorkflowChatMessage { Role = "system", Content = $"{SummaryHeader}\n{text.Trim()}" },
            snapshot.Incarnation)).ConfigureAwait(false);

        LogCompacted(input.ConversationId, older.Count, coversUpTo);
        return new CompactConversationOutput { Compacted = true, CoversUpToSequence = coversUpTo };
    }

    /// <summary>
    /// Picks what to summarize: the oldest turns that fit <see cref="ConversationCompactionOptions.MaxSummaryInputTokens"/>
    /// together with the earlier summary (at least one turn), leaving the most recent turns: at most
    /// <see cref="ConversationCompactionOptions.KeepRecentTurns"/>, within half of the trigger, and always the latest.
    /// Returns <see langword="null"/> when nothing can be summarized.
    /// </summary>
    internal static (List<ConversationEntry> Older, long CoversUpTo)? PlanCompaction(
        List<List<ConversationEntry>> turns, ConversationEntry? previous, ConversationCompactionOptions options)
    {
        static long Tokens(IEnumerable<ConversationEntry> entries) => TokenEstimate.Of(entries.Select(e => e.Message));

        var keepBudget = options.TriggerTokens / 2;
        var kept = 1;
        long keptTokens = Tokens(turns.Count > 0 ? turns[^1] : []);
        while (kept < options.KeepRecentTurns && kept < turns.Count)
        {
            var candidate = Tokens(turns[turns.Count - 1 - kept]);
            if (keptTokens + candidate > keepBudget)
            {
                break;
            }

            keptTokens += candidate;
            kept++;
        }

        var summarizable = turns.Count - kept;
        if (summarizable <= 0)
        {
            return null;
        }

        var inputBudget = options.MaxSummaryInputTokens ?? options.TriggerTokens;
        var used = previous is null ? 0 : TokenEstimate.Of([previous.Message]);
        var take = 0;
        while (take < summarizable)
        {
            var size = Tokens(turns[take]);
            if (take > 0 && used + size > inputBudget)
            {
                break;
            }

            used += size;
            take++;
        }

        var older = turns.Take(take).SelectMany(t => t).ToList();
        return (older, turns[take][0].Sequence);
    }

    /// <summary>
    /// Groups entries into turns: consecutive entries committed by the same turn.
    /// </summary>
    internal static List<List<ConversationEntry>> SplitTurns(IEnumerable<ConversationEntry> entries)
    {
        var turns = new List<List<ConversationEntry>>();
        foreach (var entry in entries)
        {
            if (turns.Count == 0 || turns[^1][0].TurnId != entry.TurnId)
            {
                turns.Add([]);
            }

            turns[^1].Add(entry);
        }

        return turns;
    }

    private async Task<string?> SummarizeAsync(
        CompactConversationInput input,
        ConversationCompactionOptions options,
        ConversationEntry? previous,
        List<ConversationEntry> older)
    {
        // The agent's own client gets the agent's options (its model id, for instance), minus anything that
        // would turn the summary into a tool call or a structured reply.
        ChatOptions? chatOptions = null;
        IChatClient client;
        if (options.ChatClient is { } custom)
        {
            client = custom;
        }
        else
        {
            var config = ResolveAgentConfig(input);
            client = config.ChatClient;
            if (config.BaseOptions?.Clone() is { } agentOptions)
            {
                agentOptions.Tools = null;
                agentOptions.ToolMode = null;
                agentOptions.ResponseFormat = null;
                chatOptions = agentOptions;
            }
        }

        // The turns go as one transcript, not as chat messages: replaying tool calls without their tool
        // definitions is rejected by some providers, and reasoning blocks cannot be sent to another model.
        var transcript = new StringBuilder();
        if (previous is not null)
        {
            transcript.AppendLine("Earlier summary:").AppendLine(previous.Message.Content).AppendLine();
        }

        transcript.AppendLine("Conversation to summarize:");
        foreach (var entry in older)
        {
            AppendTranscript(transcript, entry.Message);
        }

        // A single turn larger than the budget is cut, so the summary call always fits.
        var maxChars = (long)(options.MaxSummaryInputTokens ?? options.TriggerTokens) * 4;
        if (transcript.Length > maxChars)
        {
            transcript.Length = (int)maxChars;
            transcript.AppendLine().Append("[truncated]");
        }

        var timeout = LlmTimeoutRegistration.Resolve(llmTimeouts ?? [], input.AgentName);
        using var timeoutCts = new CancellationTokenSource();
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, options.SummarizationPrompt), new ChatMessage(ChatRole.User, transcript.ToString())],
            chatOptions,
            timeoutCts.Token).ConfigureAwait(false);

        return response.Text;
    }

    internal static void AppendTranscript(StringBuilder transcript, WorkflowChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.Content))
        {
            transcript.Append(message.Role).Append(": ").AppendLine(message.Content);
        }

        foreach (var call in message.FunctionCalls ?? [])
        {
            transcript.Append(message.Role).Append(" called ").Append(call.Name).Append(' ').AppendLine(call.ArgumentsJson);
        }

        foreach (var result in message.FunctionResults ?? [])
        {
            transcript.Append("tool result: ").AppendLine(result.ResultJson ?? "(none)");
        }
    }

    private ChatClientRegistry.AgentChatConfig ResolveAgentConfig(CompactConversationInput input)
    {
        var config = chatClientRegistry.Get(input.AgentName);
        if (config is null)
        {
            agentRegistry.Get(input.AgentName, input.ChatClientKey, serviceProvider);
            config = chatClientRegistry.Get(input.AgentName)
                ?? throw new InvalidOperationException(
                    $"Agent '{input.AgentName}' has no chat client to summarize with. Set ConversationCompactionOptions.ChatClient.");
        }

        return config;
    }

    [LoggerMessage(LogLevel.Information, "Compacted conversation '{ConversationId}': {Count} messages before entry {CoversUpTo} are now summarized")]
    private partial void LogCompacted(string conversationId, int count, long coversUpTo);

    [LoggerMessage(LogLevel.Warning, "Could not compact conversation '{ConversationId}' for agent '{AgentName}'; the run is unaffected and the next run tries again")]
    private partial void LogCompactionFailed(Exception exception, string conversationId, string agentName);
}
