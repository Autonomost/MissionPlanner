<#
.SYNOPSIS
    Build Mission Planner, then produce a release zip and a Windows Installer (.msi), code-signing both.

.DESCRIPTION
    Steps (each can be skipped with a switch):
      1. Build   - DriverCleanup, MissionPlanner.csproj and the wix installer generator (MSBuild, Release).
      2. Clean   - remove plugin DLLs that duplicate a root DLL (same cleanup CI and build.bat do).
      3. Sign    - Authenticode sign MissionPlanner.exe (and optionally every unsigned exe/dll) so the
                   files inside the zip and MSI carry a signature.
      4. Zip     - dist\<Product>-<version>.zip of bin\Release\net461.
      5. MSI     - generate installer.wxs with wix.exe, compile with WiX 3.11 candle/light, sign the MSI.
      6. Hash    - dist\SHA256SUMS.txt.

    Signing needs a code-signing certificate. Give either -CertThumbprint (certificate in the
    CurrentUser\My or LocalMachine\My store) or -PfxPath/-PfxPassword. With neither, the artifacts are
    produced unsigned and a warning is printed. See packaging\README.md for what signing means and how
    to make a local test certificate.

.EXAMPLE
    .\packaging\package.ps1 -CertThumbprint 0123ABCD...

.EXAMPLE
    .\packaging\package.ps1 -SkipBuild -PfxPath C:\certs\nevermind.pfx -PfxPassword (Read-Host -AsSecureString)

.EXAMPLE
    # upgrade over an official Mission Planner install instead of side by side
    .\packaging\package.ps1 -UpgradeCode "{625389D7-EB3C-4d77-A5F6-A285CF99437D}"
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Manufacturer = "Nevermind",
    [string]$ProductName = "Mission Planner",
    [string]$UpgradeCode = "",
    [switch]$IncludeDrivers,
    [string]$OutDir = "",
    # Compiled exe/dll/pdb files record the source path of the build machine. PathMap replaces the
    # repo root with this neutral path so no user name or folder name is embedded in shipped files.
    [string]$PathMapTo = "C:\src\MissionPlanner",

    [string]$CertThumbprint = "",
    [string]$PfxPath = "",
    [System.Security.SecureString]$PfxPassword = $null,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SignAllBinaries,

    [switch]$SkipBuild,
    [switch]$SkipZip,
    [switch]$SkipMsi,
    [switch]$SkipSign
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$BinDir = Join-Path $Root "bin\$Configuration\net461"
if ($OutDir -eq "") { $OutDir = Join-Path $Root "dist" }
$MsiDir = Join-Path $Root "Msi"

function Write-Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($p) { return $p }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "MSBuild not found. Install Visual Studio 2022 or later with the .NET desktop workload (see vs2022.vsconfig)."
}

function Find-SignTool {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $p = Get-ChildItem $kits -Directory | Where-Object { $_.Name -match '^\d+\.' } | Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($p) { return $p }
    }
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Find-Wix {
    $candidates = @()
    if ($env:WIX) { $candidates += $env:WIX }
    $candidates += "${env:ProgramFiles(x86)}\WiX Toolset v3.14"
    $candidates += "${env:ProgramFiles(x86)}\WiX Toolset v3.11"
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "bin\candle.exe"))) { return (Join-Path $c "bin") }
    }
    return $null
}

# ---------------------------------------------------------------- signing setup
$SignTool = $null
$SignArgs = @()
$CanSign = $false
if (-not $SkipSign) {
    if ($CertThumbprint -ne "" -or $PfxPath -ne "") {
        $SignTool = Find-SignTool
        if (-not $SignTool) { throw "signtool.exe not found. Install the Windows 10/11 SDK (Windows Kits) or add signtool to PATH." }
        $SignArgs = @("sign", "/fd", "SHA256", "/td", "SHA256", "/tr", $TimestampUrl, "/d", $ProductName)
        if ($CertThumbprint -ne "") {
            $SignArgs += @("/sha1", $CertThumbprint)
        } else {
            if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
            $SignArgs += @("/f", (Resolve-Path $PfxPath).Path)
            if ($PfxPassword) {
                $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword)
                $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
                $SignArgs += @("/p", $plain)
            }
        }
        $CanSign = $true
    } else {
        Write-Warning "No -CertThumbprint or -PfxPath given: output will NOT be code-signed. Windows will show 'Unknown publisher'."
    }
}

