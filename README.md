# HVE Squad for Microsoft Agent Framework

<img src="docs/assets/logo.svg" width="80" height="80" alt="HVE Squad MAF: connected framework nodes around a coordinator">

Embed [HVE Squad](https://github.com/Peter-N91/hve-squad) in your own .NET AI
application so end users can do evidence-backed business work: prepare an RFP
response, compare customer solution options, or draft a reviewed knowledge article.
Built on native Microsoft Agent Framework, this is a library intended for NuGet
consumption, not a Copilot development plugin or an application-building assistant.
Version **0.1.0-preview.1 is unpublished**; use source or a local feed today.

It runs real MAF agents using released HVE charters, skills, and instructions.
The supported app-facing subset is advisory document work with research, planning,
one producing owner, human gates, and review. Existing `technical-writer`, `analyst`,
and `product-owner` specialists are not newly shipped sales, insurance, finance, or
support expert personas. Approved domain evidence grounds them; it does not
automatically confer domain expertise.

Start with the [RFP response workbench](docs/use-cases.html#rfp), including a
[complete `BidDraftService.DraftAsync` method](docs/use-cases.html#rfp-code).
For publication rather than local consumption, see the
[maintainer publishing procedure](docs/maintaining.html#publishing).
The library does not call the HVE Squad MCP service, fork Agent Framework, or
redistribute upstream charters in its package.

## Documentation site

The static GitHub Pages site is ready in `docs/`:
[overview](docs/index.html), [getting started](docs/getting-started.html),
[runtime reference](docs/usage.html), [hosting](docs/hosting.html),
[use cases](docs/use-cases.html), [federation](docs/federation.html), and
[contributing and maintaining](docs/maintaining.html).

**No separate squad server is required.** This is a .NET library distributed as a
NuGet package, not a Copilot plugin. It executes inside the consumer's MAF application.
That application supplies its model client, authentication, document ingestion and
rights checks, isolated case storage, human approval UI, job lifecycle, and any
retrieval or external tools. GitHub Pages hosts documentation only, never the agent
runtime. Optional service hosting and future NuGet publication are explained in the
[maintainer guide](docs/maintaining.html#publishing), not presented as shipped infrastructure.

To publish the site after reviewing and pushing these changes, set the repository's
**Settings > Pages > Source** to **GitHub Actions** and run **Deploy documentation**
from `main`. The actual workflow deployment output supplies the URL; adding these
files alone does not make the site live.

## Supported behavior

| Capability | Native implementation |
| --- | --- |
| Simple setup | `SquadRuntime.CreateAsync`, or `AddHveSquad` with configuration and the application's existing `IChatClient` |
| Release alignment | Defaults to GitHub's latest published stable release, resolved to an exact commit before installation |
| Roles and profiles | Reads the release's cast, profiles, packs and selection cues; rejects missing, disabled or off-roster agents |
| Advisory delivery | Research, plan with phase details, conditional intake/council, one producing role, then review; native Markdown output and evidence |
| Focused requests | Research-only, plan-only and review-only runs have distinct completion statuses |
| Skills | Native `AgentSkillsProvider` loads canonical skill names and referenced resources, including APM-renamed directories |
| Human interaction | Host callbacks confirm initialization, routing, plan/implementation and consequential tool calls; unanswered questions stop work |
| Evidence | Verifies artifacts, companion files, output hashes and dispatch history before advancing |
| Return-only roles | Intake and other applicable roles return typed findings, persisted as Scribe receipts rather than fake agent-written deliverables |
| Host tools | Explicit function registrations bind role permissions, effects and successful output evidence |
| Persistence | Release-bound MAF conversation sessions, confirmed answers, history, decisions and observed consumption |

**This is not full Copilot-host parity.** The runtime currently supports interactive,
single-squad operation with one producing-role owner per delivery turn. Federation,
Watch Mode, autonomous/autopilot loops, discovery interviews, multi-owner fan-out,
remote approval transports and skill-script execution are not implemented. Unsupported
modes fail explicitly rather than silently skipping their contracts. The library
provides no unrestricted shell, deployment, tracker, web-search or model credentials.
Those capabilities need explicit host integration.

For an RFP job, the result is a cited Markdown draft with gaps and review evidence,
not a Word/PDF export, a pricing engine, compliance proof, or a submitted bid.
`Completed` does not authorize commercial, legal, or security commitments.
Delivery/plan requests with `InputPaths` require intake; the released intake seat
is the PRD Quality Reviewer, not a generic business-document validator. Evaluate
that fit and surface blockers rather than assuming arbitrary RFPs will pass.
Commodity ticket routing and simple summaries may need only plain MAF; long-running
autonomous business agents are not a fit for this preview.

The native Scribe is a deterministic persistence service, not a model agent. It owns
the state directory and writes evidence only for actual specialist dispatches. Its
versioned state format is separate from Copilot's, so existing Copilot squad state
cannot be adopted or overwritten implicitly.

### Why federation is not implemented yet

This is an implementation scope limitation, **not an Agent Framework limitation**.
The native preview built one governed squad first. It has a fixed per-project state
root, one roster, and a single-writer Scribe. Federation needs a coordinator above
multiple squads, a registry and meta-routing, isolated sub-squad state roots and
locks, cross-squad input permissions, correlated approvals, and aggregate evidence
and outcomes. Native child-agent dispatch does not provide these contracts.

MAF can host that orchestration, but simply removing the unsupported-mode guard
would risk state collisions and bypassed gates. The
[federation guide](docs/federation.html) describes the missing work and possible
implementation sequence. No MCP service is inherently required to add it.

## Prerequisites

- .NET 10 SDK.
- Git and [APM CLI](https://github.com/microsoft/apm) on PATH. APM 0.18.0 is used in compatibility CI.
- Network access to GitHub and APM dependencies when resolving a release. The
  default path acquires the complete matched dependency tree, not just a source clone.
- A dedicated writable case directory for approved text inputs, native state, and
  outputs. An individual business case needs no Git repository or source checkout;
  Git/APM are used for artifact acquisition.
- For model execution, an application-provided `Microsoft.Extensions.AI.IChatClient`.
  The sample uses the official OpenAI .NET adapter.

The package is not published to NuGet. Use a project reference, or build a local
package; the [getting-started guide](docs/getting-started.html#package) covers local
consumption and labels the future public-feed command separately:

```powershell
dotnet build HveSquad.AgentFramework.slnx
dotnet pack src\HveSquad.AgentFramework --configuration Release --output artifacts
```

## Use in an existing MAF application

The worked example puts a **Draft response for bid review** button in a .NET sales
portal. The host authorizes the case and normalizes an RFP plus approved capability
evidence into known text inputs, then calls the library inside an application job:

```text
Sales user + approved inputs
    -> authorized .NET case job
    -> SquadRuntime: intake / research / approved outline / draft / review
    -> run status + draft evidence
    -> portal UI for bid-manager review (no external send)
```

The [complete class and result record](docs/use-cases.html#rfp-code) accept a
server-selected absolute `caseDirectory`, an existing `IChatClient`, human approval
and question callbacks, and cancellation. It pins `v0.16.2`, selects `full` for
`technical-writer` and the intake seats, supplies two known `InputPaths`, and
registers no custom tools. Native case reads and scoped Markdown/state writes
remain available. `InputPaths` is not a read allowlist; isolate the available data
in the case directory.

`BidDraftResult` retains the `SquadRunResult` and returns a draft-relative path only
on `Completed`, selected from the producing owner's exact evidence—not the latest
file or a user-supplied path. The caller maps other outcomes and exceptions to its
job UI and revalidates canonical path containment and user access before serving
output. The runtime does not ingest cloud Office documents or send results itself.

`ProjectPath` means this case work directory, not necessarily a development project.
The fixed `.copilot-tracking/squad` state root requires separate directories for
unrelated cases. The application queue enforces one active run per case. Dispose
each runtime after use; the host retains ownership of its model client.

Human callbacks confirm the full initialization proposal, routing, and later gates.
No approval callback means `ApprovalRequired`, not implicit approval. In a web or
desktop host, await an authenticated answer through the application's UI within
`RunTimeout`; the runtime does not provide durable approval suspension or automatic
job resume.

### Configuration and dependency injection

```json
{
  "HveSquad": {
    "ProjectPath": "C:\\app-data\\bid-cases\\demo",
    "Profile": "full",
    "Version": "v0.16.2",
    "Mode": "interactive",
    "Packs": [],
    "InputPaths": ["inputs\\rfp.txt", "inputs\\approved-capabilities.txt"],
    "MaxDispatches": 32,
    "MaxModelCalls": 64
  }
}
```

```csharp
using HveSquad.AgentFramework.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

services.AddSingleton<IChatClient>(chatClient);
services.AddHveSquad(
    configuration.GetSection("HveSquad"),
    configureHost: options =>
    {
        options.ApproveAsync = ApproveInYourApplicationAsync;
        options.AskAsync = AskInYourApplicationAsync;
    });

// Resolve from the application's service provider:
var factory = serviceProvider.GetRequiredService<SquadRuntimeFactory>();
using var squad = await factory.CreateAsync(cancellationToken);
var result = await squad.RunAsync(request, cancellationToken);
```

This configuration describes one provisioned demo case, not one directory shared
by every application user. `ApproveInYourApplicationAsync` and
`AskInYourApplicationAsync` are the application's callbacks, with the signatures in
the [complete job method](docs/use-cases.html#rfp-code). Registration is lazy:
network access and artifact acquisition occur in `CreateAsync`, not in a DI constructor.
Each factory call creates an independently owned runtime. Configuration binds only
known data settings; delegates, model selectors, tools and logging are host-only.

### Supplying real capabilities

Advisory Markdown drafts need no source editor, CRM writer, or external sender.
Agents can read contained case files and write scoped methodology artifacts using
native tools. Your host owns ingestion, approved retrieval, and delivery.
If another application feature genuinely needs to change source or an external
system, it requires a trusted host function; a report is not evidence of that effect.
For example, an optional source-editing integration can register:

```csharp
options.Tools.Add(new SquadToolRegistration(
    function,                    // An AIFunction implemented by the host.
    "edit",                      // Permission corresponding to the charter.
    ["developer"],               // Only these roster roles can call it.
    SquadToolEffect.ProjectWrite,
    OutputEvidence: result => ExtractSuccessfulOutputs(result)));
```

`ExtractSuccessfulOutputs` must return a `SquadToolOutput` based on the function's
actual successful result, never on model claims. Its `ProjectPaths` are verified
project-relative output files; external operations require a meaningful receipt.
Failed or dry-run operations must not attest an applied effect. For a host-specific
producing role, `RequireOutputForCompletion: true` can require the configured effect.

The sample's `ProjectFileWriter` demonstrates a contained source writer and output
attestation, not a capability needed by the RFP example. Every consequential call requires a separate host approval containing
the actual arguments. Hosts are responsible for honest effect classification,
resource isolation and not mutating the runtime's shared state through custom tools.

Host output files are hashed as raw bytes, including binary artifacts, with a
64 MiB per-file limit. Repeated approved edits in one dispatch retain all receipts
and verify the last observed contents. Overlapping output ownership across distinct
dispatches fails closed rather than treating an earlier artifact as unchanged.
A presenter needs a host-backed `.pptx` output; a Markdown report does not count as
a rendered deck. The runtime verifies file evidence, not the internal validity of
an Office package, and supplies no renderer itself.

Use `ModelSelector` for per-role clients and explicit model IDs, and
`EnableOpenTelemetry` for tracing. Provider-reported token counts are recorded;
unknown counts, model identities and costs remain unknown rather than invented.

## Staying aligned with HVE Squad releases

`Version = "latest"` resolves `/repos/Peter-N91/hve-squad/releases/latest`.
The source validates that it is a published stable semantic-version release,
resolves the **tag** to its commit SHA, and installs that SHA through APM.
It never uses the release's `target_commitish` field, a branch head, or `main`.

The currently exercised release is **v0.16.2**, commit
`a941195c36dfb6181453f51ec4638b085ca3b3ad`. Its APM manifest provides the matched
HVE Core dependency pin; upstream roles and rules are not maintained as duplicate
prompt text in this repository.

- A new runtime using `latest` checks for the current published stable release.
  A running runtime retains its resolved release and rejects artifact changes.
- Cache inventories cover deployed agents, instructions and complete skill resources.
  Missing or changed files invalidate the cache and trigger reacquisition.
- Authentication/rate-limit/network failures are reported; there is no silent
  fallback to `main`, a prerelease or another version.
- For reproducible/offline operation, resolve/install a release in advance and pass
  a `ProjectArtifactSource` for that installed directory. Explicit directory sources
  are host-managed overrides, not proof of published-release provenance.
- Existing native state is bound to the release, artifact fingerprint and roster.
  A new release cannot silently resume an old conversation. Keep the original
  version for that project, use a fresh project, or supply an explicit host migration.
  Automatic state migration is not implemented.

After this repository's changes are pushed, `release-compatibility.yml` runs on
pushes, pull requests, manual dispatch and daily. It exercises both v0.16.2 and
the latest stable release with a scripted model, including full-profile intake
and council. It never publishes packages or changes consumer state automatically.
New upstream formats or safety contracts can still require adapter changes:
**automatic acquisition is not a guarantee of automatic behavioral compatibility.**

## Runnable sample

Credential-free release inspection:

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --latest
dotnet run --project samples\HveSquad.AgentFramework.Sample -- --release v0.16.2
```

With no arguments the inspector searches for an installed project; explicit
artifact-directory arguments remain supported.

For a live run, configure `OPENAI_API_KEY` and `OPENAI_MODEL` securely in the host
environment. Optional `OPENAI_ENDPOINT` supports an OpenAI-compatible endpoint,
including a correctly configured Azure OpenAI v1 endpoint. No credentials are
stored in the repository.

```powershell
dotnet run --project samples\HveSquad.AgentFramework.Sample -- `
  --run "Research the question in inputs\rfp.txt using inputs\approved-capabilities.txt. Cite source identifiers and report gaps. Stop after research; do not change inputs or send anything externally." `
  --project C:\app-data\bid-cases\demo --version v0.16.2 --profile full
```

First provision the [fictional approved text inputs](docs/getting-started.html#sample);
an empty directory cannot supply product evidence. This focused request produces
research evidence, not a completed response draft.

Alternatively, `--config <run.json>` accepts `request`, `project`, `version` and
`profile`. The console waits for explicit `yes` on approvals and supports Ctrl+C.
In addition to native case reads and methodology artifacts, it exposes a contained
`write_project_file` source tool for the developer role. That is an optional sample
capability, not the library's business proposition. It has no shell/build/test
executor or external research connector and does not imply unavailable operations ran.

## Development and compatibility checks

```powershell
dotnet test HveSquad.AgentFramework.slnx

# Acquire and exercise the exact published release with a scripted IChatClient.
$env:HVE_SQUAD_RELEASE_VERSION = "v0.16.2"
$env:HVE_SQUAD_REQUIRE_RELEASE_TESTS = "true"
dotnet test tests\HveSquad.AgentFramework.Tests --filter Category=ReleaseCompatibility
```

Use `HVE_SQUAD_RELEASE_VERSION=latest` for the current stable release, or
`HVE_SQUAD_RELEASE_ROOT` for an already-installed project containing `.github`
and `.agents`. These checks require no paid model calls and do not establish
live-model quality or complete Copilot parity.

### Security and repository automation

The workflows adapt HVE Squad's security approach to this C# library and static site:

| Workflow | Purpose |
| --- | --- |
| `codeql.yml` | C# manual-build analysis and JavaScript analysis |
| `checkov.yml` | Blocking Actions/secrets checks and IaC/container checks when relevant files exist |
| `zizmor.yml` | Blocking offline GitHub Actions analysis |
| `dependency-review.yml` | High/critical newly introduced dependency advisories block pull requests |
| `dependency-audit.yml` | Direct/transitive NuGet audit; high/critical advisories and incomplete feeds block |
| `scorecard.yml` | Supply-chain assessment reports, no public Scorecard API publication or badge |
| `release-compatibility.yml` | Build, tests, package, public documentation links and released upstream contracts |
| `docs.yml` | Least-privilege static Pages deployment from `main` |

Dependabot proposes weekly NuGet and action updates. Repository dependency graph,
code scanning, secret scanning/push protection and branch rules must still be enabled
where available; workflow files cannot configure them. Set the Actions variable
`SECURITY_SARIF_UPLOAD=true` to opt into trusted-main Checkov/Zizmor code-scanning
uploads. Their report artifacts are retained regardless. See
[workflow activation and maintenance](docs/maintaining.html#workflows) for permissions,
feature availability, severity policy and publishing boundaries.

The lower-level `HveSquadBuilder`, `SquadAgentFactory` and `SquadWorkflowBuilder`
remain available for artifact inspection and custom composition. They are not the
governed runtime entry point; use `SquadRuntime` when you need its enforced gates.

## License

MIT. See [LICENSE](LICENSE). Upstream artifacts are acquired at installation time,
not redistributed in this package, and retain their own licenses. This project is
not affiliated with or endorsed by Microsoft.
