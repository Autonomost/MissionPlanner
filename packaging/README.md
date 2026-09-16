# Packaging: zip, MSI and code signing

Everything here runs on Windows with Visual Studio 2022+ (MSBuild), the Windows 10/11 SDK
(`signtool.exe`) and the [WiX Toolset v3.11](https://wixtoolset.org/docs/wix3/). 7-Zip is used for
the zip if installed, otherwise PowerShell's `Compress-Archive`.

```powershell
# full build + zip + msi, unsigned
.\packaging\package.ps1

# same, signed with a certificate from the Windows certificate store
.\packaging\package.ps1 -CertThumbprint <thumbprint>

# signed with a PFX file, skipping the (slow) build because bin\Release is already current
.\packaging\package.ps1 -SkipBuild -PfxPath C:\certs\company.pfx -PfxPassword (Read-Host -AsSecureString)
```

Output lands in `dist\`:

| File | What it is |
|------|------------|
| `MissionPlanner-<version>.zip` | portable build of `bin\Release\net461`, unzip and run |
| `MissionPlanner-<version>.msi` | Windows Installer, per-machine, Start menu shortcut, file associations |
| `SHA256SUMS.txt` | hashes to publish next to the downloads |

Useful switches: `-SkipBuild`, `-SkipZip`, `-SkipMsi`, `-SignAllBinaries` (sign every unsigned
exe/dll, not only `MissionPlanner.exe`), `-IncludeDrivers` (run the legacy DPInst USB driver install
from the MSI; off by default because Windows 10/11 ship the USB serial driver ArduPilot boards use),
`-Manufacturer`, `-ProductName`, `-UpgradeCode`.

## Names that appear to end users

* **Publisher / Manufacturer** in Programs and Features and the MSI properties comes from
  `-Manufacturer` (default `Nevermind`).
* **Publisher in the UAC prompt and file Properties > Digital Signatures** comes from the
  certificate's subject (CN). It is whatever name the certificate was issued to, never the Windows
  account that ran the build.
* The fork installs with its own `UpgradeCode`, so it will not silently replace an official Mission
  Planner install. Pass `-UpgradeCode "{625389D7-EB3C-4d77-A5F6-A285CF99437D}"` (upstream's value)
  if you want this MSI to upgrade over the official installer instead of installing side by side.
* Compiled exe/dll files and their PDBs normally embed the source path of the build machine
  (`C:\Users\<name>\...\<repo folder>\...`) and, via SourceLink, the git remote URL. `package.ps1`
  builds with the compiler's `PathMap` option (`-PathMapTo`, default `C:\src\MissionPlanner`),
  SourceLink disabled and `DebugType=embedded`, so no `.pdb` files ship and no build-machine path
  or repository name is written into the binaries. Line numbers in crash logs are kept. This only
  applies when the script does the build: a Visual Studio build followed by `-SkipBuild` will
  still carry the real paths. Pass `-PathMapTo ""` to build the normal way.

## What "signing a Windows app" means

Authenticode signing attaches a digital signature to a file (exe, dll, msi). The signature is a
hash of the file encrypted with the private key of a **code-signing certificate**. Windows verifies:

1. the file has not been changed since it was signed (hash matches), and
2. the certificate chains to a Certificate Authority that Windows trusts, and
3. via the **timestamp** (`/tr`), that the signature was made while the certificate was valid, so
   the signature stays valid after the certificate expires.

What the user sees:

| Situation | UAC / SmartScreen |
|-----------|-------------------|
| unsigned | "Unknown publisher", yellow UAC, SmartScreen "Windows protected your PC" |
| self-signed (test cert) | same as unsigned on any machine that has not imported the cert |
| OV certificate from a CA | publisher name shown, blue UAC; SmartScreen warns until the cert builds reputation (days to weeks of downloads) |
| EV certificate (hardware token / cloud HSM) | publisher name shown, SmartScreen reputation immediately |

Signing does not encrypt or hide anything, and does not by itself make Windows trust the program.
It only proves who published the file and that it is intact.

### Getting a real certificate

Buy a code-signing certificate from a CA (DigiCert, Sectigo, GlobalSign, SSL.com, Certum for
individuals). Since 2023 CA rules require the private key on hardware (USB token or the CA's cloud
signing service), so you will usually sign by thumbprint with the token plugged in, not from a PFX.
The certificate is issued to the **organisation's legal name** (or an individual's legal name for
an individual cert), which is the name Windows shows. Expect identity validation paperwork.

### Local testing without buying anything

```powershell
.\packaging\New-TestSigningCert.ps1 -Subject "Nevermind" -TrustLocally
.\packaging\package.ps1 -SkipBuild -CertThumbprint <thumbprint printed above>
Get-AuthenticodeSignature dist\MissionPlanner-*.msi
```

This proves the pipeline works and lets you inspect the signature UI. Other machines will still see
"Unknown publisher" until you sign with a CA-issued certificate. To remove the test cert later:

```powershell
Get-ChildItem Cert:\CurrentUser\My, Cert:\CurrentUser\Root, Cert:\CurrentUser\TrustedPublisher |
    Where-Object { $_.Subject -eq "CN=Nevermind" -and $_.Issuer -eq "CN=Nevermind" } | Remove-Item
```

## How the MSI is built

`wix\wix.csproj` is a small generator (`Msi\net472\wix.exe`) that walks the build output and writes
`Msi\installer.wxs`; `candle`/`light` compile that into the MSI. `package.ps1` drives all of it.
The generator accepts `--manufacturer=`, `--product=`, `--upgradecode=` and `--drivers`.
