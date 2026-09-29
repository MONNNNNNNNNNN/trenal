# Runs once per session, before the user's profile. Keep it fast: it delays the first prompt.

# No progress UI in this host, and rendering it slows Invoke-WebRequest badly.
$global:ProgressPreference = 'SilentlyContinue'

# The stock Clear-Host shells out to `clear` on Unix; there are no native commands on iOS.
function global:Clear-Host { $Host.UI.Write("`e[2J`e[3J`e[H") }
Set-Alias -Name clear -Value Clear-Host -Scope Global -Option AllScope -Force
Set-Alias -Name cls -Value Clear-Host -Scope Global -Option AllScope -Force

function global:prompt {
    $path = $ExecutionContext.SessionState.Path.CurrentLocation.Path
    if ($path.StartsWith($HOME, [StringComparison]::Ordinal)) { $path = '~' + $path.Substring($HOME.Length) }
    "$($PSStyle.Foreground.BrightBlue)$path$($PSStyle.Reset) $('>' * ($NestedPromptLevel + 1)) "
}

$global:PROFILE = [IO.Path]::Combine($HOME, '.config', 'powershell', 'Microsoft.PowerShell_profile.ps1')
foreach ($__profile in [IO.Path]::Combine($HOME, '.config', 'powershell', 'profile.ps1'), $PROFILE) {
    if (Test-Path -LiteralPath $__profile) { . $__profile }
}
Remove-Variable __profile
Set-Location $HOME
