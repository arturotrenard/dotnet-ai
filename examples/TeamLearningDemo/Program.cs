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
// engineer proposes an improvement as a sandboxed plugin, a deterministic backtest decides, and
// the improvement is published as the next skill version only if it is measurably better.

using System.Text.Json;
using Dapr.Workflow;
using Diagrid.AI.Microsoft.AgentFramework.Hosting;
using Microsoft.Agents.AI;
using TeamLearningDemo;

var builder = WebApplication.CreateBuilder(args);

var paths = new TeamPaths(builder.Environment.ContentRootPath);
var python = new PythonRunner(paths);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(python);

builder.Services.AddDaprAgents(registrations: opt =>
    {
        opt.RegisterWorkflow<TeamLearningWorkflow>();
        opt.RegisterActivity<PrepareBaselineActivity>();
        opt.RegisterActivity<EvaluatePluginActivity>();
        opt.RegisterActivity<PublishActivity>();
    })
    .WithAgent(
        agentName: "AnalystAgent",
        conversationComponentName: "conversation-ollama",
        instructions: """
                      You are the team's analyst. You answer with the btc-support-resistance skill: load it,
                      run its levels script, and report exactly what the script returns, in the report format
                      the skill defines. You never compute or estimate prices yourself.
                      """,
        serviceLifetime: ServiceLifetime.Singleton)
    .WithSkills("AnalystAgent", skills => skills
        // Only scripts/levels.py is a script: features/*.py are plugins that levels.py loads itself,
        // after validating them, and must never be run directly.
        .UseFileSkill(paths.SkillDirectory,
            new AgentFileSkillsSourceOptions { ScriptFilter = f => f.RelativeFilePath == "scripts/levels.py" },
            scriptRunner: async (_, script, arguments, _, ct) =>
            {
                var args = arguments is { ValueKind: JsonValueKind.Array } a
                    ? a.EnumerateArray().Select(e => e.ToString())
                    : arguments?.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                var result = await python.RunAsync(script.FullPath, args, TimeSpan.FromMinutes(2), cancellationToken: ct);
                return result.ExitCode == 0 ? result.Stdout : $"exit code {result.ExitCode}\n{result.Stdout}{result.Stderr}";
            }))
    .WithAgent(
        agentName: "EngineerAgent",
        conversationComponentName: "conversation-ollama",
        instructions: """
                      You are the team's engineer. You read backtest results of the team's skill and write one
                      plugin for its levels.py that adds price levels where the skill fails. A sandbox rejects
                      anything outside these rules:

                      - One Python module. The only import allowed is `import math`.
                      - Module-level string literals: NAME = "<lower_snake_case>", DESCRIPTION = "<one line>",
                        SOURCE = "<where the idea comes from>".
                      - `def levels(candles):` gets the 336 closed 30-minute BTCUSDT candles before the cutoff,
                        oldest first, as dicts with keys t, o, h, l, c, v. Do not modify them.
                      - Return at most 50 dicts {"level": <positive float price>}; the skill assigns the side by
                        comparing each level with the last close.
                      - Only arithmetic, control flow, comprehensions, helper functions, lists, dicts, tuples
                        and the builtins abs all any bool dict enumerate filter float int isinstance len list
                        map max min range reversed round set sorted sum tuple zip. Attributes only for math
                        functions and list/dict methods. No string methods, print, classes, names starting
                        with an underscore, global/nonlocal, with or yield. Under 2 s per call.

                      Answer with the complete module in a single ```python block.
                      """,
        serviceLifetime: ServiceLifetime.Singleton)
    .WithAgent(
        agentName: "SkillCuratorAgent",
        conversationComponentName: "conversation-ollama",
        instructions: """
                      You are the team's skill curator. You turn the outcome of a learning run into a short
                      lesson. The backtest decides what is published; you never change or second-guess its
                      verdict, and you never report a number you were not given.
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

// GET /learn/{id} — status, then the outcome: task report, plugin, verdict, published version, lesson.
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
