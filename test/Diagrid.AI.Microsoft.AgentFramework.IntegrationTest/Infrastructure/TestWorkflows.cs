// Copyright (c) 2026-present Diagrid Inc
//
// Licensed under the Business Source License 1.1 (BSL 1.1).

using Dapr.Workflow;
using Diagrid.AI.Microsoft.AgentFramework.Runtime;
using Microsoft.Extensions.AI;

namespace Diagrid.AI.Microsoft.AgentFramework.IntegrationTest.Infrastructure;

/// <summary>
/// Custom workflow that invokes EchoAgent via <see cref="WorkflowContextExtensions.GetAgent"/>
/// and <see cref="WorkflowContextExtensions.RunAgentAsync"/>, exercising the workflow-context path.
/// </summary>
internal sealed class EchoOrchestrationWorkflow : Workflow<string, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, string input)
    {
        var agent    = context.GetAgent("EchoAgent");
        var response = await context.RunAgentAsync(agent, message: input);
        return response.Text ?? string.Empty;
    }
}

/// <summary>
/// Custom workflow that starts EchoAgent as a detached run via
/// <see cref="WorkflowContextExtensions.StartAgentRunDetachedAsync"/> and completes immediately with the
/// run's instance ID, without waiting for the run.
/// </summary>
internal sealed class DetachedEchoOrchestrationWorkflow : Workflow<string, string>
{
    public override Task<string> RunAsync(WorkflowContext context, string input) =>
        context.StartAgentRunDetachedAsync(context.GetAgent("EchoAgent"), message: input);
}

/// <summary>
/// Custom workflow that invokes CapitalAgent and deserializes its JSON response via
/// <see cref="WorkflowContextExtensions.RunAgentAndDeserializeAsync{T}"/>.
/// </summary>
internal sealed class CapitalOrchestrationWorkflow : Workflow<string, CapitalAnswer?>
{
    public override async Task<CapitalAnswer?> RunAsync(WorkflowContext context, string input)
    {
        var agent = context.GetAgent("CapitalAgent");
        return await context.RunAgentAndDeserializeAsync<CapitalAnswer>(agent, message: input);
    }
}

/// <summary>
/// Custom workflow that resolves a keyed agent (AlphaAgent / "chat-key-alpha") via
/// <see cref="WorkflowContextExtensions.GetAgent(WorkflowContext, string, string)"/>.
/// </summary>
internal sealed class KeyedOrchestrationWorkflow : Workflow<string, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, string input)
    {
        var agent    = context.GetAgent("AlphaAgent", "chat-key-alpha");
        var response = await context.RunAgentAsync(agent, message: input);
        return response.Text ?? string.Empty;
    }
}

/// <summary>
/// Custom workflow that runs <see cref="ReasoningMockChatClient.AgentName"/> for two turns with
/// <see cref="WorkflowContextExtensions.RunAgentWithHistoryAsync"/>, passing the first turn's
/// messages as the second turn's history. Returns
/// <c>{turn 1 text}|{turn 2 text}|{reasoning on the turn 2 response}</c>.
/// </summary>
internal sealed class ReasoningHistoryOrchestrationWorkflow : Workflow<string, string>
{
    public override async Task<string> RunAsync(WorkflowContext context, string input)
    {
        var agent = context.GetAgent(ReasoningMockChatClient.AgentName);

        var first = await context.RunAgentWithHistoryAsync(agent, $"{input} (turn 1)");
        var second = await context.RunAgentWithHistoryAsync(agent, $"{input} (turn 2)", first.TurnMessages);

        var finalReasoning = second.Response.Messages
            .SelectMany(m => m.Contents)
            .OfType<TextReasoningContent>()
            .Select(r => $"{r.Text}/{r.ProtectedData}");

        return $"{first.Response.Text}|{second.Response.Text}|{string.Join(",", finalReasoning)}";
    }
}
