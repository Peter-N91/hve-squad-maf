using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HveSquad.AgentFramework.Hosting;
using HveSquad.AgentFramework.Runtime;
using HveSquad.AgentFramework.Sample;
using HveSquad.AgentFramework.Sources;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace HveSquad.AgentFramework.Tests;

public sealed class RuntimeSkillScriptTests
{
    private const string ScriptPath = "scripts/fixture.ps1";
    private const string Script = """
        #!/usr/bin/env pwsh
        # Copyright (c) 2026 Microsoft Corporation. All rights reserved.
        # SPDX-License-Identifier: MIT
        #Requires -Version 7.4
        <#
        .SYNOPSIS
            Original bounded process fixture; no network or credentials.
        #>
        [CmdletBinding()]
        param([string]$Mode, [string]$Value, [string]$OutputPath, [string]$InputPath)
        $ErrorActionPreference = 'Stop'
        if ($Mode -eq 'fail') { [Console]::Error.WriteLine('original process failure'); exit 7 }
        if ($Mode -eq 'oversize' -or $Mode -eq 'oversize-error') {
            if ($Mode -eq 'oversize') { [Console]::Out.WriteLine(('x' * 8192)) }
            else { [Console]::Error.WriteLine(('x' * 8192)) }
            exit 0
        }
        if ($Mode -eq 'environment') {
            [Console]::Out.WriteLine([Environment]::GetEnvironmentVariable('HVE_SQUAD_PRIVATE_TEST_VALUE') ?? 'absent')
            exit 0
        }
        if ($Mode -eq 'wait' -or $Mode -eq 'spawn') {
            $ids = "$PID"
            if ($Mode -eq 'spawn') {
                $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
                if (-not $IsWindows) { $start.FileName = Join-Path $PSHOME 'pwsh' }
                $start.UseShellExecute = $false
                foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                    $PSCommandPath, '-Mode', 'wait', '-OutputPath', "$OutputPath-child")) {
                    $start.ArgumentList.Add($argument)
                }
                $child = [Diagnostics.Process]::Start($start)
                $ids += ",$($child.Id)"
            }
            [IO.File]::WriteAllText("$OutputPath-pending", $ids)
            [IO.File]::Move("$OutputPath-pending", $OutputPath)
            [Threading.Thread]::Sleep(30000)
            exit 0
        }
        if ($Mode -eq 'dry') { [Console]::Out.WriteLine('original non-applied preview'); exit 0 }
        $text = if ($InputPath) { [IO.File]::ReadAllText($InputPath) + $Value } else { $Value }
        [IO.File]::WriteAllText($OutputPath, $text)
        if ($Mode -eq 'partial') { [Console]::Error.WriteLine('original failure after partial write'); exit 7 }
        [Console]::Out.WriteLine($text)
        """;

