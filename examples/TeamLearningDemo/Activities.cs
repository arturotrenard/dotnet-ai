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

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapr.Workflow;

namespace TeamLearningDemo;

// The deterministic half of the team. Agents propose; these activities measure and decide. Each
// one shells out to backtest.py, so every number in a run comes from the same script the skill is
// judged by, and none of it is a model's own arithmetic.

/// <summary>Where the demo keeps its files, resolved from the project directory.</summary>
public sealed class TeamPaths(string contentRoot)
{
    public string SkillDirectory { get; } = Path.Combine(contentRoot, "skills", "btc-support-resistance");
    public string ParamsFile => Path.Combine(SkillDirectory, "params.json");
    public string FeaturesDirectory => Path.Combine(SkillDirectory, "features");
    public string MethodologyFile => Path.Combine(SkillDirectory, "references", "methodology.md");
    public string BacktestScript { get; } = Path.Combine(contentRoot, "backtest", "backtest.py");
    public string Workspace { get; } = Path.Combine(contentRoot, "workspace");
    public string RunDirectory(string runId) => Path.Combine(Workspace, "runs", runId);
}

/// <summary>
/// Runs the skill's and the backtest's Python scripts. The child gets a scrubbed environment, so
/// secrets in this process (OLLAMA_API_KEY) never reach the scripts or the plugins agents write,
/// and <c>python3 -I</c>, so neither PYTHON* variables nor the working directory can inject modules.
/// </summary>
public sealed class PythonRunner(TeamPaths paths)
{
    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string script, IEnumerable<string> arguments, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }
        psi.Environment.Clear();
        psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        psi.Environment["BTC_LEVELS_CACHE"] = Path.Combine(paths.Workspace, "cache");
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("python3 did not start");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, await stdout, "timed out");
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>Caches the 2026 candles and scores the published skill: verdict on test, failures by regime on train.</summary>
public sealed class PrepareBaselineActivity(PythonRunner python, TeamPaths paths) : WorkflowActivity<string, Baseline>
{
    public override async Task<Baseline> RunAsync(WorkflowActivityContext context, string runId)
    {
        var dir = paths.RunDirectory(runId);
        Directory.CreateDirectory(dir);
        var prefetch = await python.RunAsync(paths.BacktestScript, ["--prefetch"], TimeSpan.FromMinutes(5));
        if (prefetch.ExitCode != 0)
        {
            throw new InvalidOperationException($"prefetch failed: {prefetch.Stderr}");
        }
        var verdict = Path.Combine(dir, "baseline-verdict.json");
        var report = Path.Combine(dir, "baseline-report.json");
        var run = await python.RunAsync(paths.BacktestScript, ["--report", report, "--json", verdict],
            TimeSpan.FromMinutes(5), new Dictionary<string, string> { ["BTC_LEVELS_OFFLINE"] = "1" });
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException($"baseline backtest failed: {run.Stderr}");
        }
        var version = JsonNode.Parse(await File.ReadAllTextAsync(paths.ParamsFile))?["version"]?.GetValue<int>() ?? 1;
        return new Baseline(version, await File.ReadAllTextAsync(verdict), await File.ReadAllTextAsync(report));
    }
}

/// <summary>
/// Validates the engineer's plugin in the sandbox (AST whitelist, pure builtins, time limit,
/// offline process) and, if it runs, judges it: weight chosen on train, readiness checks on test
/// against the published version. Only this result can promote a plugin.
/// </summary>
public sealed partial class EvaluatePluginActivity(PythonRunner python, TeamPaths paths)
    : WorkflowActivity<PluginInput, PluginVerdict>
{
    [GeneratedRegex("^[a-z][a-z0-9_]{2,40}$")]
    private static partial Regex PluginName();

    public override async Task<PluginVerdict> RunAsync(WorkflowActivityContext context, PluginInput input)
    {
        if (!PluginName().IsMatch(input.Name) || File.Exists(Path.Combine(paths.FeaturesDirectory, input.Name + ".py")))
        {
            return Rejected("the plugin name must be new and lower_snake_case");
        }
        var dir = Path.Combine(paths.RunDirectory(input.RunId), "plugins");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, input.Name + ".py"), input.Code);
        var sandbox = new Dictionary<string, string> { ["BTC_LEVELS_FEATURE_PATH"] = dir };

        var check = await python.RunAsync(paths.BacktestScript, ["--sandbox", "--check-feature", input.Name],
            TimeSpan.FromSeconds(90), sandbox);
        if (check.ExitCode != 0 || JsonNode.Parse(check.Stdout)?["ok"]?.GetValue<bool>() != true)
        {
            return Rejected(check.ExitCode == 0 ? JsonNode.Parse(check.Stdout)?["error"]?.ToString() ?? check.Stdout : check.Stderr);
        }

        var output = Path.Combine(dir, input.Name + ".evaluation.json");
        var evaluation = await python.RunAsync(paths.BacktestScript,
            ["--sandbox", "--evaluate-feature", input.Name, "--baseline", paths.ParamsFile, "--json", output],
            TimeSpan.FromMinutes(10), sandbox);
        if (evaluation.ExitCode != 0)
        {
            return Rejected(evaluation.Stderr);
        }
        var json = await File.ReadAllTextAsync(output);
        var promote = JsonNode.Parse(json)?["promote"]?.GetValue<bool>() == true;
        return new PluginVerdict(promote ? "promoted" : "rejected_by_backtest", promote, json);
    }

    private static PluginVerdict Rejected(string error) =>
        new("rejected_by_sandbox", false, new JsonObject { ["error"] = error }.ToJsonString());
}

/// <summary>
/// Publishes a promoted plugin as the next skill version: copies it into features/, enables it in
/// params.json for the sides it improved, and appends the change to the methodology. levels.py
/// reads these files on every run, so the new version takes effect without a restart.
/// </summary>
public sealed class PublishActivity(TeamPaths paths) : WorkflowActivity<PublishInput, int>
{
    public override async Task<int> RunAsync(WorkflowActivityContext context, PublishInput input)
    {
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(paths.ParamsFile))!.AsObject();
        var onDisk = doc["version"]?.GetValue<int>() ?? 1;
        if (onDisk != input.BaseVersion)
        {
            return onDisk;  // published already (a retry) or by another run: do not apply it twice
        }
        var result = JsonNode.Parse(input.ResultJson)!;
        foreach (var (side, r) in result["sides"]!.AsObject())
        {
            if (r?["promote"]?.GetValue<bool>() == true)
            {
                var sideParams = doc[side] as JsonObject ?? new JsonObject();
                var features = sideParams["features"] as JsonObject ?? new JsonObject();
                features[input.Name] = r["weight"]!.GetValue<double>();
                sideParams["features"] = features;
                doc[side] = sideParams;
            }
        }
        var version = input.BaseVersion + 1;
        doc["version"] = version;
        doc["notes"] = $"v{version}: adds plugin '{input.Name}', promoted by learning run {input.RunId}.";

        Directory.CreateDirectory(paths.FeaturesDirectory);
        File.Copy(Path.Combine(paths.RunDirectory(input.RunId), "plugins", input.Name + ".py"),
            Path.Combine(paths.FeaturesDirectory, input.Name + ".py"), overwrite: true);
        await File.AppendAllTextAsync(paths.MethodologyFile,
            $"\n### v{version}: plugin `{input.Name}`\n\n- **Learning run:** `{input.RunId}`\n- **Note:** {input.Lesson}\n");
        // params.json last: until it changes, the previous version stays in force.
        await File.WriteAllTextAsync(paths.ParamsFile, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return version;
    }
}
