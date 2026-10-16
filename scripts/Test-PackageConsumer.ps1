#!/usr/bin/env pwsh
# Copyright (c) 2026 Microsoft Corporation. All rights reserved.
# SPDX-License-Identifier: MIT
#Requires -Version 7.4

<#
.SYNOPSIS
    Packs and executes the real package in a new isolated consumer.
.DESCRIPTION
    Restores from a package-mapped local feed without source-project references,
    checks metadata and assembly bytes, and runs scripted native public APIs.
    Leaves exact local evidence for inspection. Does not publish or deploy.
.PARAMETER WorkRoot
    A new absolute directory outside the repository, never an existing directory.
.EXAMPLE
    .\scripts\Test-PackageConsumer.ps1
.NOTES
    Requires .NET 10, PowerShell 7.4+, NuGet connectivity and APM for public release
    acquisition. No model credentials or remote write permissions are used.
    The consumer explicitly configures an optional host-owned
    HVE_SQUAD_RELEASE_GITHUB_TOKEN for release metadata; it is not sent to APM.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ('hve-squad-package-' + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'

#region Main Execution
if ($MyInvocation.InvocationName -ne '.') {
    try {
        if (-not [IO.Path]::IsPathFullyQualified($WorkRoot)) { throw 'WorkRoot must be an absolute directory.' }
        $Repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
        $Relative = [IO.Path]::GetRelativePath($Repository, $WorkRoot)
        if ((Test-Path -LiteralPath $WorkRoot) -or $Relative -eq '.' -or
            (-not $Relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar) -and -not [IO.Path]::IsPathFullyQualified($Relative))) {
            throw 'WorkRoot must be a new isolated directory outside the repository.'
        }
        $Feed = Join-Path $WorkRoot 'feed'
        $Packages = Join-Path $WorkRoot 'packages'
        $Consumer = Join-Path $WorkRoot 'HveSquad.PackageConsumer.csproj'
        $Case = Join-Path $WorkRoot 'case'
        $null = New-Item -ItemType Directory -Path $Feed
        foreach ($Name in @('HveSquad.PackageConsumer.csproj', 'Program.cs', 'NuGet.Config')) {
            Copy-Item -LiteralPath (Join-Path $Repository "samples\HveSquad.PackageConsumer\$Name") -Destination (Join-Path $WorkRoot $Name)
        }
        [xml]$Project = Get-Content -LiteralPath (Join-Path $Repository 'src\HveSquad.AgentFramework\HveSquad.AgentFramework.csproj') -Raw
        $Version = [string]$Project.Project.PropertyGroup.Version
        $Package = Join-Path $Feed "HveSquad.AgentFramework.$Version.nupkg"
        & dotnet pack (Join-Path $Repository 'src\HveSquad.AgentFramework\HveSquad.AgentFramework.csproj') --configuration Release --output $Feed
        if ($LASTEXITCODE -ne 0) { throw 'Actual library pack failed.' }

        $Archive = [IO.Compression.ZipFile]::OpenRead($Package)
        try {
            $Names = @($Archive.Entries.FullName)
            $Allowed = '^(HveSquad\.AgentFramework\.nuspec|README\.md|LICENSE|compatibility/hve-squad-release\.json|lib/net10\.0/HveSquad\.AgentFramework\.dll|_rels/\.rels|\[Content_Types\]\.xml|package/services/metadata/core-properties/(nuget|[a-fA-F0-9]+)\.psmdcp)$'
            if (@($Names | Where-Object { $_ -cnotmatch $Allowed }).Count -ne 0) {
                throw 'Unexpected package content: refuse upstream charter, state, secret or source redistribution.'
            }
            foreach ($Required in @('README.md', 'LICENSE', 'compatibility/hve-squad-release.json', 'lib/net10.0/HveSquad.AgentFramework.dll')) {
                if ($Required -cnotin $Names) { throw "Missing required package entry: $Required" }
            }
            $Reader = [IO.StreamReader]::new($Archive.GetEntry('HveSquad.AgentFramework.nuspec').Open())
            try { [xml]$Metadata = $Reader.ReadToEnd() } finally { $Reader.Dispose() }
            if ($Metadata.package.metadata.id -cne 'HveSquad.AgentFramework' -or
                $Metadata.package.metadata.version -cne $Version -or
                $Metadata.package.metadata.license.InnerText -cne 'MIT' -or
                $Metadata.package.metadata.readme -cne 'README.md' -or
                $Metadata.package.metadata.repository.url -cne 'https://github.com/Peter-N91/hve-squad-maf' -or
                $Metadata.package.metadata.repository.type -cne 'git') {
                throw 'Package identity, independent version, license, readme or repository provenance is incorrect.'
            }
            $ExpectedAssembly = Join-Path $WorkRoot 'packaged-assembly.dll'
            [IO.Compression.ZipFileExtensions]::ExtractToFile($Archive.GetEntry('lib/net10.0/HveSquad.AgentFramework.dll'), $ExpectedAssembly)
            $Reader = [IO.StreamReader]::new($Archive.GetEntry('compatibility/hve-squad-release.json').Open())
            try { $PackagedManifest = $Reader.ReadToEnd() } finally { $Reader.Dispose() }
            if ($PackagedManifest -cne (Get-Content -LiteralPath (Join-Path $Repository 'compatibility\hve-squad-release.json') -Raw)) {
                throw 'Packaged supported-release manifest differs from the canonical source.'
            }
            foreach ($Entry in @(
                @{ Name = 'README.md'; Source = 'src\HveSquad.AgentFramework\README.md' },
                @{ Name = 'LICENSE'; Source = 'LICENSE' }
            )) {
                $Reader = [IO.StreamReader]::new($Archive.GetEntry($Entry.Name).Open())
                try { $Text = $Reader.ReadToEnd() } finally { $Reader.Dispose() }
                if ($Text -cne (Get-Content -LiteralPath (Join-Path $Repository $Entry.Source) -Raw)) {
                    throw "Packaged $($Entry.Name) differs from its approved source."
                }
            }
        }
        finally { $Archive.Dispose() }

        $Repack = Join-Path $WorkRoot 'repack'
        & dotnet pack (Join-Path $Repository 'src\HveSquad.AgentFramework\HveSquad.AgentFramework.csproj') --configuration Release --no-build --no-restore --output $Repack
        if ($LASTEXITCODE -ne 0) { throw 'Unchanged-assembly package repack failed.' }
        if ((Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath (Join-Path $Repack "HveSquad.AgentFramework.$Version.nupkg") -Algorithm SHA256).Hash) {
            throw 'The package is not byte-identical when repacked. Use a .NET 10 SDK with fixed DeterministicTimestamp support (validated: 10.0.402); do not treat changing ZIP timestamps as reproducible output.'
        }

        & dotnet restore $Consumer --configfile (Join-Path $WorkRoot 'NuGet.Config') --packages $Packages "-p:HveSquadPackageVersion=$Version" -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false
        if ($LASTEXITCODE -ne 0) { throw 'Isolated actual-package restore failed.' }
        $Assets = Get-Content -LiteralPath (Join-Path $WorkRoot 'obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($Assets.libraries["HveSquad.AgentFramework/$Version"].type -cne 'package' -or
            @($Assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) {
            throw 'The isolated consumer did not resolve the library exclusively as an actual package.'
        }
        & dotnet build $Consumer --configuration Release --no-restore "-p:HveSquadPackageVersion=$Version" -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false
        if ($LASTEXITCODE -ne 0) { throw 'Public-package consumer build/analyzers failed.' }
        $LoadedAssembly = Join-Path $WorkRoot 'bin\Release\net10.0\HveSquad.AgentFramework.dll'
        if ((Get-FileHash -LiteralPath $ExpectedAssembly -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $LoadedAssembly -Algorithm SHA256).Hash) {
            throw 'The executing consumer assembly is not the actual packed DLL.'
        }
        & dotnet (Join-Path $WorkRoot 'bin\Release\net10.0\HveSquad.PackageConsumer.dll') --case $Case --package-version $Version
        if ($LASTEXITCODE -ne 0) { throw 'Isolated public-package native execution failed.' }
        $Report = Get-Content -LiteralPath (Join-Path $Case 'verification.json') -Raw | ConvertFrom-Json
        if ($Report.status -cne 'passed') { throw 'The consumer did not persist passed actual execution evidence.' }
        $Evidence = [ordered]@{
            schemaVersion = 1
            status = 'passed'
            packageVersion = $Version
            packageSha256 = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
            repackSha256 = (Get-FileHash -LiteralPath (Join-Path $Repack "HveSquad.AgentFramework.$Version.nupkg") -Algorithm SHA256).Hash
            assemblySha256 = (Get-FileHash -LiteralPath $LoadedAssembly -Algorithm SHA256).Hash
            manifestSha256 = (Get-FileHash -LiteralPath (Join-Path $Repository 'compatibility\hve-squad-release.json') -Algorithm SHA256).Hash
            consumer = $Report
        }
        $Evidence | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $WorkRoot 'package-evidence.json') -Encoding utf8NoBOM
        Write-Output "Verified actual package: $Package"
        Write-Output "Isolated consumer evidence: $(Join-Path $Case 'verification.json')"
        exit 0
    }
    catch {
        Write-Error -ErrorAction Continue "Package consumer failed: $($_.Exception.Message)"
        exit 1
    }
}
#endregion Main Execution
