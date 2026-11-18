[CmdletBinding()]
param(
    [string]$InnoCompiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    [string]$CrtDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Clario.Presenter.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$output = Join-Path $root "bin\installer\$version"
$payload = Join-Path $root ("bin\installer-staging\" + [guid]::NewGuid())
if (!(Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) { throw "Inno Setup 6 tidak ditemukan di $InnoCompiler" }
if (!$CrtDirectory) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'Pasang komponen Visual C++ x64 atau tentukan -CrtDirectory.' }
    $crt = Get-Item "$vs\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT" |
        Sort-Object { [version]$_.Parent.Parent.Name } -Descending | Select-Object -First 1
    $CrtDirectory = $crt.FullName
}
if (!(Test-Path "$CrtDirectory\vcruntime140_1.dll")) { throw 'Folder runtime Visual C++ x64 tidak valid.' }

$properties = @('-p:Platform=x64', '-p:WindowsPackageType=None', '-p:WindowsAppSDKSelfContained=true',
    '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false', '-p:DebugType=none',
    '-p:OutputPath=bin\installer-build\', '-p:IntermediateOutputPath=obj\installer-build\')

Push-Location $root
try {
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $payload @properties
    if ($LASTEXITCODE) { throw 'Publikasi aplikasi gagal.' }
    foreach ($file in 'Clario.Presenter.exe','coreclr.dll','Microsoft.UI.Xaml.dll','Microsoft.Graphics.Canvas.dll','Clario.Presenter.pri','Assets\AppIcon.ico') {
        if (!(Test-Path -LiteralPath (Join-Path $payload $file))) { throw "Berkas distribusi belum lengkap: $file" }
    }
    Copy-Item "$CrtDirectory\*.dll" -Destination $payload

    # Include notices from the exact package versions selected by restore.
    $licenses = New-Item -ItemType Directory -Path (Join-Path $payload 'Licenses')
    Copy-Item "$PSScriptRoot\licenses\*" -Destination $licenses.FullName
    $assets = Get-Content (Join-Path $root 'obj\project.assets.json') -Raw | ConvertFrom-Json
    $paths = @($assets.libraries.PSObject.Properties.Value | Where-Object type -eq 'package' | ForEach-Object path)
    foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
        foreach ($dep in $framework.downloadDependencies) {
            $resolvedVersion = $dep.version.Trim('[',']').Split(',')[0].Trim()
            $paths += $dep.name.ToLowerInvariant() + '/' + $resolvedVersion
        }
    }
    foreach ($path in $paths | Sort-Object -Unique) {
        $packagePath = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $path } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$packagePath) { continue }
        $notices = Get-ChildItem -LiteralPath $packagePath -File | Where-Object Name -Match '^(license|notice|third-party-notices)(\.txt|\.md)?$'
        foreach ($notice in $notices) {
            Copy-Item $notice.FullName (Join-Path $licenses.FullName ($path.Replace('/','-') + '-' + $notice.Name))
        }
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    & $InnoCompiler /Q "/DAppVersion=$version" "/DPayload=$payload" "/O$output" (Join-Path $PSScriptRoot 'Clario.iss')
    if ($LASTEXITCODE) { throw 'Pembuatan installer gagal.' }
    $installer = Get-Item (Join-Path $output 'Clario-Presenter-Setup-x64.exe')
    $digest = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content (Join-Path $output 'SHA256SUMS.txt') "$digest  $($installer.Name)" -Encoding ascii
    Write-Output $installer.FullName
}
finally {
    Pop-Location
    $stagingRoot = [IO.Path]::GetFullPath((Join-Path $root 'bin\installer-staging')) + '\'
    $resolved = [IO.Path]::GetFullPath($payload)
    if ($resolved.StartsWith($stagingRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
