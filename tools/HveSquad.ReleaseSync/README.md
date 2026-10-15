---
title: Reviewed HVE Squad release alignment
description: Detect official stable releases, inspect exact contract drift, and maintain review-only candidate proposals.
---

## Supported scenarios

The tool uses the library's [release resolver](../../src/HveSquad.AgentFramework/Sources/ReleaseArtifactSource.cs)
and [supported manifest](../../compatibility/hve-squad-release.json). Production defaults remain pinned.
Drafts, prereleases, mutable refs, unpublished metadata, moved tags and unavailable API results are explicit failures.
It never acquires from upstream `main`.

The scheduled [alignment workflow](../../.github/workflows/release-alignment.yml) separates read-only
evaluation from a narrowly privileged proposal job. The latter compiles only the trusted repository head,
does not acquire/execute candidate artifacts, and can add only
`compatibility/candidate-release.json` to one deterministic `automation/hve-squad/vX.Y.Z` branch.
The atomic GitHub commit binds the expected branch head. Original parent, sole file, exact content,
branch and PR head are independently re-read. Existing, closed, colliding or uncertain proposals are
not overwritten, force-pushed, auto-suffixed, reopened or silently duplicated.

No command merges, publishes a package, deploys, edits consumer squad state or provisions credentials.
The proposal is a draft, not a compatibility pass or an upgrade.

## Read-only local evaluation

Run from the repository with .NET 10, Git and the existing tested APM CLI available:

```powershell
dotnet run --project tools\HveSquad.ReleaseSync --configuration Release -- inspect
dotnet run --project tools\HveSquad.ReleaseSync --configuration Release -- inspect --evaluate-current
```

The first command performs metadata-only no-op detection when the official stable release matches the
production pin. The second acquires/verifies the exact current graph and compares its canonical resources,
roles/profiles/packs, modes, schemas and numbered assertion rows. Text resource digests normalize UTF-8 BOM
and line endings; binary resources retain byte identity. Version/commit/dependency identities remain exact.
No raw release notes, charter bodies, private case content or credentials enter the report.

Default output is `artifacts/release-sync/report.json`. An optional host-owned
`HVE_SQUAD_RELEASE_GITHUB_TOKEN` authenticates public metadata requests; it is never printed or inherited
by the APM process. Network/rate/rights failures do not become cached success or alternate versions.

The reviewed snapshot is [hve-squad-contracts.json](../../compatibility/hve-squad-contracts.json).
Missing or changed contracts, unmapped assertions, an unreviewed candidate pin or incomplete mechanical
conformance explicitly block promotion. A successfully emitted report means evaluation completed,
not that these blockers passed.

## Maintainer setup

* Enable Actions and allow GitHub Actions to create pull requests in repository Actions settings.
* The evaluation job needs only `contents: read`; the proposal job separately needs `contents: write`,
  `pull-requests: write` and `actions: read`. Checkout credentials are not persisted.
* Require the `candidate-linux` and `candidate-windows` checks and human review in branch rules.
  These files cannot configure repository settings or grant themselves rights.
