param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$Digest,
    [Parameter(Mandatory)][long]$Size,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][version]$Version,
    [int]$ParentId
)
$ErrorActionPreference = 'Stop'
$app = Join-Path (Split-Path $PSScriptRoot -Parent) 'Clario.Presenter.exe'
$reopen = $false
try {
    # Keep the verified file open read-only until Setup has finished.
    $handle = [IO.File]::Open($Installer, 'Open', 'Read', 'Read')
    if ($handle.Length -ne $Size -or (Get-FileHash -InputStream $handle).Hash -ne $Digest) { throw 'Digest' }
    $signature = Get-AuthenticodeSignature -LiteralPath $Installer
    if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate) { throw 'Signature' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $pin = [BitConverter]::ToString($sha.ComputeHash($signature.SignerCertificate.RawData)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($Publisher -ne $pin) { throw 'Publisher' }
    $info = (Get-Item -LiteralPath $Installer).VersionInfo
    $actual = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart)
    if ($actual -ne $Version -or $info.FilePrivatePart -ne 0) { throw 'Version' }
    if ($ParentId) {
        $parent = Get-Process -Id $ParentId
        if ($parent.Path -ne $app) { throw 'Unexpected application' }
        [Console]::WriteLine('READY')
        if (!$parent.WaitForExit(30000)) { throw 'Clario still running' }
        $reopen = $true
        $setup = Start-Process -FilePath $Installer -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NOCLOSEAPPLICATIONS','/NORESTART' -PassThru -Wait -WindowStyle Hidden
        if ($setup.ExitCode -ne 0) { throw "Installer $($setup.ExitCode)" }
    }
} catch { exit 1 }
finally {
    if ($handle) { $handle.Dispose() }
    if ($reopen -and (Test-Path -LiteralPath $app)) { Start-Process -FilePath $app -WindowStyle Normal }
}
