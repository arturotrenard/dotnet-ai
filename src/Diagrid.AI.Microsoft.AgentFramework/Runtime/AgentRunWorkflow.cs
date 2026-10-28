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

using Dapr.Workflow;
using Diagrid.AI.Microsoft.AgentFramework.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Diagrid.AI.Microsoft.AgentFramework.Runtime;

/// <summary>
/// Durable workflow that orchestrates an agent run. Each LLM call and each tool invocation
/// is scheduled as a separate Dapr Workflow activity so that on crash recovery, already-completed
/// activities are NOT replayed.
/// <list type="number">
///   <item>Resolve the agent's <c>AIContextProviders</c> once for this run (<see cref="ResolveAgentContextActivity"/>)</item>
///   <item>Call the LLM as an activity (<see cref="CallLlmActivity"/>)</item>
///   <item>If the LLM returns tool calls, execute each tool as a separate activity (<see cref="ExecuteToolActivity"/>)</item>
///   <item>Feed tool results back and loop until the LLM returns a final response</item>
///   <item>Notify the agent's <c>AIContextProviders</c> that the run has completed (<see cref="CompleteAgentContextActivity"/>)</item>
/// </list>
/// </summary>
public sealed class AgentRunWorkflow : Workflow<DaprAgentInvocation, AgentRunResult>
{
    private const int MaxIterations = 20;