* When a bot uses `GITHUB_TOKEN` to create/update a PR, select **Approve workflows to run** in the
  PR merge box. Until then, approval-pending or missing checks block promotion.
  See [GitHub's current triggering rules](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).
* No PAT/App is silently required or created. An independently approved App/PAT may alter triggering
  behavior, but does not replace exact-head checks or human review.

## Reviewing a candidate

1. Read the alignment artifact and the draft PR. Its identity/dependency/resource/role/mode/schema/assertion
   drift identifies maintenance work; the tool cannot guess implementations of unknown contracts.
2. Update adapters, tests and every applicable assertion's ownership/evidence in the candidate review branch.
   Keep the library independently versioned and preserve intended legacy behavior.
3. Update the supported tag/commit/Core manifest and `ReleaseArtifactSource.TestedReleaseTag` together in
   that branch, never an active consumer's state. Capture and review the exact new contract snapshot:

   ```powershell
   dotnet run --project tools\HveSquad.ReleaseSync --configuration Release -- capture-contracts --output compatibility\hve-squad-contracts.json
   ```

4. Let [candidate checks](../../.github/workflows/release-candidate.yml) run on the actual PR head.
   They require a clean exact checkout, the full native/current suite, exact legacy cases, an actual
   isolated package consumer with byte-identical repack, the individual
   [conformance catalogue](../../compatibility/hve-squad-conformance.json), nonempty successful TRX
   results and six actual mode/profile observations. Each required build/test/package/conformance
   step must succeed on both Windows and Linux; green base/merge-head runs, empty reports, skips,
   absent methods/data cases and mismatched commits do not count.
5. Compare the actual observations against the prior last-good
   [fixture baseline](../../compatibility/observations/v0.18.1.json).
   They are reduced from verified native dispatch/history/gate receipts and actual files, not run prose.
   Deliverables compare normalized root plus extension, never topic filenames. Native bookkeeping,
   memory/learning operations and coordinator/provider calls are not fictitious role dispatches.
   The configured host `TimeProvider` governs new provider, dispatch and native-writer timestamps;
   original historical receipt bytes are never rewritten to a new clock.
6. Semantic drift remains advisory until its noise floor is measured. Missing baselines are `unbaselined`;
   absent/unavailable answer judging is `not judged`, with its weight redistributed across observed
   dimensions. This automation invokes no answer judge or paid model.
7. Mark the draft ready and obtain human review before merging. Only that reviewed source change promotes
   the package's default. Existing release-bound cases still require their original pin or an explicit
   verified migration/fresh case. A PR is not a release, deployment or IndustryIQ quality guarantee.

The current manifest remains `in-progress` until full conformance/package/platform acceptance is
recorded. A candidate workflow correctly refuses promotion while that evidence is incomplete.

To validate a saved actual fixture report locally without pretending a dirty working tree is a
verified candidate head, use `verify-evidence`. This checks only the identity-bound observation/TRX
shape and executed category set, not promotion:

```powershell
dotnet run --project tools\HveSquad.ReleaseSync --configuration Release -- verify-evidence --trx artifacts\release-sync\candidate.trx
```

That narrower command is not individual conformance. After the full current test run exports
`artifacts\release-sync\observations`, execute the maintained package consumer and verify every
individual mapping:

```powershell
$Work = Join-Path ([IO.Path]::GetTempPath()) ("hve-squad-package-" + [Guid]::NewGuid().ToString("N"))
pwsh -NoProfile -File .\scripts\Test-PackageConsumer.ps1 -WorkRoot $Work
dotnet run --project tools\HveSquad.ReleaseSync --configuration Release -- verify-conformance --trx artifacts\release-sync\candidate.trx --package-proof (Join-Path $Work "package-evidence.json") --output artifacts\release-sync\conformance.json
```

`verify-conformance` checks the exact release and all per-ID method/minimum-case bindings,
complete passed result/definition identities, original observations, package/repack/manifest facts
and equality of the packaged/consumed/tested native DLL bytes. An optional `--head` additionally
requires a clean exact checkout before and after verification. The report is mechanical evidence,
not promotion permission. `verify` also requires `--package-proof` and rechecks this complete
evidence before enforcing reviewed contracts and the canonical conformance status.

The compatibility schedule and `main` workflow call this same acceptance matrix; PR updates run the
candidate workflow directly, avoiding duplicate full suites. The dedicated authorized validation
branch can run it before merge. No check publishes its built NuGet package or deploys a product.
The catalogue explicitly explains native schema/Markdown receipts, deterministic Scribe work,
the 32 dispatchable full-profile roles plus Scribe, optional frontmatter defaults, native MAF
charter delivery beyond the Copilot UI cap, and NuGet versus plugin-hook packaging.

`capture-observations --output compatibility\observations\vX.Y.Z.json` records the latest actual
fixture attempt for each scenario. Review the fixture's executed release/commit and successful report
before adopting it as a last-good baseline. Never replace the prior baseline merely to hide drift.

## Explicit proposal and status commands

After trusted evaluation supplies an exact clean default-branch `--head`, a separately authorized operator
can use a job-scoped `GH_TOKEN` to run:

```powershell
dotnet run --project tools\HveSquad.ReleaseSync --configuration Release -- propose --allow-write --repository OWNER/REPO --report artifacts\release-sync\report.json
dotnet run --project tools\HveSquad.ReleaseSync --configuration Release -- status --repository OWNER/REPO --report artifacts\release-sync\report.json
```

The first command can mutate the candidate branch/PR and must not be used merely to inspect a release.
The second is read-only. Failed/missing/unavailable, running and approval-pending checks are distinct
from `checks-passed`; even the latter still requires human review.

Transport uncertainty is an explicit error. Inspect the provider and run read-only status before any
retry. A repeat proposal recovers only an independently verified original metadata commit/PR;
expected-head CAS and provider identity prevent replaying an already observed write.

AI-assisted maintenance evidence requires human review. Scripted native fixtures establish mechanics,
not live-model answer quality, product authorization, tenant isolation or deployment readiness.
