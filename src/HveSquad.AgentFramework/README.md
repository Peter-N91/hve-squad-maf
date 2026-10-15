---
title: HVE Squad for Microsoft Agent Framework
description: Embed release-pinned native HVE Squad workflows in a consumer-owned .NET application.
---

## Package status

`HveSquad.AgentFramework` is independently versioned **0.1.0-preview.1** and is not
published to NuGet.org by this work. Use a reviewed local package feed. The package
targets .NET 10 and uses Microsoft Agent Framework 1.22.0 and
Microsoft.Extensions.AI 10.10.0.

The supported official methodology is HVE Squad **v0.18.1** at
`4c1f140f12f17abd3f200baa3afcb6d4c84bb6da`, with HVE Core dependency
`18f5ac75a1556e49f1e90647a4816d17499e1d7f`.
`SupportedSquadRelease.Current` reads the embedded exact identity and all 279
assertion rows. The inventory is not a claim of completed conformance; individual
release/platform acceptance remains in progress.

The package contains native code, this README, the MIT license and the supported
manifest. It does not redistribute upstream agent charters, customer state or
credentials. Released artifacts are acquired with Git/APM or supplied by an
explicit host-managed complete artifact source.

## Consumer-owned host

Register your existing `Microsoft.Extensions.AI.IChatClient`, then call
`AddHveSquad` from `HveSquad.AgentFramework.Hosting`. Its transient
`SquadRuntimeFactory.CreateAsync` asynchronously acquires artifacts and creates an
independently owned runtime. The application retains client ownership.

```csharp
services.AddSingleton<IChatClient>(chatClient);
services.AddHveSquad(
    configuration.GetSection("HveSquad"),
    configureHost: options =>
    {
        options.ProjectPath = authorizedCaseDirectory;
        options.ApproveAsync = approveExactProposalAsync;
        options.AskAsync = askAuthorizedHumanAsync;
    });

var factory = serviceProvider.GetRequiredService<SquadRuntimeFactory>();
using var runtime = await factory.CreateAsync(cancellationToken);
var result = await runtime.RunAsync(request, cancellationToken);
```

The example variables are application integrations, not built-in authentication.
Provide tenant/case rights, isolated durable storage, ingestion, model bindings,
queues, cancellation, human UI and secure result delivery. Use one active owner
per native root. Do not pass arbitrary browser paths or model-asserted permission
into the host. Await execution before disposing the runtime.

Runtime JSON accepts allowlisted data settings, never callbacks, tools, process
executors, renderers, principals, artifact sources, memory rights or connectors.
Register those capabilities in trusted code. A successful tool return must attest
real output/resource postconditions; process exit alone cannot prove an external
action. The library is not an OS sandbox or a hosted service.

## Native entry points

* `SquadRuntime.RunAsync` supports interactive, bounded autonomous and full
  autopilot workflows with discovery, independent intake, task-fit councils,
  verified plan-owned producing waves and independent review.
* An optional authenticated `HumanApprovalChannel` suspends exact gates.
  Recreate the original host, submit/poll the actual response and explicitly call
  `ResumeAsync(runId)`. Notifications and prepared hashes are not consent.
* `SquadRuntimeFactory.CreateFederationAsync` provides confirmed native
  Init/Promotion/Expansion and actual dependency-ordered member execution.
  Federation is in-repository, not an implicit Copilot-state import.
* `CreateReports`, `CreateMemory` and `CreateLearning` on the factory require no
  chat-client registration or artifact acquisition. Each helper still needs its
  separate host-owned case/source/human authorization and existing native state.
* `SquadLocalDocumentRenderer` optionally produces actual local OOXML Word and
  bounded WinAnsi PDF. Unsupported PDF content has an explicit Markdown fallback;
  a fallback is not the requested format.
* Consumer-local Scribe memory is separate from model-context disclosure and
  human-only learning. No connector means prepared-only learning. Every actual
  provider mutation requires its own authenticated approval and independent
  resource rights; uncertain effects require read-only reconciliation, not replay.
* Authorized Watch handling requires authenticated event opt-in, durable original
  state and separately registered terminal rights. It is not an installed
  always-on service or permission inferred from event text.

Inspect `SquadRunResult.Status`, partial evidence and diagnostics. Focused research,
plan and review have distinct statuses. `Completed` is not independent business
validation, acceptance, publication, deployment or a guarantee of domain accuracy.

## Release maintenance and verification

Production defaults use the exact packaged pin. Explicit `latest` is candidate
evaluation, not promotion. Scheduled read-only drift inspection and separately
privileged draft upgrade proposals require actual candidate-head checks and human
review. They never auto-merge, publish or deploy, or silently migrate an existing
release-bound case.

From the source repository, `scripts\Test-PackageConsumer.ps1` packs this library
and restores/builds/runs a new consumer outside the checkout with package-source
mapping, isolated dependencies and no source-project references. It verifies
deterministic package timestamps, byte-identical unchanged-assembly repacking,
actual DLL bytes, same-run durable resume, MAF outputs, a federation member,
four local formats, memory and human-only discovery using scripted responses.
No paid model, customer data, remote mutation or product-quality claim is involved.

Use the [bilingual setup and API documentation](https://peter-n91.github.io/hve-squad-maf/getting-started.html)
when its reviewed Pages deployment is available, or the
[authoritative documentation sources](https://github.com/Peter-N91/hve-squad-maf/tree/main/docs).
The [release maintenance reference](https://github.com/Peter-N91/hve-squad-maf/blob/main/tools/HveSquad.ReleaseSync/README.md)
describes proposal permissions, workflow approval, exact-head status and reviewed
pin promotion.

MIT. Community project, not affiliated with or endorsed by Microsoft. Upstream
artifacts retain their own licenses. Review application security, privacy, tenant
isolation and domain/model quality separately before production use.
