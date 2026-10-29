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

// A small team of agents learns a skill by doing a task with it: the analyst uses the skill, the
// engineer proposes better parameters, a deterministic backtest decides, and the proposal is
// published as the next skill version only if it is measurably better.

using System.Text.Json;
using Dapr.Workflow;
using Diagrid.AI.Microsoft.AgentFramework.Hosting;
using TeamLearningDemo;
using TeamLearningDemo.Market;

var builder = WebApplication.CreateBuilder(args);

var paths = new TeamPaths(builder.Environment.ContentRootPath);
var store = new CandleStore(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, paths.CandleCache);

// Without any agent:
//   dotnet run -- --backtest                                        score the published skill
//   dotnet run -- --backtest --propose support '{"min_score": 2}'   judge a proposal like the workflow does
if (args.Contains("--backtest"))
{
    var published = SkillParams.Parse(await File.ReadAllTextAsync(paths.ParamsFile));
    var backtest = await Backtest.LoadAsync(store);
    Console.WriteLine($"skill v{published.Version}; {backtest.Range}");
    var propose = Array.IndexOf(args, "--propose");
    if (propose >= 0)
    {
        if (propose + 2 >= args.Length)
        {
            Console.Error.WriteLine("usage: --backtest --propose <support|resistance> '<changes JSON>'");
            Environment.ExitCode = 2;
            return;
        }
        var verdict = await ProposalJudge.JudgeAsync(published, args[propose + 1], args[propose + 2], () => Task.FromResult(backtest));
        Console.WriteLine($"valid {verdict.Valid}, promote {verdict.Promote}: {verdict.ResultJson}");
        Environment.ExitCode = verdict.Valid ? 0 : 2;
        return;
    }
    foreach (var side in Levels.Sides)
    {
        Console.WriteLine($"{side}: {JsonSerializer.Serialize(backtest.Verdict(side, published.For(side), baseline: null))}");
    }
    return;
}

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(store);

var catalogue = string.Join("\n", LevelParams.Catalogue.Select(c => $"- {c.Key}: {c.Value.Min} to {c.Value.Max}"
                                                                    + (c.Value.Integer ? " (integer)" : "")));

builder.Services.AddDaprAgents(registrations: opt =>
    {
        opt.RegisterWorkflow<TeamLearningWorkflow>();
        opt.RegisterActivity<PrepareBaselineActivity>();
        opt.RegisterActivity<EvaluateProposalActivity>();
        opt.RegisterActivity<PublishActivity>();
    })
    .WithAgent(
        agentName: "AnalystAgent",
        conversationComponentName: "conversation-ollama",
        instructions: """
                      You are the team's analyst. You answer with the btc-support-resistance skill: load it, run
                      its levels script, and report exactly what the script returns, in the report format the
                      skill defines. You never compute or estimate prices yourself.
                      """,
        serviceLifetime: ServiceLifetime.Singleton)
    .WithSkills("AnalystAgent", skills => skills.UseSkill(new BtcSupportResistanceSkill(store, paths)))
    .WithAgent(
        agentName: "EngineerAgent",
        conversationComponentName: "conversation-ollama",
        instructions: $$"""
                        You are the team's engineer. You read backtest results of the team's skill and propose new
                        parameters for one side (support or resistance) aimed at where it fails. A precision gain
                        only counts if coverage stays at or above 70%.

                        Parameters you may change, with their allowed ranges (anything else is rejected):
                        {{catalogue}}

                        trend_gate_pct makes a side abstain after a 7-day move against it larger than that percent;
                        min_score makes it abstain when its best level is weak; carry_days keeps an unbroken zone
                        from the previous day when it abstains. The w_* weights score levels by touches, volume,
                        reaction volume, recency, confluence, role flips and distance.

                        Answer with only this JSON:
                        {"side": "support" | "resistance", "changes": {"<parameter>": <number>, ...}, "rationale": "..."}
                        """,
        serviceLifetime: ServiceLifetime.Singleton)
    .WithAgent(
        agentName: "SkillCuratorAgent",
        conversationComponentName: "conversation-ollama",
        instructions: """
                      You are the team's skill curator. You turn the outcome of a learning run into a short lesson.
                      The backtest decides what is published; you never change or second-guess its verdict, and you
                      never report a number you were not given.
                      """,
        serviceLifetime: ServiceLifetime.Singleton);

var app = builder.Build();

// POST /learn — start a learning run. Body (optional): {"cutoff": "2026-10-19T00:00:00Z"}
app.MapPost("/learn", async (DaprWorkflowClient client, LearnRequest? request) =>
{
    var id = "learn-" + Guid.NewGuid().ToString("N")[..12];
    await client.ScheduleNewWorkflowAsync(nameof(TeamLearningWorkflow), id, request ?? new LearnRequest());
    return Results.Accepted($"/learn/{id}", new { instanceId = id });
});

// GET /learn/{id} — status, then the outcome: task report, proposal, verdict, published version, lesson.
app.MapGet("/learn/{id}", async (DaprWorkflowClient client, string id, CancellationToken ct) =>
    await client.GetWorkflowStateAsync(id, cancellation: ct) is { } state
        ? Results.Ok(new
        {
            status = state.RuntimeStatus.ToString(),
            result = state.RuntimeStatus == WorkflowRuntimeStatus.Completed ? state.ReadOutputAs<LearnResult>() : null,
            failure = state.FailureDetails?.ErrorMessage,
        })
        : Results.NotFound());

await app.RunAsync();
