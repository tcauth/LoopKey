[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Laptop", "DevBox")]
    [string]$Role,

    [ValidateSet("github-copilot", "codex", "claude-code", "all", "none")]
    [string]$Agent = "all",

    [string]$Version = "v0.2.0-alpha.2",

    [string]$SkillRef,

    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA "LoopKey"),

    [string]$WrapperDirectory = (Join-Path $env:USERPROFILE ".azd-profiles"),

    [string]$GreenTenantId,

    [string]$GreenPostAuthScript,

    [string]$ExpectedDevTunnelUser,

    [switch]$SkipDevTunnelLogin,

    [switch]$SkipPathUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Refresh-ProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$machinePath;$userPath"
}

function Ensure-Command {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$PackageId
    )

    if (Get-Command $Name -ErrorAction SilentlyContinue) {
        return
    }

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "Missing '$Name' and winget is unavailable. Install $PackageId, then rerun setup."
    }

    Invoke-Checked "winget" @(
        "install",
        "--id", $PackageId,
        "--exact",
        "--silent",
        "--accept-package-agreements",
        "--accept-source-agreements"
    )
    Refresh-ProcessPath

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Installed $PackageId, but '$Name' is not available in PATH. Start a new terminal and rerun setup."
    }
}

function Install-LoopKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVersion
    )

    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
    $temporaryPath = Join-Path ([IO.Path]::GetTempPath()) "loopkey-$([Guid]::NewGuid().ToString('N')).exe"
    $checksumPath = "$temporaryPath.sha256"
    $releaseBase = "https://github.com/tcauth/LoopKey/releases/download/$ResolvedVersion"
    try {
        Invoke-WebRequest -Uri "$releaseBase/loopkey.exe" -OutFile $temporaryPath -UseBasicParsing
        Invoke-WebRequest -Uri "$releaseBase/loopkey.exe.sha256" -OutFile $checksumPath -UseBasicParsing
        $checksum = (Get-Content -Raw $checksumPath).Trim()
        if ($checksum -notmatch "^([0-9a-fA-F]{64})\s+loopkey\.exe$") {
            throw "Release $ResolvedVersion contains an invalid loopkey.exe.sha256 file."
        }

        $expected = $Matches[1].ToLowerInvariant()
        $actual = (Get-FileHash -Path $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "Downloaded loopkey.exe digest mismatch. Expected $expected, got $actual."
        }

        Move-Item -Force -Path $temporaryPath -Destination (Join-Path $InstallDirectory "loopkey.exe")
    }
    finally {
        Remove-Item -Force -ErrorAction SilentlyContinue $temporaryPath
        Remove-Item -Force -ErrorAction SilentlyContinue $checksumPath
    }
}