    private static string PowerShell()
    {
        var explicitPath = Environment.GetEnvironmentVariable("HVE_SQUAD_TEST_POWERSHELL");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
            if (File.Exists(candidate))
            {
                var file = new FileInfo(candidate);
                var target = file.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    return target.FullName;
                }
                if (file.Length != 0)
                {
                    return file.FullName;
                }
            }
        }
        throw new InvalidOperationException("Actual registered-process tests require PowerShell 7.4+. Set HVE_SQUAD_TEST_POWERSHELL to its absolute executable path; this mandatory integration is not silently skipped.");
    }

    private static string Fixture(RuntimeTestHost host)
    {
        host.Write("skills/apm-renamed-specialist/" + ScriptPath, Script);
        return Path.Combine(host.Artifacts, "skills", "apm-renamed-specialist", "scripts", "fixture.ps1");
    }

    private static SquadProcessInvocation Invocation(
        string script, string project, string mode, string value = "original actual output",
        string output = "implementation.txt", string? input = null) =>
        new(["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, "-Mode", mode, "-Value", value,
                "-OutputPath", Path.Combine(project, output), "-InputPath", input is null ? "" : Path.Combine(project, input)],
            [output]);

    private static async Task<SquadToolRegistration> RegisterAsync(
        RuntimeTestHost host, string mode = "write", string tag = "v0.16.2", string? input = null,
        SquadToolAction action = SquadToolAction.General, string output = "implementation.txt")
    {
        var script = Fixture(host);
        var executor = new SquadProcessExecutor(PowerShell(), host.Project, "fixture-" + mode + "-v1", TimeSpan.FromSeconds(20));
        var roots = await host.Source(tag).ResolveAsync();
        var binding = SquadSkillScriptBinding.Create(roots, "specialist-test", ScriptPath, executor.Binding);
        var function = AIFunctionFactory.Create(async (string value, CancellationToken token) =>
        {
            var invocation = Invocation(script, host.Project, mode, value, output, input);
            if (mode == "dry")
            {
                invocation = invocation with { ProjectOutputs = [] };
            }
            var result = await executor.ExecuteAsync(invocation, token);
            return mode == "dry" ? result with { Output = result.Output with { Status = SquadToolOutputStatus.DryRun } } : result;
        }, "registered_source", "Original bounded host script; no arbitrary shell or path parameter.");
        var registration = new SquadToolRegistration(function, "execute/runInTerminal", ["developer"],
            SquadToolEffect.ProjectWrite, SquadProcessExecutor.GetOutputEvidence,
            ProjectWritePaths: _ => [output], Impact: SquadToolImpact.Safe,
            Action: action, SkillScript: binding, ProjectReadPaths: _ => input is null ? [] : [input]);
        host.Options.Tools.Clear();
        host.Options.Tools.Add(registration);
        return registration;
    }

    private static void QueueScript(RuntimeTestHost host, string value = "original actual output")
    {
        host.Client.Call(AgentSkillsProvider.LoadSkillToolName, new() { ["skillName"] = "specialist-test" });
        host.Client.Call(AgentSkillsProvider.RunSkillScriptToolName, new()
        {
            ["skillName"] = "specialist-test",
            ["scriptName"] = ScriptPath,
            ["arguments"] = new Dictionary<string, object?> { ["value"] = value },
        });
    }

    private static void QueueProducingCompletion(RuntimeTestHost host)
    {
        host.Client.Call("write_artifact", new() { ["content"] = "Original methodology artifact accompanying the actual registered source edit." });
        host.Client.Call("complete_stage", new()
        {
            ["summary"] = "Actual registered source output",
            ["verdict"] = "Completed",
            ["risk"] = "Low",
            ["blockingIssues"] = 0,
        });
        host.Client.Text("Producing dispatch finished.");
    }

    private static void QueueCompletion(RuntimeTestHost host)
    {
        QueueProducingCompletion(host);
        if (host.Options.Mode != "interactive")
        {
            RuntimeModeTests.Council(host);
        }
        host.QueueStage("tester");
        host.Client.Text("Coordinator synthesis.");
    }

    private static void QueuePrework(RuntimeTestHost host)
    {
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        if (host.Options.Mode != "interactive")
        {
            RuntimeModeTests.Council(host);
        }
    }

    private static async Task<SquadRunResult> SuspendAsync(RuntimeTestHost host, SquadRuntime runtime)
    {
        host.QueuePipeline();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        _ = await RuntimeModeTests.RespondAsync(runtime, "routing");
        host.QueueStage("researcher");
        result = await runtime.ResumeAsync(result.RunId);
        _ = await RuntimeModeTests.RespondAsync(runtime, "plan");
        host.QueueStage("lead");
        result = await runtime.ResumeAsync(result.RunId);
        _ = await RuntimeModeTests.RespondAsync(runtime, "implementation");
        QueueScript(host);
        result = await runtime.ResumeAsync(result.RunId);
        Assert.Equal(SquadRunStatus.ApprovalRequired, result.Status);
        Assert.Equal("consequentialtool", Assert.Single(result.PendingHumanActions!, item => item.Status == "pending").Binding.Gate);
        return result;
    }

    [Theory]
    [InlineData("v0.16.2", "interactive")]
    [InlineData("v0.18.1", "interactive")]
    [InlineData("v0.16.2", "autonomous")]
    [InlineData("v0.18.1", "autonomous")]
    [InlineData("v0.16.2", "autopilot")]
    [InlineData("v0.18.1", "autopilot")]
    public async Task GivenCanonicalRegisteredScript_WhenMafInvokesIt_ProducesActualSourceAndExactReceipt(string tag, string mode)
    {
        using var host = new RuntimeTestHost();
        if (tag == "v0.18.1")
        {
            host.UseCurrentPlanContract();
        }
        var registration = await RegisterAsync(host, tag: tag);
        host.Options.Mode = mode;
        host.Options.MaxConcurrentDispatches = 1;
        QueuePrework(host);
        const string value = "literal ; $(not a command) | \"quotes\" & <data>";
        QueueScript(host, value);
        QueueCompletion(host);
        using var runtime = await host.CreateAsync(tag);

        var result = await runtime.RunAsync("Implement the original bounded source fixture.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(value, File.ReadAllText(Path.Combine(host.Project, "implementation.txt")));
        var receipt = Assert.Single(Assert.Single(result.Evidence, item => item.Stage == "produce").ExecutedHostTools!);
        Assert.Equal(registration.SkillScript, receipt.SkillScript!.Binding);
        Assert.Equal(tag, receipt.SkillScript.Binding.Release!.Tag);
        Assert.Equal(["implementation.txt"], receipt.SkillScript.ProjectWritePaths);
        Assert.Contains("exit-code:0", receipt.Receipt, StringComparison.Ordinal);
        Assert.Equal(new ProjectFiles(host.Project, 200_000).HashFile("implementation.txt"), Assert.Single(receipt.Outputs!).Sha256);
        if (mode == "interactive")
        {
            Assert.Equal(registration.SkillScript, Assert.Single(host.Approvals, item => item.Kind == SquadApprovalKind.ConsequentialTool).SkillScript!.Binding);
        }
        else
        {
            Assert.DoesNotContain(host.Approvals, item => item.Kind == SquadApprovalKind.ConsequentialTool);
        }
        Assert.DoesNotContain(host.Client.Calls.SelectMany(call => call.Options?.Tools ?? []), tool => tool.Name == "registered_source");
        Assert.False(host.Client.Disposed);
    }

    [Theory]
    [InlineData("interactive", SquadToolAction.General)]
    [InlineData("autonomous", SquadToolAction.Deployment)]
    [InlineData("autopilot", SquadToolAction.Push)]
    [InlineData("autopilot", SquadToolAction.Merge)]
    [InlineData("autonomous", SquadToolAction.Migration)]
    [InlineData("autopilot", SquadToolAction.SecretRotation)]
    [InlineData("autonomous", SquadToolAction.Deletion)]
    [InlineData("autopilot", SquadToolAction.Compliance)]
    [InlineData("autonomous", SquadToolAction.CostConfirmation)]
    [InlineData("autopilot", SquadToolAction.Irreversible)]
    public async Task GivenRegisteredScript_WhenSpecificApprovalIsDenied_NoModeGrantsItsEffect(string mode, SquadToolAction action)
    {
        using var host = new RuntimeTestHost();
        host.Options.Mode = mode;
        host.Options.MaxConcurrentDispatches = 1;
        await RegisterAsync(host, action: action);
        host.Options.ApproveAsync = (approval, _) =>
        {
            host.Approvals.Add(approval);
            return ValueTask.FromResult(approval.Kind != SquadApprovalKind.ConsequentialTool);
        };
        QueuePrework(host);
        QueueScript(host);
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the original bounded source fixture.");

        Assert.True(result.Status == SquadRunStatus.ApprovalDenied, result.ResponseText);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
        Assert.Single(host.Approvals, item => item.Kind == SquadApprovalKind.ConsequentialTool && item.SkillScript is not null);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("charter")]
    [InlineData("filter")]
    public async Task GivenUnregisteredRolePermissionOrSkill_WhenScriptIsInvented_NoExecutorIsExposed(string denial)
    {
        using var host = new RuntimeTestHost();
        var registration = await RegisterAsync(host);
        if (denial == "role")
        {
            host.Options.Tools[0] = registration with { AllowedRoles = ["technical-writer"] };
        }
        else if (denial == "charter")
        {
            host.WriteCharter("developer", tools: "read");
        }
        else
        {
            host.Options.SkillFilter = (_, skill) => skill.Name != "specialist-test";
        }
        QueuePrework(host);
        host.Client.Call(AgentSkillsProvider.RunSkillScriptToolName, new()
        {
            ["skillName"] = "specialist-test",
            ["scriptName"] = ScriptPath,
            ["arguments"] = new { value = "not authorized" },
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.Unsupported, result.Status);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("entry")]
    [InlineData("resource")]
    [InlineData("executor")]
    [InlineData("input-scope")]
    [InlineData("output-scope")]
    [InlineData("attestation")]
    [InlineData("duplicate")]
    public async Task GivenInvalidTrustedBinding_WhenCreatingRuntime_RejectsBeforeAnyProviderCall(string defect)
    {
        using var host = new RuntimeTestHost();
        var registration = await RegisterAsync(host);
        var binding = registration.SkillScript!;
        host.Options.Tools[0] = defect switch
        {
            "release" => registration with { SkillScript = binding with { Release = new("v0.18.1", new string('c', 40)) } },
            "entry" => registration with { SkillScript = binding with { SkillSha256 = new string('0', 64) } },
            "resource" => registration with { SkillScript = binding with { ResourceSha256 = new string('0', 64) } },
            "executor" => registration with { SkillScript = binding with { ExecutorBinding = "secret=https://not-a-binding" } },
            "input-scope" => registration with { ProjectReadPaths = null },
            "output-scope" => registration with { ProjectWritePaths = null },
            "attestation" => registration with { OutputEvidence = null },
            _ => registration,
        };
        if (defect == "duplicate")
        {
            host.Options.Tools.Add(registration with
            {
                Function = AIFunctionFactory.Create(() => "never", "another_executor", "Not a second canonical binding."),
            });
        }
        await Assert.ThrowsAnyAsync<Exception>(() => host.CreateAsync());
        Assert.Empty(host.Client.Calls);
    }

    [Theory]
    [InlineData("scripts/../fixture.ps1")]
    [InlineData("scripts\\fixture.ps1")]
    [InlineData("scripts//fixture.ps1")]
    [InlineData("references/note.md")]
    [InlineData("scripts/missing.ps1")]
    public async Task GivenGuessedOrTraversingResource_WhenBinding_RejectsIt(string resource)
    {
        using var host = new RuntimeTestHost();
        Fixture(host);
        var roots = await host.Source().ResolveAsync();
        Assert.ThrowsAny<Exception>(() => SquadSkillScriptBinding.Create(roots, "specialist-test", resource, "fixture-v1"));
    }

    [Theory]
    [InlineData("scripts/Measure-SquadLedger.ps1")]
    [InlineData("scripts/Resolve-SquadModelRoute.ps1")]
    public async Task GivenCopilotStateScript_WhenRegistering_RequiresExistingDeterministicNativeEquivalent(string resource)
    {
        using var host = new RuntimeTestHost();
        host.Write("skills/apm-renamed-squad/" + resource, "Original fixture of a foreign state script.");
        var roots = await host.Source().ResolveAsync();
        var error = Assert.Throws<InvalidDataException>(() => SquadSkillScriptBinding.Create(roots, "squad", resource, "state-v1"));
        Assert.Contains("native", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(host.Project, host.Options.SquadRoot)));
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("script")]
    [InlineData("alias")]
    public async Task GivenAvailableScript_WhenModelGuessesAnotherIdentity_RejectsWithoutExecuting(string identity)
    {
        using var host = new RuntimeTestHost();
        await RegisterAsync(host);
        QueuePrework(host);
        host.Client.Call(identity == "alias" ? "registered_source" : AgentSkillsProvider.RunSkillScriptToolName, new()
        {
            ["skillName"] = identity == "skill" ? "not-installed" : "specialist-test",
            ["scriptName"] = identity == "script" ? "scripts/not-registered.ps1" : ScriptPath,
            ["arguments"] = new { value = "not authorized" },
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Theory]
    [InlineData("fail", SquadToolOutputStatus.Failed)]
    [InlineData("dry", SquadToolOutputStatus.DryRun)]
    public async Task GivenActualNonAppliedProcess_WhenMafReturnsIt_PreservesFailureReceiptWithoutDelivery(string mode, SquadToolOutputStatus status)
    {
        using var host = new RuntimeTestHost();
        await RegisterAsync(host, mode);
        QueuePrework(host);
        QueueScript(host);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Equal(status, Assert.Single(result.Effects!).Receipt.Status);
        Assert.Empty(Assert.Single(result.Effects!).Receipt.Outputs!);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
        Assert.DoesNotContain(result.Evidence, evidence => evidence.Stage == "review");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenAuthenticatedScriptGate_WhenRestarting_BindsOriginalInputAndActualExecutor(bool changedInput)
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "input.txt"), "original input ");
        var registration = await RegisterAsync(host, input: "input.txt");
        using var notification = RuntimeModeTests.Durable(host);
        SquadRunResult suspended;
        using (var first = await host.CreateAsync())
        {
            suspended = await SuspendAsync(host, first);
            var pending = Assert.Single(suspended.PendingHumanActions!, action => action.Status == "pending");
            var payload = await first.ReadHumanActionAsync(pending.Binding.Id);
            Assert.Equal(registration.SkillScript, payload.Approval!.SkillScript!.Binding);
            Assert.Equal(new ProjectFiles(host.Project, 200_000).HashFile("input.txt"),
                Assert.Single(payload.Approval.SkillScript.Inputs).Sha256);
        }
        using var next = await host.CreateAsync();
        var action = Assert.Single(await next.GetPendingHumanActionsAsync(), action => action.Status == "pending");
        if (changedInput)
        {
            File.WriteAllText(Path.Combine(host.Project, "input.txt"), "changed input ");
            var denied = await next.SubmitHumanResponseAsync(SquadHumanResponse.FromCommand(action, "/approve", "changed-input"));
            Assert.False(denied.Accepted);
            Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
            return;
        }
        _ = await RuntimeModeTests.RespondAsync(next, "consequentialtool");
        QueueScript(host);
        QueueCompletion(host);
        var result = await next.ResumeAsync(suspended.RunId);
        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal("original input original actual output", File.ReadAllText(Path.Combine(host.Project, "implementation.txt")));
        Assert.Single(result.Effects!);
        Assert.DoesNotContain("input.txt", notification.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("original input", notification.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenChangedCallbackInput_WhenApprovingScript_StopsBeforeProcessLaunch()
    {
        using var host = new RuntimeTestHost();
        File.WriteAllText(Path.Combine(host.Project, "input.txt"), "original input");
        await RegisterAsync(host, input: "input.txt");
        host.Options.ApproveAsync = (approval, _) =>
        {
            if (approval.Kind == SquadApprovalKind.ConsequentialTool)
            {
                File.WriteAllText(Path.Combine(host.Project, "input.txt"), "changed by host while waiting");
            }
            return ValueTask.FromResult(true);
        };
        QueuePrework(host);
        QueueScript(host);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("scope changed", result.ResponseText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Fact]
    public async Task GivenChangedExecutorRevision_WhenResuming_DoesNotInvokeAnotherProcess()
    {
        using var host = new RuntimeTestHost();
        var registration = await RegisterAsync(host);
        using var notification = RuntimeModeTests.Durable(host);
        SquadRunResult suspended;
        using (var first = await host.CreateAsync())
        {
            suspended = await SuspendAsync(host, first);
        }
        host.Options.Tools[0] = registration with { SkillScript = registration.SkillScript! with { ExecutorBinding = "changed-v2" } };
        var calls = host.Client.Calls.Count;
        using var next = await host.CreateAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => next.ResumeAsync(suspended.RunId));
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Fact]
    public async Task GivenActualPartialEffect_WhenExecutorCannotReturn_RestartRequiresReconciliationWithoutReplay()
    {
        using var host = new RuntimeTestHost();
        var registration = await RegisterAsync(host);
        host.Options.Tools[0] = registration with
        {
            Function = AIFunctionFactory.Create(async (string value, CancellationToken token) =>
            {
                _ = await registration.Function.InvokeAsync(new AIFunctionArguments { ["value"] = value }, token);
                throw new IOException("Original interruption after actual process output, before the host returned.");
            }, "registered_source", "Bounded actual interruption."),
        };
        QueuePrework(host);
        QueueScript(host);
        SquadRunResult failed;
        using (var runtime = await host.CreateAsync())
        {
            failed = await runtime.RunAsync("Implement the original bounded source fixture.");
            Assert.Equal(SquadRunStatus.Failed, failed.Status);
        }
        var bytes = File.ReadAllBytes(Path.Combine(host.Project, "implementation.txt"));
        var calls = host.Client.Calls.Count;
        using var next = await host.CreateAsync();
        var result = await next.ResumeAsync(failed.RunId);
        Assert.Equal(SquadRunStatus.ReconciliationRequired, result.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(host.Project, "implementation.txt")));
    }

    [Fact]
    public async Task GivenFailedProcessWithActualPartialWrite_WhenRestarting_RequiresReconciliationAndRetainsBytes()
    {
        using var host = new RuntimeTestHost();
        await RegisterAsync(host, mode: "partial");
        QueuePrework(host);
        QueueScript(host);
        SquadRunResult failed;
        using (var first = await host.CreateAsync())
        {
            failed = await first.RunAsync("Implement the original bounded source fixture.");
            Assert.Equal(SquadRunStatus.Failed, failed.Status);
            Assert.Contains("partial bytes", failed.ResponseText, StringComparison.Ordinal);
        }
        Assert.Equal("original actual output", File.ReadAllText(Path.Combine(host.Project, "implementation.txt")));
        var calls = host.Client.Calls.Count;
        using var next = await host.CreateAsync();
        var result = await next.ResumeAsync(failed.RunId);
        Assert.Equal(SquadRunStatus.ReconciliationRequired, result.Status);
        Assert.Equal(calls, host.Client.Calls.Count);
    }

    [Fact]
    public async Task GivenIdempotentScriptPostcondition_WhenHostReconciles_ResumesWithoutRepeatingTheActualProcess()
    {
        using var host = new RuntimeTestHost();
        var registration = await RegisterAsync(host);
        var processes = 0;
        var reconciliations = 0;
        host.Options.Tools[0] = registration with
        {
            SkillScript = registration.SkillScript! with { ExecutorBinding = "idempotent-fixture-interruption-v1" },
            Function = AIFunctionFactory.Create(async (string value, CancellationToken token) =>
            {
                processes++;
                _ = await registration.Function.InvokeAsync(new AIFunctionArguments { ["value"] = value }, token);
                throw new IOException("Original interruption after actual idempotent process write.");
            }, "registered_source", "Bounded idempotent process postcondition."),
            ReconcileAsync = (request, token) =>
            {
                token.ThrowIfCancellationRequested();
                reconciliations++;
                Assert.Equal("idempotent-fixture-interruption-v1", request.SkillScript!.Binding.ExecutorBinding);
                Assert.Equal(["implementation.txt"], request.SkillScript.ProjectWritePaths);
                var expected = request.Arguments.GetProperty("value").GetString()!;
                var actual = File.ReadAllBytes(Path.Combine(host.Project, "implementation.txt"));
                Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected), actual);
                return ValueTask.FromResult(new SquadEffectReconciliation(SquadEffectDisposition.Executed,
                    new(["implementation.txt"], "Actual unchanged idempotent file postcondition verified."),
                    JsonSerializer.SerializeToElement(expected)));
            },
        };
        QueuePrework(host);
        QueueScript(host);
        SquadRunResult failed;
        using (var first = await host.CreateAsync())
        {
            failed = await first.RunAsync("Implement the original bounded source fixture.");
            Assert.Equal(SquadRunStatus.Failed, failed.Status);
        }
        using var next = await host.CreateAsync();
        var reconciled = await next.ReconcileAsync(failed.RunId);
        Assert.Equal(SquadRunStatus.ApprovalRequired, reconciled.Status);
        QueueScript(host);
        QueueCompletion(host);

        var result = await next.ResumeAsync(failed.RunId);

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(1, processes);
        Assert.Equal(1, reconciliations);
        Assert.NotNull(Assert.Single(result.Effects!).Receipt.SkillScript);
    }

    [Theory]
    [InlineData(SquadToolEffect.External)]
    [InlineData(SquadToolEffect.ExternalRead)]
    public async Task GivenOnlyAProcessReceipt_WhenExternalMutationIsClaimed_RequiresActualResourceProof(SquadToolEffect effect)
    {
        using var host = new RuntimeTestHost();
        var registration = await RegisterAsync(host);
        host.Options.Tools[0] = registration with
        {
            Effect = effect,
            Impact = SquadToolImpact.Impactful,
            ProjectWritePaths = null,
        };
        QueuePrework(host);
        QueueScript(host);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.Failed, result.Status);
        Assert.Contains("Process exit alone cannot attest external retrieval or mutation", result.ResponseText, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Effects ?? [], item => item.Receipt.Effect == effect);
        var resumed = await runtime.ResumeAsync(result.RunId);
        Assert.Equal(SquadRunStatus.ReconciliationRequired, resumed.Status);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("sibling")]
    public async Task GivenPrivateOrOffOwnerScriptInput_WhenBindingDispatch_RejectsBeforeExecution(string target)
    {
        using var host = new RuntimeTestHost();
        var input = target == "state" ? host.Options.SquadRoot + "/state.json" : "private-input.txt";
        if (target != "state")
        {
            File.WriteAllText(Path.Combine(host.Project, input), "not granted to a member");
            host.Options.SquadRoot = ".copilot-tracking/squad/members/one";
        }
        var output = target == "sibling" ? "outputs/implementation.txt" : "implementation.txt";
        await RegisterAsync(host, input: input, output: output);
        QueuePrework(host);
        QueueScript(host);
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains(target == "sibling" ? "Member reads" : "not dispatch inputs", result.ResponseText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host.Project, output)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenRunningProcess_WhenCancelled_TerminatesActualParentAndChildren(bool child)
    {
        using var host = new RuntimeTestHost();
        var script = Fixture(host);
        var executor = new SquadProcessExecutor(PowerShell(), host.Project, "cancel-v1", TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SquadProcessResult> execution;
        using (var watcher = new FileSystemWatcher(host.Project, "ready.pid"))
        {
            watcher.Created += (_, _) => ready.TrySetResult();
            watcher.Renamed += (_, _) => ready.TrySetResult();
            watcher.EnableRaisingEvents = true;
            execution = executor.ExecuteAsync(Invocation(script, host.Project, child ? "spawn" : "wait", output: "ready.pid"), cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            watcher.EnableRaisingEvents = false;
        }
        var ids = File.ReadAllText(Path.Combine(host.Project, "ready.pid")).Split(',')
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(child ? 2 : 1, ids.Length);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(process.HasExited);
            }
            catch (ArgumentException)
            {
                // An already-reaped process no longer has an OS identity.
            }
        }
    }

    [Fact]
    public async Task GivenRunningProcess_WhenTimeoutExpires_ReportsFailureInsteadOfOutputSuccess()
    {
        using var host = new RuntimeTestHost();
        var script = Fixture(host);
        var executor = new SquadProcessExecutor(PowerShell(), host.Project, "timeout-v1", TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SquadProcessExecutionException>(() =>
            executor.ExecuteAsync(Invocation(script, host.Project, "wait", output: "ready.pid")));
        Assert.Contains("timeout", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(13), watch.Elapsed.ToString());
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("oversize-error")]
    public async Task GivenExcessiveActualStdout_WhenBoundIsExceeded_StopsWithoutPublishingAFile(string mode)
    {
        using var host = new RuntimeTestHost();
        var script = Fixture(host);
        var executor = new SquadProcessExecutor(PowerShell(), host.Project, "output-bound-v1", TimeSpan.FromSeconds(30), maximumOutputCharacters: 64);
        var invocation = Invocation(script, host.Project, mode) with { StandardOutputPath = "captured.txt" };
        var error = await Assert.ThrowsAsync<SquadProcessExecutionException>(() => executor.ExecuteAsync(invocation));
        Assert.Contains("output bound", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host.Project, "captured.txt")));
    }

    [Fact]
    public async Task GivenDefaultProcessEnvironment_WhenRunning_DoesNotInheritHostCredentials()
    {
        using var host = new RuntimeTestHost();
        var script = Fixture(host);
        var previous = Environment.GetEnvironmentVariable("HVE_SQUAD_PRIVATE_TEST_VALUE");
        Environment.SetEnvironmentVariable("HVE_SQUAD_PRIVATE_TEST_VALUE", "original private test value");
        try
        {
            var executor = new SquadProcessExecutor(PowerShell(), host.Project, "environment-v1", TimeSpan.FromSeconds(10));
            var result = await executor.ExecuteAsync(Invocation(script, host.Project, "environment") with { ProjectOutputs = [] });
            Assert.Equal("absent", result.StandardOutput.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVE_SQUAD_PRIVATE_TEST_VALUE", previous);
        }
    }

    [Theory]
    [InlineData(".copilot-tracking/squad/state.json")]
    [InlineData(".copilot-tracking/squad/members/other/history/private.md")]
    [InlineData(".git/config")]
    [InlineData("../escape.txt")]
    public async Task GivenWriterOwnedOrEscapingProcessOutput_WhenExecuting_RejectsBeforeLaunch(string path)
    {
        using var host = new RuntimeTestHost();
        var script = Fixture(host);
        var executor = new SquadProcessExecutor(PowerShell(), host.Project, "scope-v1", TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            executor.ExecuteAsync(Invocation(script, host.Project, "write") with { ProjectOutputs = [path] }));
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Fact]
    public void GivenMissingOrGuessedExecutable_WhenConstructingReferenceExecutor_RejectsIt() =>
        Assert.Throws<ArgumentException>(() => new SquadProcessExecutor("pwsh", ".", "not-guessed-v1", TimeSpan.FromSeconds(1)));

    [Fact]
    public async Task GivenInvalidExistingExecutable_WhenStarting_SurfacesAnExplicitProcessFailure()
    {
        using var host = new RuntimeTestHost();
        var executable = Path.Combine(host.Project, "not-an-executable");
        File.WriteAllText(executable, "Original text, not an executable.");
        var executor = new SquadProcessExecutor(executable, host.Project, "invalid-binary-v1", TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<SquadProcessExecutionException>(() => executor.ExecuteAsync(new([], [])));
        Assert.Contains("could not start", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenSuccessfulExitWithoutDeclaredOutput_WhenAttesting_RejectsTheMissingEffect()
    {
        using var host = new RuntimeTestHost();
        var script = Fixture(host);
        var executor = new SquadProcessExecutor(PowerShell(), host.Project, "missing-output-v1", TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<SquadProcessExecutionException>(() =>
            executor.ExecuteAsync(Invocation(script, host.Project, "dry")));
        Assert.Contains("declared output", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Fact]
    public async Task GivenRegisteredScriptSchemas_WhenLoadingSkill_ContentStillRespectsTheConfiguredReadBound()
    {
        using var host = new RuntimeTestHost();
        var artifacts = HveSquad.AgentFramework.Artifacts.SquadArtifactLoader.Load(host.Artifacts);
        var skill = Assert.Single(artifacts.Skills, skill => skill.Name == "specialist-test");
        var function = AIFunctionFactory.Create((string value) => value, "original_function", "Original registered schema.");
        var scripts = Enumerable.Range(0, 64).ToDictionary(index => "scripts/original-" + index + ".ps1",
            _ => function, StringComparer.Ordinal);
        Assert.True(File.ReadAllText(skill.SourcePath).Length < 1024);
        var mapped = new MappedFileSkill(skill, 1024, scripts);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => mapped.GetContentAsync().AsTask());
        Assert.Contains("read limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GivenChangedImportedResource_WhenScriptIsRequested_VerifiesTheWholeExactReleaseBeforeLaunch()
    {
        using var host = new RuntimeTestHost();
        host.Write("skills/apm-renamed-specialist/scripts/imported.psm1", "Original immutable helper.");
        await RegisterAsync(host);
        QueuePrework(host);
        host.Client.Steps.Enqueue((_, _) =>
        {
            host.Write("skills/apm-renamed-specialist/scripts/imported.psm1", "Changed helper after planning.");
            return RuntimeTestHost.Function(AgentSkillsProvider.RunSkillScriptToolName, new()
            {
                ["skillName"] = "specialist-test",
                ["scriptName"] = ScriptPath,
                ["arguments"] = new { value = "not authorized" },
            });
        });
        using var runtime = await host.CreateAsync();
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.Blocked, result.Status);
        Assert.Contains("release artifacts changed", result.ResponseText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Fact]
    public async Task GivenTooManyProjectedScriptInputs_WhenDispatching_EnforcesTheExactArtifactBound()
    {
        using var host = new RuntimeTestHost();
        host.UseCurrentPlanContract();
        host.Options.MaxArtifactsPerDispatch = 2;
        File.WriteAllText(Path.Combine(host.Project, "one.txt"), "one");
        File.WriteAllText(Path.Combine(host.Project, "two.txt"), "two");
        File.WriteAllText(Path.Combine(host.Project, "three.txt"), "three");
        var registration = await RegisterAsync(host, tag: "v0.18.1");
        host.Options.Tools[0] = registration with { ProjectReadPaths = _ => ["one.txt", "two.txt", "three.txt"] };
        QueuePrework(host);
        QueueScript(host);
        using var runtime = await host.CreateAsync("v0.18.1");
        var result = await runtime.RunAsync("Implement the original bounded source fixture.");
        Assert.Equal(SquadRunStatus.LimitReached, result.Status);
        Assert.Contains("MaxArtifactsPerDispatch", result.ResponseText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host.Project, "implementation.txt")));
    }

    [Fact]
    public async Task GivenOptionalProcessTrace_WhenExecuting_EmitsNoArgumentsPrivateOutputOrEnvironmentAttributes()
    {
        using var host = new RuntimeTestHost();
        var script = Fixture(host);
        using var source = new ActivitySource("Original.ProcessFixture");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var executor = new SquadProcessExecutor(PowerShell(), host.Project, "trace-v1", TimeSpan.FromSeconds(10), activitySource: source);
        var result = await executor.ExecuteAsync(Invocation(script, host.Project, "write", "original private fixture output"));
        var span = Assert.Single(spans);
        Assert.Equal("run.skill_script", span.OperationName);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Empty(span.TagObjects);
        Assert.Empty(span.Events);
        Assert.Contains("original private fixture output", result.StandardOutput, StringComparison.Ordinal);
        using var stillOwned = source.StartActivity("host.owned");
        Assert.NotNull(stillOwned);
    }

    [Theory]
    [InlineData("output-status")]
    [InlineData("receipt-kind")]
    [InlineData("provider-receipt")]
    [InlineData("exit-code")]
    [InlineData("pid")]
    [InlineData("contradiction")]
    public void GivenIncompleteSerializedProcessResult_WhenAttesting_RejectsInsteadOfDefaultingToSuccess(string defect)
    {
        var result = new Dictionary<string, object?>
        {
            ["processId"] = 42,
            ["exitCode"] = 0,
            ["standardOutput"] = "original stdout",
            ["standardError"] = "",
            ["output"] = new Dictionary<string, object?>
            {
                ["projectPaths"] = Array.Empty<string>(),
                ["receipt"] = "actual fixture receipt",
                ["status"] = "Succeeded",
                ["receiptKind"] = "Process",
            },
        };
        if (defect is "output-status" or "receipt-kind")
        {
            ((Dictionary<string, object?>)result["output"]!).Remove(defect == "output-status" ? "status" : "receiptKind");
        }
        else if (defect == "provider-receipt")
        {
            ((Dictionary<string, object?>)result["output"]!)["receiptKind"] = "HostObservation";
        }
        else if (defect == "contradiction")
        {
            result["exitCode"] = 7;
        }
        else
        {
            result.Remove(defect == "pid" ? "processId" : "exitCode");
        }
        Assert.ThrowsAny<Exception>(() => SquadProcessExecutor.GetOutputEvidence(JsonSerializer.SerializeToElement(result)));
    }

    [Fact]
    public void GivenCompleteSerializedProcessResult_WhenAttesting_PreservesActualOutputAndProcessOnlyAuthority()
    {
        var expected = new SquadProcessResult(42, 0, "original stdout", "",
            new([], "actual fixture process receipt", ReceiptKind: SquadToolReceiptKind.Process));
        var actual = SquadProcessExecutor.GetOutputEvidence(JsonSerializer.SerializeToElement(expected));
        Assert.Equal(expected.Output.Receipt, actual.Receipt);
        Assert.Equal(expected.Output.Status, actual.Status);
        Assert.Empty(actual.ProjectPaths);
        Assert.Equal(SquadToolReceiptKind.Process, actual.ReceiptKind);
    }

    [Fact]
    public async Task GivenBoundedSecondScriptDraft_WhenRevalidating_PreservesOriginalBytesAndActualEffectOwnership()
    {
        using var host = new RuntimeTestHost();
        await RegisterAsync(host);
        host.Options.Mode = "autonomous";
        host.Options.MaxConcurrentDispatches = 1;
        host.QueuePipeline();
        host.QueueStage("researcher");
        host.QueueStage("lead");
        RuntimeModeTests.Council(host, verdict: "Conditional", condition: true);
        QueueScript(host, "first actual bytes");
        QueueProducingCompletion(host);
        RuntimeModeTests.Council(host, verdict: "Conditional", condition: true);
        QueueScript(host, "second actual bytes");
        QueueProducingCompletion(host);
        RuntimeModeTests.Council(host, verdict: "Conditional", condition: true, resolve: true);
        host.QueueStage("tester");
        host.Client.Text("Bounded script revisions completed.");
        using var runtime = await host.CreateAsync();

        var result = await runtime.RunAsync("Implement the original bounded source fixture.");

        Assert.True(result.Status == SquadRunStatus.Completed, result.ResponseText);
        Assert.Equal(2, result.ModeOutcome!.Iterations.Count);
        Assert.Equal("second actual bytes", File.ReadAllText(Path.Combine(host.Project, "implementation.txt")));
        var state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(Path.Combine(host.Project, host.Options.SquadRoot, "state.json")))!;
        var revision = Assert.Single(state.OutputRevisions!);
        Assert.Equal("first actual bytes", File.ReadAllText(Path.Combine(host.Project, revision.ArchivePath)));
        Assert.Equal(2, result.Effects!.Count);
        Assert.NotEqual(result.Effects[0].DispatchId, result.Effects[1].DispatchId);
        Assert.All(result.Effects, effect => Assert.NotNull(effect.Receipt.SkillScript));
        Assert.Single(host.Approvals, approval => approval.Kind == SquadApprovalKind.AutonomousOutcome);
    }

    [Fact]
    public async Task GivenRegisteredReferenceDemo_WhenExecuting_UsesActualMafAndProcessWithoutPaidModels()
    {
        using var host = new RuntimeTestHost();
        host.Write("skills/apm-renamed-pr-reference/SKILL.md", "---\nname: pr-reference\n---\nOriginal synthetic read skill.");
        host.Write("skills/apm-renamed-pr-reference/" + SkillExecutorDemo.ResourcePath,
            "param([string]$InputPath, [switch]$Info)\n[Console]::Out.WriteLine([IO.File]::ReadAllText($InputPath))\n");
        var command = SampleCommand.Parse(["--skill-executor-demo", PowerShell(), "--project", host.Project]);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var status = await SkillExecutorDemo.RunAsync(command, default,
            new HveSquad.AgentFramework.Sources.DirectoryArtifactSource([host.Artifacts]), output);
        Assert.Equal(0, status);
        Assert.Contains("ResearchCompleted", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("original synthetic input", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "ReleaseCompatibility")]
    public async Task GivenActualAcquiredRelease_WhenExecutingRegisteredReference_UsesItsRealScriptAndExactProvenance()
    {
        var version = Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_VERSION");
        if (string.IsNullOrWhiteSpace(version))
        {
            Assert.NotEqual("true", Environment.GetEnvironmentVariable("HVE_SQUAD_REQUIRE_RELEASE_TESTS"));
            return;
        }
        using var releaseHttp = ReleaseTestHttpClient.Create(Environment.GetEnvironmentVariable("HVE_SQUAD_RELEASE_GITHUB_TOKEN"));
        var source = ReleaseTestHttpClient.Source(version, releaseHttp);
        var roots = await source.ResolveAsync();
        var tag = roots.Release!.Tag;
        using var host = new RuntimeTestHost();
        var command = SampleCommand.Parse(["--skill-executor-demo", PowerShell(), "--project", host.Project, "--version", tag]);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var exit = await SkillExecutorDemo.RunAsync(command, default, source, output);

        Assert.Equal(0, exit);
        var state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(
            Path.Combine(host.Project, command.SquadRoot, "state.json")))!;
        var evidence = Assert.Single(Assert.Single(state.Runs).Evidence);
        var script = Assert.Single(evidence.ExecutedHostTools!);
        Assert.Equal(roots.Release, script.SkillScript!.Binding.Release);
        Assert.Equal("pr-reference", script.SkillScript.Binding.SkillName);
        Assert.Equal(SkillExecutorDemo.ResourcePath, script.SkillScript.Binding.ResourcePath);
        Assert.Equal(SquadToolReceiptKind.Process, script.ReceiptKind);
        Assert.Equal(SquadToolOutputStatus.Succeeded, script.Status);
        Assert.Contains("exit-code:0", script.Receipt, StringComparison.Ordinal);
        Assert.Single(script.SkillScript.Inputs);
        Assert.Contains("Total lines:", File.ReadAllText(Path.Combine(host.Project, evidence.ArtifactPath)), StringComparison.Ordinal);
        Assert.DoesNotContain("executor-input", output.ToString(), StringComparison.Ordinal);
    }
}
