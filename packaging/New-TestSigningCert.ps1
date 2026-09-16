<#
.SYNOPSIS
    Create a self-signed code-signing certificate for LOCAL TESTING of the packaging pipeline.

.DESCRIPTION
    Makes a certificate in the CurrentUser\My store whose subject is the publisher name you choose
    (default "Nevermind"), so the name shown in signature details is the organisation, not a Windows
    user name. Prints the thumbprint to pass to package.ps1 -CertThumbprint.

    A self-signed certificate is NOT trusted by other machines: Windows will still say
    "Unknown publisher" / SmartScreen there. For distribution buy an OV or EV code-signing certificate
    from a CA (DigiCert, Sectigo, GlobalSign, SSL.com ...) issued to the company name and use its
    thumbprint or PFX instead. See README.md.

.PARAMETER TrustLocally
    Also import the certificate into this user's Trusted Root and Trusted Publishers stores so that
    signed files verify as Valid on THIS machine only. Windows shows a confirmation prompt for the
    root store import. Remove later with Remove-TestSigningCert.ps1 style cleanup shown in README.md.

.EXAMPLE
    .\packaging\New-TestSigningCert.ps1 -Subject "Nevermind" -TrustLocally
#>
[CmdletBinding()]
param(
    [string]$Subject = "Nevermind",
    [int]$Years = 3,
    [string]$ExportCerPath = "",
    [switch]$TrustLocally
)

$ErrorActionPreference = "Stop"

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=$Subject" `
    -FriendlyName "$Subject code signing (self-signed test)" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears($Years)

Write-Host "Created self-signed code-signing certificate"
Write-Host "  Subject    : $($cert.Subject)"
Write-Host "  Thumbprint : $($cert.Thumbprint)"
Write-Host "  Expires    : $($cert.NotAfter)"
Write-Host "  Store      : Cert:\CurrentUser\My"

if ($ExportCerPath -ne "") {
    Export-Certificate -Cert $cert -FilePath $ExportCerPath | Out-Null
    Write-Host "  Exported   : $ExportCerPath (public key only, safe to share)"
}

if ($TrustLocally) {
    $tmp = [IO.Path]::GetTempFileName() + ".cer"
    Export-Certificate -Cert $cert -FilePath $tmp | Out-Null
    try {
        Import-Certificate -FilePath $tmp -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
        Import-Certificate -FilePath $tmp -CertStoreLocation "Cert:\CurrentUser\TrustedPublisher" | Out-Null
        Write-Host "  Trusted on this machine for the current user (Root + TrustedPublisher)."
    } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "Use it with:  .\packaging\package.ps1 -CertThumbprint $($cert.Thumbprint)" -ForegroundColor Green