function Invoke-Sign([string[]]$files) {
    if (-not $CanSign) { return }
    foreach ($f in $files) {
        $sig = Get-AuthenticodeSignature $f
        if ($sig.Status -eq "Valid" -and $sig.SignerCertificate.Thumbprint -eq $CertThumbprint) { continue }
        Write-Host "  signing $([IO.Path]::GetFileName($f))"
        & $SignTool @SignArgs $f | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "signtool failed on $f (exit $LASTEXITCODE)" }
    }
}

# ---------------------------------------------------------------- 1. build
$MSBuild = Find-MSBuild
if (-not $SkipBuild) {
    Write-Step "Building ($Configuration) with $MSBuild"
    # note: -p:Deterministic=true is not possible here, the project uses a wildcard AssemblyVersion (1.3.*)
    $buildProps = @("-p:Configuration=$Configuration")
    # PathMap is not part of MSBuild's up-to-date check, so an incremental Build would keep old
    # binaries with the real path embedded. Clean first when mapping. (Not -t:Rebuild: its Clean
    # deletes NuGet generated content files such as GDAL's GdalConfiguration.cs without re-running
    # restore, so the build after it fails. Clean + forced restore + Build works.)
    Push-Location $Root
    try {
        if ($PathMapTo -ne "") {
            # SourceLink would embed the git remote URL and the unmapped repo path into every PDB.
            # DebugType=embedded keeps line numbers for crash logs but puts the debug info inside the
            # exe/dll: no .pdb files ship, and (unlike a separate PDB on this non-deterministic exe)
            # the build machine path is not written into the binary.
            $buildProps += "-p:PathMap=$Root=$PathMapTo", "-p:EnableSourceLink=false", "-p:DebugType=embedded"
            & $MSBuild -v:m -t:Clean @buildProps -m "MissionPlanner.csproj"
            & $MSBuild -v:m -t:Clean @buildProps "ExtLibs\DriverCleanup\DriverCleanup.csproj"
            $buildProps += "-p:RestoreForce=true"
        }
        & $MSBuild -v:m -restore -t:Build @buildProps "ExtLibs\DriverCleanup\DriverCleanup.csproj"
        if ($LASTEXITCODE -ne 0) { throw "DriverCleanup build failed" }
        & $MSBuild -v:m -restore -t:Build @buildProps -m "MissionPlanner.csproj"
        if ($LASTEXITCODE -ne 0) { throw "MissionPlanner build failed" }
        if ($PathMapTo -ne "") {
            # embedded debug info produces no .pdb files; drop any left over from earlier builds
            Get-ChildItem $BinDir -Recurse -Filter *.pdb -File | Remove-Item -Force
        }
    } finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $BinDir "MissionPlanner.exe"))) { throw "MissionPlanner.exe not found in $BinDir - build first (or drop -SkipBuild)." }

# ---------------------------------------------------------------- 2. clean
Write-Step "Removing plugin DLLs that duplicate root DLLs"
$plugins = Join-Path $BinDir "plugins"
if (Test-Path $plugins) {
    Get-ChildItem $plugins -Recurse -File | ForEach-Object {
        $rootCopy = $_.FullName -replace '\\plugins\\', '\'
        if (Test-Path $rootCopy -PathType Leaf) { Remove-Item $_.FullName -Force }
    }
}

$Version = (Get-Item (Join-Path $BinDir "MissionPlanner.exe")).VersionInfo.ProductVersion.Trim()
$BaseName = ($ProductName -replace '[^A-Za-z0-9]', '') + "-" + $Version
Write-Host "Version $Version  ->  $BaseName"
New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---------------------------------------------------------------- 3. sign binaries
if ($CanSign) {
    Write-Step "Signing binaries"
    $toSign = @((Join-Path $BinDir "MissionPlanner.exe"))
    if ($SignAllBinaries) {
        $toSign = Get-ChildItem $BinDir -Recurse -Include *.exe, *.dll -File |
            Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne "Valid" } |
            ForEach-Object { $_.FullName }
    }
    Invoke-Sign $toSign
}

