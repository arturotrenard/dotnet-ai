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
using Dapr.Workflow;
using Diagrid.AI.Microsoft.AgentFramework.Abstractions;
using Diagrid.AI.Microsoft.AgentFramework.Conversations;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Diagrid.AI.Microsoft.AgentFramework.Runtime;

/// <summary>
/// Activity that performs a single LLM call. Each invocation is checkpointed by Dapr Workflows,
/// so on crash recovery the result is replayed without re-executing the LLM call.
/// </summary>
/// <remarks>
/// In addition to the agent's statically-registered instructions/tools (from <see cref="ChatClientRegistry"/>),
/// this activity layers in the per-run contribution computed once by <see cref="ResolveAgentContextActivity"/>
/// (<see cref="CallLlmInput.AdditionalInstructions"/>, <see cref="CallLlmInput.AdditionalMessages"/>,
/// <see cref="CallLlmInput.AdditionalToolNames"/>) so that context providers — e.g. an MAF
/// <c>AgentSkillsProvider</c> — stay in effect for every LLM call within a single agent run.
/// When the run continues a stored conversation (<see cref="CallLlmInput.ConversationId"/>), its history is
/// loaded from the <see cref="IConversationStore"/> on every call, pinned to the prefix the run's first call saw.
/// </remarks>
internal sealed partial class CallLlmActivity(
    ChatClientRegistry chatClientRegistry,
    ToolRegistry toolRegistry,
    AgentRegistry agentRegistry,
    IServiceProvider serviceProvider,
    ILogger<CallLlmActivity> logger,
    IEnumerable<LlmTimeoutRegistration>? llmTimeouts = null,
    IEnumerable<ReasoningEffortRegistration>? reasoningEfforts = null,
    IEnumerable<ReasoningReplayRegistration>? reasoningReplays = null,
    IConversationStore? conversationStore = null,
    IEnumerable<ContextWindowRegistration>? contextWindows = null,
    IEnumerable<ConversationCompactionRegistration>? compactions = null) : WorkflowActivity<CallLlmInput, CallLlmOutput>
{

    /// <inheritdoc />
    public override async Task<CallLlmOutput> RunAsync(WorkflowActivityContext context, CallLlmInput input)
    {
        AgentTelemetryBaggage.SetAgent(
            input.AgentName,
            input.ChatClientKey,
            AgentTelemetryBaggage.LlmOperation,
            input.TelemetryBaggage);

        var config = chatClientRegistry.Get(input.AgentName);
        if (config is null)
        {
            // Trigger lazy agent resolution — this runs the factory which
            // populates ChatClientRegistry as a side effect for ChatClientAgent types.
            agentRegistry.Get(input.AgentName, input.ChatClientKey, serviceProvider);
            config = chatClientRegistry.Get(input.AgentName)
                ?? throw new InvalidOperationException(
                    $"Agent '{input.AgentName}' did not register a chat client configuration. " +
                    "Ensure the agent is a ChatClientAgent created via AsAIAgent() or registered " +
                    "with explicit instructions and tools via the DaprAgentsBuilderExtensions overloads.");
        }

        // Stored history goes where PriorMessages would: before the run's own messages.
        var history = await LoadHistoryAsync(input).ConfigureAwait(false);
        var runMessages = input.Messages;
        var runMessagesStart = input.RunMessagesStart ?? 0;
        if (history is { Entries.Count: > 0 })
        {
            runMessages = [.. history.Entries.Select(e => e.Message), .. input.Messages];
            runMessagesStart += history.Entries.Count;
        }

        LogLlmCallInfo(input.AgentName, runMessages.Count);

        var timeout = LlmTimeoutRegistration.Resolve(llmTimeouts ?? [], input.AgentName);
        using var timeoutCts = new CancellationTokenSource();
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutCts.CancelAfter(timeout);
        }

        try
        {
            var reasoningSource = WorkflowChatMessageConverter.GetReasoningSource(input.AgentName, input.ChatClientKey);
            var replay = ReasoningReplayRegistration.Resolve(reasoningReplays ?? [], input.AgentName);
            var messages = BuildChatMessages(config.Instructions, input.AdditionalInstructions, input.AdditionalMessages,
                runMessages, reasoningSource, replay, runMessagesStart);
            messages = await FitContextWindowAsync(input.AgentName, messages, runMessages, runMessagesStart, timeoutCts.Token)
                .ConfigureAwait(false);
            var additionalTools = ResolveAdditionalTools(input.AgentName, input.AdditionalToolNames);
            var reasoning = ReasoningEffortRegistration.Resolve(reasoningEfforts ?? [], input.AgentName, config.BaseOptions?.Reasoning);
            WarnIfEffortIgnored(input.AgentName, config.ChatClient, reasoning);
            var options = BuildChatOptions(config.BaseOptions, config.Tools, additionalTools, input.Options, reasoning,
                isFollowUpCall: input.Iteration > 0);

            var response = await config.ChatClient.GetResponseAsync(messages, options, timeoutCts.Token)
                .ConfigureAwait(false);

            return ParseResponse(response, reasoningSource) with
            {
                ReasoningReplay = replay,
                HistorySequence = history?.NextSequence,
                HistoryIncarnation = history?.Incarnation,
                CompactionDue = IsCompactionDue(input.AgentName, history) ? true : null
            };
        }
        // A cancelled gRPC call surfaces as RpcException(Cancelled), not OperationCanceledException,
        // so the token's own state is what tells a timeout apart from any other failure.
        catch (Exception ex) when (timeoutCts.IsCancellationRequested)
        {
            LogLlmCallTimeout(input.AgentName, timeout);
            throw new TimeoutException(
                $"The LLM call for agent '{input.AgentName}' did not complete within {timeout}. " +
                "Raise it with WithLlmTimeout(...) on the agents builder.", ex);
        }
        catch (Exception ex)
        {
            LogLlmCallError(input.AgentName, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Loads the run's stored conversation history, or returns <see langword="null"/> when the run has none.
    /// Reasoning in it is sent back only to the agent and chat client that produced it, through each block's
    /// source, as for <see cref="CallLlmInput.Messages"/>.
    /// </summary>
    private async Task<ConversationSnapshot?> LoadHistoryAsync(CallLlmInput input)
    {
        if (input.ConversationId is null)
        {
            return null;
        }

        var store = ConversationStoreGuard.Require(conversationStore, input.ConversationId);
        return await store.LoadAsync(input.ConversationId, input.HistoryUpToSequence, input.HistoryIncarnation)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Trims the messages to the agent's context window (<c>WithContextWindow</c>), or returns them unchanged when
    /// it has none. Only what is sent changes; nothing stored does.
    /// </summary>
    /// <remarks>
    /// Only the part before the run's own messages (instructions, context provider messages, earlier turns) is
    /// trimmed, against the budget the run leaves. The run's messages are never touched: dropping its user
    /// message loses the task, and collapsing its tool calls strips the reasoning a provider such as Anthropic
    /// requires next to each tool result. A run that alone exceeds the window is sent as it is.
    /// </remarks>
    private async Task<List<ChatMessage>> FitContextWindowAsync(
        string agentName,
        List<ChatMessage> messages,
        List<WorkflowChatMessage> runMessages,
        int runMessagesStart,
        CancellationToken cancellationToken)
    {
        if (ContextWindowRegistration.Resolve(contextWindows ?? [], agentName) is not { } window)
        {
            return messages;
        }

        // BuildChatMessages appends one chat message per run message, last.
        var runCount = Math.Max(0, runMessages.Count - runMessagesStart);
        var prefix = messages.Take(messages.Count - runCount).ToList();
        var run = messages.Skip(messages.Count - runCount);

        var runTokens = TokenEstimate.Of(runMessages.Skip(runMessagesStart));
        var prefixBudget = (int)Math.Max(window.MaxOutputTokens + 1L, window.MaxContextTokens - runTokens);

        // MAF's compaction API is experimental (MAAI001).
#pragma warning disable MAAI001
        var strategy = new ContextWindowCompactionStrategy(prefixBudget, window.MaxOutputTokens);
        var fittedPrefix = (await CompactionProvider.CompactAsync(strategy, prefix, logger, cancellationToken)
            .ConfigureAwait(false)).ToList();
#pragma warning restore MAAI001

        if (fittedPrefix.Count == prefix.Count)
        {
            return messages;
        }

        LogContextWindowApplied(agentName, messages.Count, fittedPrefix.Count + runCount);
        return [.. fittedPrefix, .. run];
    }

    /// <summary>
    /// Whether the run's stored history has grown past the agent's compaction trigger. Recorded in the output, so
    /// the workflow schedules the compaction the same way on every replay.
    /// </summary>
    private bool IsCompactionDue(string agentName, ConversationSnapshot? history) =>
        history is { Entries.Count: > 0 }
        && ConversationCompactionRegistration.Resolve(compactions ?? [], agentName) is { } compaction
        && TokenEstimate.Of(history.Entries.Select(e => e.Message)) > compaction.Options.TriggerTokens;

    private static List<ChatMessage> BuildChatMessages(
        string? instructions,
        string? additionalInstructions,
        List<WorkflowChatMessage>? additionalMessages,
        List<WorkflowChatMessage> messages,
        string reasoningSource,
        ReasoningReplay replay,
        int runMessagesStart)
    {
        var chatMessages = new List<ChatMessage>();

        var combinedInstructions = CombineInstructions(instructions, additionalInstructions);
        if (!string.IsNullOrWhiteSpace(combinedInstructions))
        {
            chatMessages.Add(new ChatMessage(ChatRole.System, combinedInstructions));
        }

        // Ephemeral context contributed by AIContextProviders for this run only (e.g. retrieved
        // memories). Never persisted to TurnMessages/session history — see ResolveAgentContextActivity.
        if (additionalMessages is { Count: > 0 })
        {
            foreach (var msg in additionalMessages)
            {
                chatMessages.Add(WorkflowChatMessageConverter.ToChatMessage(msg, reasoningSource));
            }
        }

        // Reasoning this agent produced earlier goes back to the model: from prior turns too with
        // AcrossTurns, from this run only with WithinRun, and never with Off.
        for (var i = 0; i < messages.Count; i++)
        {
            var sendReasoning = replay switch
            {
                ReasoningReplay.Off => false,
                ReasoningReplay.WithinRun => i >= runMessagesStart,
                _ => true
            };
            chatMessages.Add(WorkflowChatMessageConverter.ToChatMessage(messages[i], sendReasoning ? reasoningSource : null));
        }

        return chatMessages;
    }

    private static string? CombineInstructions(string? instructions, string? additionalInstructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return additionalInstructions;
        }

        if (string.IsNullOrWhiteSpace(additionalInstructions))
        {
            return instructions;
        }

        return $"{instructions}\n\n{additionalInstructions}";
    }

    /// <summary>
    /// Resolves tool names contributed by <see cref="ResolveAgentContextActivity"/> (e.g. a skills
    /// provider's <c>load_skill</c>/<c>read_skill_resource</c>/<c>run_skill_script</c>) back into
    /// <see cref="AITool"/> instances via <see cref="ToolRegistry"/>, where they were registered.
    /// </summary>
    private IReadOnlyList<AITool>? ResolveAdditionalTools(string agentName, List<string>? toolNames)
    {
        if (toolNames is not { Count: > 0 })
        {
            return null;
        }

        List<AITool>? resolved = null;
        foreach (var name in toolNames)
        {
            var fn = toolRegistry.Get(agentName, name);
            if (fn is null)
            {
                continue;
            }

            (resolved ??= []).Add(fn);
        }

        return resolved;
    }

    /// <summary>
    /// Builds the options for one LLM call: the agent's own <see cref="ChatOptions"/> (temperature,
    /// output limits, ...), the resolved reasoning effort, the run's options where it sets them, and
    /// the agent's tools plus those its context providers contributed.
    /// </summary>
    private static ChatOptions? BuildChatOptions(
        ChatOptions? baseOptions,
        IList<AITool>? tools,
        IReadOnlyList<AITool>? additionalTools,
        AgentRunOptions? agentRunOptions,
        ReasoningOptions? reasoning,
        bool isFollowUpCall)
    {
        var combinedTools = CombineTools(tools, additionalTools);
        if (baseOptions is null && combinedTools is null && agentRunOptions is null && reasoning is null)
        {
            return null;
        }

        // Cloned per call: the registered options are shared by every call of the agent.
        var options = baseOptions?.Clone() ?? new ChatOptions();
        options.Reasoning = reasoning;
        options.Tools = combinedTools;

        if (isFollowUpCall && options.ToolMode is RequiredChatToolMode)
        {
            options.ToolMode = null;
        }

        if (agentRunOptions is not null)
        {
#pragma warning disable MEAI001
            // The run overrides only what it sets, so an agent's own ResponseFormat is not erased.
            options.AllowBackgroundResponses = agentRunOptions.AllowBackgroundResponses ?? options.AllowBackgroundResponses;
            options.ContinuationToken = agentRunOptions.ContinuationToken ?? options.ContinuationToken;
#pragma warning restore MEAI001
            options.ResponseFormat = agentRunOptions.ResponseFormat ?? options.ResponseFormat;
            options.AdditionalProperties = MergeAdditionalProperties(options.AdditionalProperties, agentRunOptions.AdditionalProperties);
        }

        return options;
    }

    private static AdditionalPropertiesDictionary? MergeAdditionalProperties(
        AdditionalPropertiesDictionary? agentProperties,
        AdditionalPropertiesDictionary? runProperties)
    {
        if (agentProperties is not { Count: > 0 })
        {
            return runProperties;
        }

        if (runProperties is not { Count: > 0 })
        {
            return agentProperties;
        }

        var merged = new AdditionalPropertiesDictionary(agentProperties);
        foreach (var (key, value) in runProperties)
        {
            merged[key] = value;
        }

        return merged;
    }

    /// <summary>
    /// Warns, once per agent, when the agent asks for a reasoning effort its chat client cannot send:
    /// Dapr Conversation (<c>DaprChatClient</c>) has no field for it today.
    /// </summary>
    private void WarnIfEffortIgnored(string agentName, IChatClient chatClient, ReasoningOptions? reasoning)
    {
        if (reasoning?.Effort is null
            || !IsDaprChatClient(chatClient)
            || !chatClientRegistry.WarnedIgnoredReasoningEffort.TryAdd(agentName, true))
        {
            return;
        }

        LogReasoningEffortIgnored(agentName);
    }

    /// <summary>Whether <paramref name="chatClient"/> is, or wraps at any depth, a <c>DaprChatClient</c>.</summary>
    private static bool IsDaprChatClient(IChatClient chatClient)
    {
        var seen = new HashSet<IChatClient>(ReferenceEqualityComparer.Instance);
        for (IChatClient? client = chatClient; client is not null && seen.Add(client);)
        {
            if (client.GetType().Name == "DaprChatClient")
            {
                return true;
            }

            client = client switch
            {
                ToolResultCompatibilityChatClient wrapper => wrapper.InnerClient,
                DelegatingChatClient delegating => Hosting.DaprAgentsBuilder.GetDelegatingInnerClient(delegating),
                _ => null
            };
        }

        return false;
    }

    private static List<AITool>? CombineTools(IList<AITool>? tools, IReadOnlyList<AITool>? additionalTools)
    {
        if (tools is not { Count: > 0 } && additionalTools is not { Count: > 0 })
        {
            return null;
        }

        var combined = new List<AITool>(
            (tools?.Count ?? 0) + (additionalTools?.Count ?? 0));

        if (tools is { Count: > 0 })
        {
            combined.AddRange(tools);
        }

        if (additionalTools is { Count: > 0 })
        {
            combined.AddRange(additionalTools);
        }

        return combined;
    }

    private static CallLlmOutput ParseResponse(ChatResponse response, string reasoningSource)
    {
        if (response.Messages.Count == 0)
        {
            throw new InvalidOperationException("The LLM returned a response with no messages.");
        }

        var responseMessage = response.Messages[^1];
        var functionCalls = responseMessage.Contents.OfType<FunctionCallContent>().ToList();
        var text = responseMessage.Text;

        var isFinal = functionCalls.Count == 0;

        return new CallLlmOutput
        {
            IsFinal = isFinal,
            Text = text,
            // Some clients return reasoning as a message of its own, so read it from every message.
            Reasoning = WorkflowChatMessageConverter.ToWorkflowReasoning(
                response.Messages.SelectMany(m => m.Contents), reasoningSource),
            FunctionCalls = isFinal
                ? null
                : functionCalls.Select(fc => new WorkflowFunctionCall
                {
                    CallId = fc.CallId ?? Guid.NewGuid().ToString("N"),
                    Name = fc.Name,
                    ArgumentsJson = fc.Arguments is { Count: > 0 }
                        ? JsonSerializer.Serialize(fc.Arguments)
                        : "{}"
                }).ToList()
        };
    }

    [LoggerMessage(LogLevel.Information, "Calling LLM for agent '{AgentName}' with {MessageCount} messages")]
    private partial void LogLlmCallInfo(string agentName, int messageCount);

    [LoggerMessage(LogLevel.Error, "LLM call failed for agent '{AgentName}': {ErrorMessage}")]
    private partial void LogLlmCallError(string agentName, string errorMessage);

    [LoggerMessage(LogLevel.Warning, "Agent '{AgentName}' has a reasoning effort, but its chat client is DaprChatClient, which does not send it: the Dapr Conversation API has no field for it")]
    private partial void LogReasoningEffortIgnored(string agentName);

    [LoggerMessage(LogLevel.Information, "Fitted agent '{AgentName}' messages to its context window: {Before} messages became {After}")]
    private partial void LogContextWindowApplied(string agentName, int before, int after);

    [LoggerMessage(LogLevel.Error, "LLM call for agent '{AgentName}' timed out after {Timeout}")]
    private partial void LogLlmCallTimeout(string agentName, TimeSpan timeout);
}
