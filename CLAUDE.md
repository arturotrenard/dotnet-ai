# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`AGENTS.md` is the canonical assistant guide for this repo — build rules, CI gates, packaging,
runtime constraints, Catalyst and example gotchas. It is imported here; do not duplicate it,
and update it (not this file) when one of its facts changes.

@AGENTS.md

## This checkout is a fork — overrides to AGENTS.md

This working copy is `arturotrenard/dotnet-ai`, a fork of `diagridio/dotnet-ai`. Where it
conflicts with AGENTS.md's git and CI notes, this section wins:

- Remotes: `origin` = the fork, `upstream` = `diagridio/dotnet-ai`.
- **`master` is a pristine mirror of `upstream/master`.** Never commit to it; only
  fast-forward it (`git fetch upstream && git switch master && git merge --ff-only upstream/master && git push origin master`).
- **All work happens on `dev`** (or feature branches off `dev`, PR'd into `dev`). Bring upstream
  in with `git merge upstream/master` on `dev`.
- **CI here is `.github/workflows/fork-ci.yml`**, on GitHub-hosted `ubuntu-latest`, triggered by
  pushes and PRs to `dev`. It runs every test project on all three frameworks, so unlike
  upstream it does cover `net8.0`/`net9.0`. Upstream's `build.yml` is **disabled in the fork's
  Actions settings** (its RunsOn runners do not exist here) rather than edited, so that
  `master` stays identical to upstream. Do not re-enable it, and do not push tags to the fork
  expecting a NuGet publish.
- Changes meant for upstream go from a branch off `upstream/master`, not from `dev`, so they do
  not carry `fork-ci.yml` or this section.

## Running tests

Tests are xunit v3 (`xunit.v3` + `xunit.runner.visualstudio`), run through `dotnet test`:

```
# one unit-test project, one framework
dotnet test test/Diagrid.AI.Microsoft.AgentFramework.Test/Diagrid.AI.Microsoft.AgentFramework.Test.csproj --framework net10.0
dotnet test test/Diagrid.AI.Identity.Test/Diagrid.AI.Identity.Test.csproj --framework net10.0

# a single test class or method
dotnet test <project.csproj> --framework net10.0 --filter "FullyQualifiedName~JwksVerifierTests"
dotnet test <project.csproj> --framework net10.0 --filter "FullyQualifiedName~JwksVerifierTests.SomeMethodName"

# integration tests (Docker must be running)
dotnet test test/Diagrid.AI.Microsoft.AgentFramework.IntegrationTest/Diagrid.AI.Microsoft.AgentFramework.IntegrationTest.csproj --framework net10.0
```

Always pass `--framework`; without it every test project runs three times (net8/9/10).

## Code map (AgentFramework)

`src/Diagrid.AI.Microsoft.AgentFramework/` is split by folder:

- `Hosting/` — the public entry points: `AddDaprAgents(...)` and the builder extensions
  (`WithSkills`, `WithContextProviders`, `WithCatalyst`, …) in `DaprAgentsBuilderExtensions.cs`.
  This is where DI registration order matters (see the approval-handler note in AGENTS.md).
- `Abstractions/` — public interfaces consumers implement or inject (e.g. `IToolApprovalHandler`).
- `Runtime/` — the durable execution engine. `DaprAIAgent` is the `AIAgent` users get; a turn
  is scheduled as `AgentRunWorkflow` (single run) or `SessionWorkflow` (multi-turn). Workflows
  only orchestrate; each side effect is an activity with a matching `*Input`/`*Output` pair
  (`CallLlmActivity`, `ExecuteToolActivity`, `ResolveAgentContextActivity`,
  `CompleteAgentContextActivity`). `AgentRegistry` / `ChatClientRegistry` / `ToolRegistry` let
  activities find the live agent, chat client and tools by name after replay.
  `WorkflowChatMessage` + `WorkflowChatMessageConverter` and `AgentJsonTypeInfoResolver` are how
  MAF chat messages survive the activity serialization boundary.
- `Catalyst/` — `WithCatalyst(...)` and the agent-registry hosted service (tab-indented).
- `Telemetry/` — `UsageAnalytics`, the anonymous Scarf event sent from `AddDaprAgents`
  (opt out with `DO_NOT_TRACK`).

The call path: `DaprAgentInvoker` (`Hosting/`) schedules the workflow through
`DaprWorkflowClient.ScheduleNewWorkflowAsync`; `SessionWorkflow` and the helpers in
`WorkflowContextExtensions` run each turn as a child `AgentRunWorkflow`.
`AddDaprAgents` (`DaprAgentsServiceCollectionExtensions.cs`) wires the Dapr Workflow builder,
the serializer choice, and the optional `registrations` callback for user workflows.

A new activity follows the existing shape: an `*Activity` class plus `*Input` and `*Output`
types in `Runtime/`, with the new types made serializable as described in AGENTS.md.