# ---------------------------------------------------------------- 4. zip
if (-not $SkipZip) {
    Write-Step "Creating zip"
    $zip = Join-Path $OutDir "$BaseName.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $sevenZip = "${env:ProgramFiles}\7-Zip\7z.exe"
    if (Test-Path $sevenZip) {
        Push-Location $BinDir
        try {
            & $sevenZip a -tzip -mx=5 -bso0 -bsp0 "-xr!gmapcache" "-xr!srtm" "-xr!logs" "-xr!*.tlog" "-xr!*.rlog" "-xr!*.etag" "-xr!config.xml" $zip "*"
            if ($LASTEXITCODE -ne 0) { throw "7z failed" }
        } finally { Pop-Location }
    } else {
        Write-Host "  7-Zip not found, using Compress-Archive (slower)"
        Compress-Archive -Path (Join-Path $BinDir "*") -DestinationPath $zip -CompressionLevel Optimal
    }
    Write-Host "  $zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

# ---------------------------------------------------------------- 5. msi
if (-not $SkipMsi) {
    Write-Step "Building MSI"
    $WixBin = Find-Wix
    if (-not $WixBin) { throw "WiX Toolset v3.11 not found (https://wixtoolset.org/docs/wix3/). Set the WIX environment variable or install it." }

    Push-Location $Root
    try {
        & $MSBuild -v:m -restore -t:Build "-p:Configuration=$Configuration" "wix\wix.csproj"
        if ($LASTEXITCODE -ne 0) { throw "wix generator build failed" }
    } finally { Pop-Location }

    $wixExe = Get-ChildItem $MsiDir -Recurse -Filter wix.exe | Select-Object -First 1
    if (-not $wixExe) { throw "wix.exe generator not found under $MsiDir" }

    Push-Location $MsiDir
    try {
        $genArgs = @(($BinDir + "\"), "MissionPlanner", "--manufacturer=$Manufacturer", "--product=$ProductName")
        if ($UpgradeCode -ne "") { $genArgs += "--upgradecode=$UpgradeCode" }
        if ($IncludeDrivers) { $genArgs += "--drivers" }
        & $wixExe.FullName @genArgs
        if ($LASTEXITCODE -ne 0) { throw "wix.exe generator failed" }

        $ext = @("-ext", "WixNetFxExtension", "-ext", "WixDifxAppExtension", "-ext", "WixUIExtension", "-ext", "WixUtilExtension", "-ext", "WixIisExtension")
        Remove-Item installer.wixobj -ErrorAction SilentlyContinue
        & (Join-Path $WixBin "candle.exe") -nologo installer.wxs @ext
        if ($LASTEXITCODE -ne 0) { throw "candle failed" }

        $msi = Join-Path $OutDir "$BaseName.msi"
        if (Test-Path $msi) { Remove-Item $msi -Force }
        & (Join-Path $WixBin "light.exe") -nologo -sval -spdb installer.wixobj (Join-Path $WixBin "difxapp_x86.wixlib") -o $msi @ext
        if ($LASTEXITCODE -ne 0) { throw "light failed" }
    } finally { Pop-Location }

    if ($CanSign) {
        Write-Step "Signing MSI"
        Invoke-Sign @($msi)
    }
    Write-Host "  $msi  ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
}

# ---------------------------------------------------------------- 6. hashes
Write-Step "Checksums"
$sums = Get-ChildItem $OutDir -File | Where-Object { $_.Name -like "$BaseName.*" } | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
$sums | Set-Content (Join-Path $OutDir "SHA256SUMS.txt") -Encoding ascii
$sums | ForEach-Object { Write-Host "  $_" }

if ($CanSign) {
    Write-Step "Signature check"
    Get-ChildItem $OutDir -File | Where-Object { $_.Name -like "$BaseName.msi" } | ForEach-Object {
        $sig = Get-AuthenticodeSignature $_.FullName
        Write-Host ("  {0}: {1} ({2})" -f $_.Name, $sig.Status, $sig.SignerCertificate.Subject)
    }
}

Write-Host ""
Write-Host "Done. Output in $OutDir" -ForegroundColor Green