    /// <inheritdoc />
    public override async Task<AgentRunResult> RunAsync(WorkflowContext context, DaprAgentInvocation input)
    {
        if (string.IsNullOrWhiteSpace(input.Message))
        {
            throw new ArgumentException(
                "Message cannot be null or whitespace.", nameof(input));
        }

        // Full message list sent to the LLM (includes prior turns for context)
        var messages = new List<WorkflowChatMessage>();
        if (input.PriorMessages is { Count: > 0 })
        {
            messages.AddRange(input.PriorMessages);
        }

        // Track where this turn's messages start
        var turnStartIndex = messages.Count;

        messages.Add(
            new WorkflowChatMessage { Role = "user", Content = input.Message }
        );

        // The messages that make up "the request" for this run (used by the Invoked phase of the
        // AIContextProvider pipeline for notification purposes). Captured once, before the loop
        // grows `messages` with assistant/tool turns.
        var turnRequestMessages = messages.Skip(turnStartIndex).ToList();

        // Resolve AIContextProviders (e.g. an MAF AgentSkillsProvider) once per run — analogous to
        // what ChatClientAgent.RunAsync does internally before delegating to FunctionInvokingChatClient.
        // A no-op (near-instant) activity call when the agent has no context providers configured.
        // Options/RequestMessages are passed through so providers can establish AIAgent.CurrentRunContext
        // (see AgentRunContextScope) and see this run's AgentRunOptions.AdditionalProperties.
        var agentContext = await context.CallActivityAsync<ResolveAgentContextOutput>(
            nameof(ResolveAgentContextActivity),
            new ResolveAgentContextInput(input.AgentName, input.ChatClientKey, input.TelemetryBaggage)
            {
                RequestMessages = turnRequestMessages,
                Options = input.Options
            });

        // The reasoning replay setting the last LLM call recorded; decides what TurnMessages keep.
        ReasoningReplay? lastReplay = null;

        try
        {
            for (var iteration = 0; iteration < MaxIterations; iteration++)
            {
                // Activity: Call the LLM once.
                var llmInput = new CallLlmInput(
                    input.AgentName,
                    input.ChatClientKey,
                    messages,
                    input.Options,
                    input.TelemetryBaggage)
                {
                    AdditionalInstructions = agentContext.Instructions,
                    AdditionalMessages = agentContext.Messages,
                    AdditionalToolNames = agentContext.ToolNames,
                    RunMessagesStart = turnStartIndex,
                    Iteration = iteration
                };
                var llmOutput = await context.CallActivityAsync<CallLlmOutput>(
                    nameof(CallLlmActivity), llmInput);

                // The first output tells the agent's replay setting. When earlier turns' reasoning is
                // never sent back, stop carrying it in the inputs of the run's later calls.
                if (iteration == 0 && llmOutput.ReasoningReplay is ReasoningReplay.WithinRun or ReasoningReplay.Off)
                {
                    StripReasoning(messages, 0, turnStartIndex);
                }

                if (llmOutput.Error is not null)
                {
                    throw new InvalidOperationException(
                        $"LLM call failed for agent '{input.AgentName}': {llmOutput.Error}");
                }

                // If this is a final response (no tool calls), we're done.
                if (llmOutput.IsFinal)
                {
                    // Add the final assistant message
                    messages.Add(new WorkflowChatMessage
                    {
                        Role = "assistant",
                        Content = llmOutput.Text,
                        Reasoning = KeptReasoning(llmOutput)
                    });

                    await NotifyAgentContextCompleteAsync(context, input, agentContext, messages, turnStartIndex, turnRequestMessages, errorMessage: null);

                    return new AgentRunResult
                    {
                        Response = new AgentResponse(
                            WorkflowChatMessageConverter.CreateAssistantMessage(llmOutput.Text, llmOutput.Reasoning)),
                        TurnMessages = ToTurnMessages(messages, turnStartIndex, llmOutput.ReasoningReplay)
                    };
                }

                // Tool-call loop
                messages.Add(new WorkflowChatMessage
                {
                    Role = "assistant",
                    Content = llmOutput.Text,
                    FunctionCalls = llmOutput.FunctionCalls,
                    // Kept so the next iteration sends it back: providers such as Anthropic require the
                    // reasoning that preceded a tool call to accompany its result.
                    Reasoning = KeptReasoning(llmOutput)
                });
                lastReplay = llmOutput.ReasoningReplay;

                // Activity: Execute each tool call as a separate activity.
                var toolResults = new List<ExecuteToolOutput>();
                foreach (var fc in llmOutput.FunctionCalls!)
                {
                    var toolInput = new ExecuteToolInput(
                        input.AgentName,
                        fc.Name,
                        fc.CallId,
                        fc.ArgumentsJson,
                        input.TelemetryBaggage);
                    var toolOutput = await context.CallActivityAsync<ExecuteToolOutput>(
                        nameof(ExecuteToolActivity), toolInput);
                    toolResults.Add(toolOutput);
                }

                // Add tool results to the conversation.
                messages.Add(new WorkflowChatMessage
                {
                    Role = "tool",
                    FunctionResults = toolResults.Select(r => new WorkflowFunctionResult
                    {
                        CallId = r.CallId,
                        Name = r.FunctionName,
                        ResultJson = r.ResultJson
                    }).ToList()
                });
            }

            var maxIterationResponse = new AgentResponse(new ChatMessage(ChatRole.Assistant,
                $"Agent '{input.AgentName}' exceeded the maximum of {MaxIterations} iterations without producing a final response."));

            await NotifyAgentContextCompleteAsync(context, input, agentContext, messages, turnStartIndex, turnRequestMessages, errorMessage: null);

            return new AgentRunResult
            {
                Response = maxIterationResponse,
                TurnMessages = ToTurnMessages(messages, turnStartIndex, lastReplay)
            };
        }
        catch (Exception ex)
        {
            await NotifyAgentContextCompleteAsync(context, input, agentContext, messages, turnStartIndex, turnRequestMessages, errorMessage: ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The reasoning to keep on the assistant message: none when the agent's replay setting is
    /// <see cref="ReasoningReplay.Off"/>, so its messages do not grow with reasoning never sent back.
    /// </summary>
    private static List<WorkflowReasoningContent>? KeptReasoning(CallLlmOutput llmOutput) =>
        llmOutput.ReasoningReplay == ReasoningReplay.Off ? null : llmOutput.Reasoning;

    /// <summary>
    /// The run's own messages returned as <see cref="AgentRunResult.TurnMessages"/>. With
    /// <see cref="ReasoningReplay.WithinRun"/> or <see cref="ReasoningReplay.Off"/> they leave the
    /// reasoning out, so the history passed to later turns does not grow with it.
    /// </summary>
    private static List<WorkflowChatMessage> ToTurnMessages(
        List<WorkflowChatMessage> messages,
        int turnStartIndex,
        ReasoningReplay? replay)
    {
        var turnMessages = messages.Skip(turnStartIndex).ToList();
        if (replay is ReasoningReplay.WithinRun or ReasoningReplay.Off)
        {
            StripReasoning(turnMessages, 0, turnMessages.Count);
        }

        return turnMessages;
    }

    /// <summary>Removes the reasoning from <paramref name="messages"/>[start..end).</summary>
    private static void StripReasoning(List<WorkflowChatMessage> messages, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (messages[i].Reasoning is not null)
            {
                messages[i] = messages[i] with { Reasoning = null };
            }
        }
    }

    /// <summary>
    /// Notifies the agent's <c>AIContextProviders</c> that this run has completed, successfully or
    /// not (<see cref="CompleteAgentContextActivity"/>). A no-op (near-instant) activity call when
    /// the agent has no context providers configured. Threads through the same <c>Options</c> and
    /// the serialized session <see cref="ResolveAgentContextActivity"/> produced, so
    /// <c>InvokedAsync</c> sees the same invocation metadata and logical session as
    /// <c>InvokingAsync</c> did.
    /// </summary>
    private static Task NotifyAgentContextCompleteAsync(
        WorkflowContext context,
        DaprAgentInvocation input,
        ResolveAgentContextOutput agentContext,
        List<WorkflowChatMessage> messages,
        int turnStartIndex,
        List<WorkflowChatMessage> turnRequestMessages,
        string? errorMessage)
    {
        var responseMessages = messages.Skip(turnStartIndex + turnRequestMessages.Count).ToList();

        return context.CallActivityAsync(
            nameof(CompleteAgentContextActivity),
            new CompleteAgentContextInput(
                input.AgentName,
                input.ChatClientKey,
                turnRequestMessages,
                responseMessages,
                errorMessage,
                input.TelemetryBaggage)
            {
                Options = input.Options,
                SerializedSessionJson = agentContext.SerializedSessionJson
            });
    }
}
