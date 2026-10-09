# Removes everything install.ps1 created. Leaves your files alone.

$ErrorActionPreference = "Stop"

$progId     = "MarkdownEditor.md"
$appKey     = "MarkdownEditor.exe"
$extensions = @(".md", ".markdown", ".mdown", ".mkd")
$classes    = "HKCU:\Software\Classes"
$removed    = @()

if (Test-Path "$classes\$progId") {
    Remove-Item "$classes\$progId" -Recurse -Force
    $removed += $progId
}
if (Test-Path "$classes\Applications\$appKey") {
    Remove-Item "$classes\Applications\$appKey" -Recurse -Force
    $removed += "Applications\$appKey"
}

foreach ($ext in $extensions) {
    $owp = "$classes\$ext\OpenWithProgids"
    if (Test-Path $owp) {
        Remove-ItemProperty -Path $owp -Name $progId -ErrorAction SilentlyContinue
        # drop the key if we left it empty
        if (-not (Get-Item $owp).GetValueNames()) { Remove-Item $owp -Force }
    }
    $extKey = "$classes\$ext"
    if (Test-Path $extKey) {
        $cur = (Get-ItemProperty -Path $extKey -Name "(default)" -ErrorAction SilentlyContinue)."(default)"
        if ($cur -eq $progId) {
            Remove-ItemProperty -Path $extKey -Name "(default)" -ErrorAction SilentlyContinue
            Remove-ItemProperty -Path $extKey -Name "PerceivedType" -ErrorAction SilentlyContinue
            $removed += $ext
        }
        # remove the extension key only if it is now completely empty
        $item = Get-Item $extKey
        if (-not $item.GetValueNames() -and -not $item.GetSubKeyNames()) {
            Remove-Item $extKey -Force
        }
    }
}

Add-Type -Namespace Win32Unassoc -Name Shell -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr a, System.IntPtr b);
"@
[Win32Unassoc.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

# drop the precompiled native image if install.ps1 created one
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$ngen = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\ngen.exe"
$exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "MarkdownEditor.exe"
if ($isAdmin -and (Test-Path $ngen) -and (Test-Path $exe)) {
    & $ngen uninstall $exe /nologo 2>&1 | Out-Null
}

if ($removed.Count) {
    Write-Host "Removed: $($removed -join ', ')" -ForegroundColor Green
} else {
    Write-Host "Nothing to remove - it was not installed."
}
Write-Host "Windows will fall back to whatever handled markdown before."
