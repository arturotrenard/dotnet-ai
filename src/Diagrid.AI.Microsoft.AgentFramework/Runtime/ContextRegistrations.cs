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
using Diagrid.AI.Microsoft.AgentFramework.Conversations;
using Microsoft.Extensions.AI;

namespace Diagrid.AI.Microsoft.AgentFramework.Runtime;

/// <summary>
/// The context window of an agent's model, registered by <c>WithContextWindow(...)</c>. A <see langword="null"/>
/// <see cref="AgentName"/> sets the default for every agent; a named registration overrides it for that agent.
/// </summary>
/// <param name="AgentName">The agent it applies to, or <see langword="null"/> for all agents.</param>
/// <param name="MaxContextTokens">The model's context window, in tokens.</param>
/// <param name="MaxOutputTokens">The tokens kept free for the model's reply.</param>
internal sealed record ContextWindowRegistration(string? AgentName, int MaxContextTokens, int MaxOutputTokens)
{
    /// <summary>
    /// The registration for <paramref name="agentName"/>: the last one naming it, else the last default, else none.
    /// </summary>
    internal static ContextWindowRegistration? Resolve(IEnumerable<ContextWindowRegistration> registrations, string agentName) =>
        ResolveNamed(registrations, r => r.AgentName, agentName);

    /// <summary>
    /// Throws unless the window is positive and leaves room for input after the reserved output.
    /// </summary>
    internal static void Validate(int maxContextTokens, int maxOutputTokens)
    {
        if (maxContextTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContextTokens), maxContextTokens, "The context window must be positive.");
        }

        if (maxOutputTokens < 0 || maxOutputTokens >= maxContextTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), maxOutputTokens,
                "The reserved output must be zero or more, and smaller than the context window.");
        }
    }

    internal static T? ResolveNamed<T>(IEnumerable<T> registrations, Func<T, string?> nameOf, string agentName)
        where T : class
    {
        T? fallback = null;
        T? named = null;
        foreach (var registration in registrations)
        {
            var name = nameOf(registration);
            if (name is null)
            {
                fallback = registration;
            }
            else if (string.Equals(name, agentName, StringComparison.OrdinalIgnoreCase))
            {
                named = registration;
            }
        }

        return named ?? fallback;
    }
}

/// <summary>
/// Conversation compaction for an agent, registered by <c>WithConversationCompaction(...)</c>. A
/// <see langword="null"/> <see cref="AgentName"/> sets the default for every agent.
/// </summary>
/// <param name="AgentName">The agent it applies to, or <see langword="null"/> for all agents.</param>
/// <param name="Options">The compaction settings.</param>
internal sealed record ConversationCompactionRegistration(string? AgentName, ConversationCompactionOptions Options)
{
    /// <summary>
    /// The registration for <paramref name="agentName"/>: the last one naming it, else the last default, else none.
    /// </summary>
    internal static ConversationCompactionRegistration? Resolve(
        IEnumerable<ConversationCompactionRegistration> registrations, string agentName) =>
        ContextWindowRegistration.ResolveNamed(registrations, r => r.AgentName, agentName);
}

/// <summary>
/// A rough token count for history: characters / 4, over every text the model would read.
/// </summary>
internal static class TokenEstimate
{
    internal static long Of(IEnumerable<string?> texts) => texts.Sum(t => (long)(t?.Length ?? 0)) / 4;

    /// <summary>The estimate for chat messages as they are sent: text, reasoning, tool calls and tool results.</summary>
    internal static long Of(IEnumerable<ChatMessage> messages)
    {
        long chars = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                chars += content switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    TextReasoningContent reasoning => (long)(reasoning.Text?.Length ?? 0) + (reasoning.ProtectedData?.Length ?? 0),
                    FunctionCallContent call => call.Name.Length
                        + (call.Arguments is { Count: > 0 } args ? JsonSerializer.Serialize(args).Length : 0),
                    FunctionResultContent result => result.Result switch
                    {
                        null => 0,
                        string s => s.Length,
                        JsonElement e => e.GetRawText().Length,
                        var other => JsonSerializer.Serialize(other).Length,
                    },
                    _ => 0,
                };
            }
        }

        return chars / 4;
    }

    internal static long Of(IEnumerable<WorkflowChatMessage> messages)
    {
        long chars = 0;
        foreach (var message in messages)
        {
            chars += message.Content?.Length ?? 0;
            if (message.Reasoning is { } reasoning)
            {
                chars += reasoning.Sum(r => (long)(r.Text?.Length ?? 0) + (r.ProtectedData?.Length ?? 0));
            }

            if (message.FunctionCalls is { } calls)
            {
                chars += calls.Sum(c => (long)c.Name.Length + c.ArgumentsJson.Length);
            }

            if (message.FunctionResults is { } results)
            {
                chars += results.Sum(r => (long)(r.ResultJson?.Length ?? 0));
            }
        }

        return chars / 4;
    }
}