function Add-UserPath {
    if ($SkipPathUpdate) {
        return
    }

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $segments = @($userPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($segments -notcontains $InstallDirectory) {
        $newPath = (@($segments) + $InstallDirectory) -join ";"
        [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    }

    if (($env:Path -split ";") -notcontains $InstallDirectory) {
        $env:Path = "$env:Path;$InstallDirectory"
    }
}

function Install-AgentSkill {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedVersion
    )

    if ($Agent -eq "none") {
        return
    }

    Ensure-Command "gh" "GitHub.cli"
    $agents = if ($Agent -eq "all") {
        @("github-copilot", "codex", "claude-code")
    }
    else {
        @($Agent)
    }
    $reference = if ([string]::IsNullOrWhiteSpace($SkillRef)) { $ResolvedVersion } else { $SkillRef }

    foreach ($target in $agents) {
        Invoke-Checked "gh" @(
            "skill", "install",
            "tcauth/LoopKey",
            "loopkey",
            "--agent", $target,
            "--scope", "user",
            "--pin", $reference,
            "--force"
        )
    }
}

function Install-AzdWrappers {
    Ensure-Command "azd" "Microsoft.Azd"
    $azdPath = (Get-Command azd -ErrorAction Stop).Source
    New-Item -ItemType Directory -Force -Path $WrapperDirectory | Out-Null

    foreach ($profile in @("corp", "green")) {
        $cacheDirectory = ".azd-$profile"
        $wrapperPath = Join-Path $WrapperDirectory "azd-$profile.cmd"
        $content = "@echo off`r`nset `"AZD_CONFIG_DIR=%USERPROFILE%\$cacheDirectory`"`r`n`"$azdPath`" %*`r`n"
        Set-Content -Path $wrapperPath -Value $content -Encoding Ascii -NoNewline
    }

    if (-not [string]::IsNullOrWhiteSpace($GreenTenantId)) {
        [Environment]::SetEnvironmentVariable("LOOPKEY_GREEN_TENANT_ID", $GreenTenantId, "User")
        $env:LOOPKEY_GREEN_TENANT_ID = $GreenTenantId
    }
    if (-not [string]::IsNullOrWhiteSpace($GreenPostAuthScript)) {
        $resolvedPostAuthScript = (Resolve-Path $GreenPostAuthScript -ErrorAction Stop).Path
        if (
            [IO.Path]::GetExtension($resolvedPostAuthScript).Equals(
                ".py",
                [StringComparison]::OrdinalIgnoreCase) -and
            -not (Get-Command python -ErrorAction SilentlyContinue)
        ) {
            throw "The configured Python Green post-auth script requires 'python' in PATH."
        }
        [Environment]::SetEnvironmentVariable(
            "LOOPKEY_GREEN_POST_AUTH_SCRIPT",
            $resolvedPostAuthScript,
            "User")
        $env:LOOPKEY_GREEN_POST_AUTH_SCRIPT = $resolvedPostAuthScript
    }
}

function Ensure-DevTunnelLogin {
    if ($SkipDevTunnelLogin) {
        return
    }

    $output = & devtunnel user show 2>&1
    if ($LASTEXITCODE -eq 0 -and ($output -join "`n") -match "Logged in as") {
        return
    }

    $authentication = if ($Role -eq "Laptop") {
        "--use-browser-auth"
    }
    else {
        "--use-device-code-auth"
    }
    Invoke-Checked "devtunnel" @("user", "login", $authentication, "--entra")
}

function Save-DevTunnelIdentity {
    $json = (& devtunnel user show --json 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read the authenticated Dev Tunnel identity."
    }

    $identity = $json | ConvertFrom-Json
    if ($identity.status -ne "Logged in" -or [string]::IsNullOrWhiteSpace($identity.username)) {
        throw "Dev Tunnel authentication did not produce a logged-in identity."
    }
    $identityMismatch =
        -not [string]::IsNullOrWhiteSpace($ExpectedDevTunnelUser) -and
        -not $identity.username.Equals(
        $ExpectedDevTunnelUser,
        [StringComparison]::OrdinalIgnoreCase)
    if ($identityMismatch) {
        throw "Dev Tunnel identity mismatch. Expected '$ExpectedDevTunnelUser', got '$($identity.username)'."
    }

    $tenantIdProperty = $identity.PSObject.Properties["tenantId"]
    $summary = [ordered]@{
        provider = $identity.provider
        username = $identity.username
        tenantId = if ($null -eq $tenantIdProperty) { $null } else { $tenantIdProperty.Value }
    }
    $identityPath = Join-Path $InstallDirectory "devtunnel-identity.json"
    $summary | ConvertTo-Json | Set-Content -Path $identityPath -Encoding UTF8
    return $summary
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "LoopKey setup supports Windows only."
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw "The published LoopKey binary requires 64-bit Windows."
}

Ensure-Command "devtunnel" "Microsoft.devtunnel"
if ($Role -eq "DevBox") {
    Install-AzdWrappers
}

if ($Version -notmatch "^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$") {
    throw "Version must be a release tag such as v0.2.0-alpha.2."
}
$resolvedVersion = $Version
Install-LoopKey $resolvedVersion
Add-UserPath
Install-AgentSkill $resolvedVersion
Ensure-DevTunnelLogin
$devTunnelIdentity = Save-DevTunnelIdentity

$loopkeyPath = Join-Path $InstallDirectory "loopkey.exe"
Invoke-Checked $loopkeyPath @("--help")
$installedVersion = (& $loopkeyPath --version 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "Installed loopkey.exe does not support version verification."
}
$expectedVersion = "loopkey $($resolvedVersion.TrimStart('v'))"
if ($installedVersion.Trim() -ne $expectedVersion) {
    throw "Release tag $resolvedVersion does not match installed binary '$installedVersion'."
}
Invoke-Checked "devtunnel" @("user", "show")

Write-Host ""
Write-Host "LoopKey setup complete."
Write-Host "Role: $Role"
Write-Host "Version: $resolvedVersion"
Write-Host "Executable: $loopkeyPath"
Write-Host "Dev Tunnel identity: $($devTunnelIdentity.username) ($($devTunnelIdentity.provider))"
if ($Role -eq "DevBox") {
    Write-Host "Wrappers: $WrapperDirectory"
}
if (-not $SkipPathUpdate) {
    Write-Host "The user PATH is configured. Existing terminals may need to be restarted."
}
