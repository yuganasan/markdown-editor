# Registers MarkdownEditor.exe as a handler for markdown files.
#
# Writes only under HKEY_CURRENT_USER - no admin rights, nothing machine-wide,
# and uninstall.ps1 removes every key this creates.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = Join-Path $root "MarkdownEditor.exe"

if (-not (Test-Path $exe)) { throw "MarkdownEditor.exe not found. Run build.ps1 first." }

$progId     = "MarkdownEditor.md"
$appKey     = "MarkdownEditor.exe"
$extensions = @(".md", ".markdown", ".mdown", ".mkd")
$command    = '"' + $exe + '" "%1"'
$classes    = "HKCU:\Software\Classes"

function Set-Key($path, $name, $value) {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    if ($null -ne $name) { New-ItemProperty -Path $path -Name $name -Value $value -PropertyType String -Force | Out-Null }
}

# ---- the file type itself --------------------------------------------------
Set-Key "$classes\$progId" "(default)" "Markdown Document"
Set-Key "$classes\$progId\DefaultIcon" "(default)" ($exe + ",0")
Set-Key "$classes\$progId\shell\open" "(default)" "Open in MD Editor"
Set-Key "$classes\$progId\shell\open\command" "(default)" $command

# ---- make it appear in the "Open with" list ---------------------------------
Set-Key "$classes\Applications\$appKey" "FriendlyAppName" "MD Editor"
Set-Key "$classes\Applications\$appKey\shell\open\command" "(default)" $command
Set-Key "$classes\Applications\$appKey\DefaultIcon" "(default)" ($exe + ",0")
foreach ($ext in $extensions) {
    Set-Key "$classes\Applications\$appKey\SupportedTypes" $ext ""
}

# ---- bind the extensions ----------------------------------------------------
# Windows 11 will not accept a default handler written by a script: the real
# switch lives in Explorer's UserChoice key, which is signed with a hash only
# Explorer itself can produce. So we register the handler properly and let the
# one-time "Open with" prompt do the final step.
foreach ($ext in $extensions) {
    Set-Key "$classes\$ext\OpenWithProgids" $progId ""
    $userChoice = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$ext\UserChoice"
    if (-not (Test-Path $userChoice)) {
        Set-Key "$classes\$ext" "(default)" $progId
        Set-Key "$classes\$ext" "PerceivedType" "text"
    }
}

# ---- tell Explorer to pick up the change ------------------------------------
Add-Type -Namespace Win32Assoc -Name Shell -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr a, System.IntPtr b);
"@
[Win32Assoc.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

# ---- optional: precompile so it starts faster ------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$ngen = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\ngen.exe"
$ngenDone = $false
if ($isAdmin -and (Test-Path $ngen)) {
    Write-Host "Precompiling (ngen)..."
    & $ngen install $exe /nologo | Out-Null
    $ngenDone = ($LASTEXITCODE -eq 0)
}

Write-Host ""
Write-Host "Registered." -ForegroundColor Green
Write-Host "  Program    : $exe"
Write-Host "  Handles    : $($extensions -join ', ')"
Write-Host ""
Write-Host "  One-time step Windows requires you to do yourself:" -ForegroundColor Yellow
Write-Host "    Right-click any .md file"
Write-Host "    > Open with > Choose another app"
Write-Host "    > MD Editor  (expand 'More apps' if you do not see it)"
Write-Host "    > tick 'Always use this app', then OK"
Write-Host ""
Write-Host "  From then on double-clicking a .md file opens it here."
if ($ngenDone) {
    Write-Host "  Precompiled with ngen - it will start noticeably faster."
} else {
    Write-Host ""
    Write-Host "  Optional: run this script from an elevated PowerShell to have it"
    Write-Host "  precompiled with ngen, which roughly halves startup time."
}
Write-Host "  Undo any time with: uninstall.ps1"
