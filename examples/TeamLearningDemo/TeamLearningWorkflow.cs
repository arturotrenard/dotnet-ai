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

using System.Globalization;
using System.Text.RegularExpressions;
using Dapr.Workflow;
using Diagrid.AI.Microsoft.AgentFramework.Runtime;

namespace TeamLearningDemo;

/// <summary>
/// One learning run: the analyst does the task with the published skill, the engineer proposes a
/// plugin aimed at where the skill fails, the backtest judges it, and the plugin becomes the next
/// skill version only if it is promoted. The curator records the lesson either way.
/// </summary>
/// <remarks>
/// Agents only propose; the sandbox check, the backtest verdict and the publication are
/// activities. The workflow body is deterministic: it branches only on recorded activity and agent
/// results, so it replays to the same decisions.
/// </remarks>
public sealed partial class TeamLearningWorkflow : Workflow<LearnRequest, LearnResult>
{
    private const int EngineerAttempts = 3;

    public override async Task<LearnResult> RunAsync(WorkflowContext context, LearnRequest input)
    {
        var runId = context.InstanceId;
        var cutoff = (input.Cutoff?.ToUniversalTime() ?? context.CurrentUtcDateTime.Date)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var baseline = await context.CallActivityAsync<Baseline>(nameof(PrepareBaselineActivity), runId);

        // 1. The task, done with the published skill.
        var analyst = await context.RunAgentAsync(context.GetAgent("AnalystAgent"),
            $"Using the btc-support-resistance skill, report the BTCUSDT support and resistance zones for the "
            + $"cutoff {cutoff}. Run the skill's levels script with --end {cutoff} and return the report JSON "
            + "the skill defines.");
        var taskReport = analyst.Text ?? string.Empty;

        // 2. The engineer studies the backtest and proposes one plugin; the sandbox may send it back.
        var engineer = context.GetAgent("EngineerAgent");
        var history = new List<WorkflowChatMessage>();
        var message = $"The published skill (v{baseline.Version}) on the test split:\n{baseline.VerdictJson}\n\n"
                      + $"Its precision and coverage by regime on the train split:\n{baseline.ReportJson}\n\n"
                      + "Find the weakness that matters most (a large group of windows where precision is low) and "
                      + "write one plugin that adds price levels aimed at it.";
        string? plugin = null;
        var verdict = new PluginVerdict("no_plugin", false, "{}");
        for (var attempt = 1; attempt <= EngineerAttempts; attempt++)
        {
            var turn = await context.RunAgentWithHistoryAsync(engineer, message, history);
            history.AddRange(turn.TurnMessages);
            var code = PythonBlock().Match(turn.Response.Text ?? string.Empty) is { Success: true } m
                ? m.Groups[1].Value.Trim() + "\n" : null;
            plugin = code is null ? null : PluginName().Match(code) is { Success: true } n ? n.Groups[1].Value : null;
            if (code is null || plugin is null)
            {
                message = "Return the complete module in one ```python block, with NAME = \"lower_snake_case\".";
                continue;
            }
            verdict = await context.CallActivityAsync<PluginVerdict>(nameof(EvaluatePluginActivity),
                new PluginInput(runId, plugin, code));
            if (verdict.Stage != "rejected_by_sandbox")
            {
                break;
            }
            message = $"The sandbox rejected the plugin:\n{verdict.ResultJson}\nFix it and return the complete module.";
        }

        // 3. The curator records what was learned; it does not change the verdict.
        var curator = await context.RunAgentAsync(context.GetAgent("SkillCuratorAgent"),
            $"Plugin '{plugin ?? "(none)"}' ended as '{verdict.Stage}'. Backtest result:\n{verdict.ResultJson}\n\n"
            + "In two or three sentences, write the lesson for the team: why it likely worked or failed, "
            + "and what to try next. Use only the numbers given.");
        var lesson = curator.Text ?? string.Empty;

        // 4. Publish only what the backtest promoted.
        var version = baseline.Version;
        if (verdict.Promote && plugin is not null)
        {
            version = await context.CallActivityAsync<int>(nameof(PublishActivity),
                new PublishInput(runId, baseline.Version, plugin, verdict.ResultJson, lesson));
        }
        return new LearnResult(taskReport, plugin, verdict.Stage, verdict.Promote, version, lesson);
    }

    [GeneratedRegex(@"```(?:python|py)?\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex PythonBlock();

    [GeneratedRegex("^NAME\\s*=\\s*[\"']([a-z][a-z0-9_]{2,40})[\"']", RegexOptions.Multiline)]
    private static partial Regex PluginName();
}
