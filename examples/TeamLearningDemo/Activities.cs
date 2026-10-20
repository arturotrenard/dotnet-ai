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
using System.Text.Json.Nodes;
using Dapr.Workflow;
using TeamLearningDemo.Market;

namespace TeamLearningDemo;

// The deterministic half of the team. Agents propose; these activities measure and decide, with the
// same Backtest the skill is judged by, so no number in a run is a model's own arithmetic.

/// <summary>Where the demo keeps its files, resolved from the project directory.</summary>
public sealed class TeamPaths(string contentRoot)
{
    public string ParamsFile { get; } = Path.Combine(contentRoot, "skill", "params.json");
    public string MethodologyFile { get; } = Path.Combine(contentRoot, "skill", "methodology.md");
    public string CandleCache { get; } = Path.Combine(contentRoot, "workspace", "cache");
}

/// <summary>Scores the published skill: readiness per side on test, failures by regime on train.</summary>
public sealed class PrepareBaselineActivity(CandleStore store, TeamPaths paths) : WorkflowActivity<string, Baseline>
{
    public override async Task<Baseline> RunAsync(WorkflowActivityContext context, string runId)
    {
        var published = SkillParams.Parse(await File.ReadAllTextAsync(paths.ParamsFile));
        var backtest = await Backtest.LoadAsync(store);
        var verdict = new JsonObject();
        var report = new JsonObject();
        foreach (var side in Levels.Sides)
        {
            verdict[side] = JsonSerializer.SerializeToNode(backtest.Verdict(side, published.For(side), baseline: null));
            report[side] = JsonSerializer.SerializeToNode(backtest.RegimeReport(published.For(side), side));
        }
        return new Baseline(published.Version, verdict.ToJsonString(), report.ToJsonString());
    }
}

/// <summary>
/// Validates the engineer's proposal against the parameter catalogue and, if valid, judges it: the
/// candidate version against the published one on the test split. Only this result can promote it.
/// </summary>
public sealed class EvaluateProposalActivity(CandleStore store, TeamPaths paths) : WorkflowActivity<ProposalInput, ProposalVerdict>
{
    public override async Task<ProposalVerdict> RunAsync(WorkflowActivityContext context, ProposalInput input)
    {
        var published = SkillParams.Parse(await File.ReadAllTextAsync(paths.ParamsFile));
        SkillParams candidate;
        try
        {
            if (!Levels.Sides.Contains(input.Side))
            {
                throw new ArgumentException("side must be \"support\" or \"resistance\"");
            }
            var changes = JsonNode.Parse(input.ChangesJson) as JsonObject
                          ?? throw new ArgumentException("changes must be a JSON object");
            if (changes.Count == 0)
            {
                throw new ArgumentException("changes is empty: propose at least one parameter");
            }
            candidate = published.WithChanges(input.Side, changes);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException)
        {
            return new ProposalVerdict(false, false, new JsonObject { ["error"] = ex.Message }.ToJsonString());
        }

        var backtest = await Backtest.LoadAsync(store);
        var verdict = backtest.Verdict(input.Side, candidate.For(input.Side), published.For(input.Side));
        var train = Backtest.Summarize(backtest.Evaluate(backtest.Train, candidate.For(input.Side), input.Side));
        var trainPublished = Backtest.Summarize(backtest.Evaluate(backtest.Train, published.For(input.Side), input.Side));
        var result = new JsonObject
        {
            ["side"] = input.Side,
            ["test"] = JsonSerializer.SerializeToNode(verdict),
            ["train"] = JsonSerializer.SerializeToNode(train),
            ["trainPublished"] = JsonSerializer.SerializeToNode(trainPublished),
        };
        return new ProposalVerdict(true, verdict.Ready, result.ToJsonString());
    }
}

/// <summary>
/// Publishes a promoted proposal as the next skill version: the new overrides go into
/// <c>skill/params.json</c> and the change into the methodology's changelog.
/// </summary>
public sealed class PublishActivity(TeamPaths paths) : WorkflowActivity<PublishInput, int>
{
    public override async Task<int> RunAsync(WorkflowActivityContext context, PublishInput input)
    {
        var published = SkillParams.Parse(await File.ReadAllTextAsync(paths.ParamsFile));
        if (published.Version != input.BaseVersion)
        {
            return published.Version;  // published already (a retry) or by another run: do not apply it twice
        }
        var next = published.WithChanges(input.Side, JsonNode.Parse(input.ChangesJson)!.AsObject());
        var version = input.BaseVersion + 1;
        next.Document["version"] = version;
        next.Document["notes"] = $"v{version}: {input.Side} changes {input.ChangesJson}, promoted by learning run {input.RunId}.";

        await File.AppendAllTextAsync(paths.MethodologyFile,
            $"\n### v{version}: {input.Side} {input.ChangesJson}\n\n- **Learning run:** `{input.RunId}`\n"
            + $"- **Why:** {input.Rationale}\n- **Lesson:** {input.Lesson}\n");
        // params.json last: until it changes, the previous version stays in force.
        await File.WriteAllTextAsync(paths.ParamsFile, next.ToJson() + "\n");
        return version;
    }
}
