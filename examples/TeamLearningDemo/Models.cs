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

using System.Text.Json.Serialization;

namespace TeamLearningDemo;

public sealed record LearnRequest(
    [property: JsonPropertyName("cutoff")] DateTime? Cutoff = null);

/// <param name="Published">True when the backtest promoted the plugin and it became the next version.</param>
public sealed record LearnResult(
    [property: JsonPropertyName("taskReport")] string TaskReport,
    [property: JsonPropertyName("plugin")] string? Plugin,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("published")] bool Published,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("lesson")] string Lesson);

// ---------------------------------------------------------------- activity input/output

public sealed record Baseline(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("verdictJson")] string VerdictJson,
    [property: JsonPropertyName("reportJson")] string ReportJson);

public sealed record PluginInput(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("code")] string Code);

/// <param name="Stage">"rejected_by_sandbox", "rejected_by_backtest" or "promoted".</param>
/// <param name="ResultJson">The sandbox error, or the backtest's verdict for the plugin.</param>
public sealed record PluginVerdict(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("promote")] bool Promote,
    [property: JsonPropertyName("resultJson")] string ResultJson);

public sealed record PublishInput(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("baseVersion")] int BaseVersion,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("resultJson")] string ResultJson,
    [property: JsonPropertyName("lesson")] string Lesson);
