# Unix command shims. iOS apps can't exec binaries, so the everyday commands people type
# are PowerShell functions here. Flags follow the real tools; behaviour covers common use,
# not every option. Functions take raw $args so unix flags (-o, -H, --data) aren't
# swallowed by PowerShell parameter binding.

# PowerShell drops these aliases on Unix because the native binaries exist; here they don't.
$aliases = @{
    ls = 'Get-ChildItem'; cat = 'Get-Content'; cp = 'Copy-Item'; mv = 'Move-Item'; rm = 'Remove-Item'
    rmdir = 'Remove-Item'; ps = 'Get-Process'; kill = 'Stop-Process'; sleep = 'Start-Sleep'
    sort = 'Sort-Object'; tee = 'Tee-Object'; man = 'Get-Help'
}
foreach ($a in $aliases.GetEnumerator()) { Set-Alias -Name $a.Key -Value $a.Value -Scope Global -Force -Option AllScope }

function Split-Flags {
    # Expands bundled short flags: -sSL -> -s -S -L. Long flags and values pass through.
    param([object[]] $Arguments)
    foreach ($a in $Arguments) {
        $s = [string]$a
        # Only letters that are argument-less flags in curl/wget/grep/head/wc, so -XPOST stays intact.
        if ($s -cmatch '^-[sSLkfiIvOqnlrwcHFoER]{2,}$') { $s.Substring(1).ToCharArray() | ForEach-Object { "-$_" } }
        else { $s }
    }
}

function curl {
    $argv = @(Split-Flags $args)
    $p = @{ Uri = $null; Method = 'GET'; Headers = @{}; ErrorAction = 'Stop' }
    $out = $null; $remoteName = $false; $headOnly = $false; $include = $false; $fail = $false
    for ($i = 0; $i -lt $argv.Count; $i++) {
        $a = $argv[$i]
        switch -CaseSensitive ($a) {
            { $_ -in '-o', '--output' } { $out = $argv[++$i]; continue }
            { $_ -in '-O', '--remote-name' } { $remoteName = $true; continue }
            { $_ -in '-X', '--request' } { $p.Method = $argv[++$i]; continue }
            { $_ -cmatch '^-X(.+)$' } { $p.Method = $Matches[1]; continue }
            { $_ -in '-H', '--header' } {
                $k, $v = $argv[++$i] -split ':\s*', 2
                $p.Headers[$k] = $v; continue
            }
            { $_ -in '-d', '--data', '--data-raw', '--data-binary' } {
                $d = $argv[++$i]
                if ($d.StartsWith('@')) { $d = Get-Content -Raw -LiteralPath $d.Substring(1) }
                $p.Body = $d
                if ($p.Method -eq 'GET') { $p.Method = 'POST' }
                if (-not $p.Headers.ContainsKey('Content-Type')) { $p.ContentType = 'application/x-www-form-urlencoded' }
                continue
            }
            '--json' {
                $p.Body = $argv[++$i]; $p.ContentType = 'application/json'
                $p.Headers['Accept'] = 'application/json'
                if ($p.Method -eq 'GET') { $p.Method = 'POST' }
                continue
            }
            { $_ -in '-u', '--user' } {
                $b = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($argv[++$i]))
                $p.Headers['Authorization'] = "Basic $b"; continue
            }
            { $_ -in '-A', '--user-agent' } { $p.UserAgent = $argv[++$i]; continue }
            { $_ -in '-I', '--head' } { $headOnly = $true; $p.Method = 'HEAD'; continue }
            { $_ -in '-i', '--include' } { $include = $true; continue }
            { $_ -in '-k', '--insecure' } { $p.SkipCertificateCheck = $true; continue }
            { $_ -in '-f', '--fail' } { $fail = $true; continue }
            { $_ -in '-L', '--location', '-s', '--silent', '-S', '--show-error', '-v', '--compressed' } { continue }
            default {
                if ($a.StartsWith('-')) { throw "curl: unsupported option $a" }
                $p.Uri = $a
            }
        }
    }
    if (-not $p.Uri) { throw 'curl: no URL specified' }
    if ($remoteName) { $out = [IO.Path]::GetFileName(([Uri]$p.Uri).AbsolutePath); if (-not $out) { $out = 'index.html' } }
    if (-not $fail) { $p.SkipHttpErrorCheck = $true }
    if ($out) { $p.OutFile = $out; $p.PassThru = $true }

    $r = Invoke-WebRequest @p
    if ($headOnly -or $include) {
        "HTTP/$($r.BaseResponse.Version) $([int]$r.StatusCode) $($r.StatusDescription)"
        foreach ($h in $r.Headers.GetEnumerator()) { "$($h.Key): $($h.Value -join ', ')" }
        ''
    }
    if (-not $headOnly -and -not $out) {
        if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { $r.Content }
    }
}

function wget {
    $argv = @(Split-Flags $args)
    $out = $null; $urls = @()
    for ($i = 0; $i -lt $argv.Count; $i++) {
        switch -CaseSensitive ($argv[$i]) {
            { $_ -in '-O', '--output-document' } { $out = $argv[++$i]; continue }
            { $_ -in '-q', '--quiet', '-nv' } { continue }
            default { $urls += $argv[$i] }
        }
    }
    foreach ($u in $urls) {
        $file = if ($out) { $out } else { [IO.Path]::GetFileName(([Uri]$u).AbsolutePath) }
        if (-not $file) { $file = 'index.html' }
        if ($file -eq '-') { (Invoke-WebRequest -Uri $u -ErrorAction Stop).Content; continue }
        Invoke-WebRequest -Uri $u -OutFile $file -ErrorAction Stop
        "saved $file ($((Get-Item -LiteralPath $file).Length) bytes)"
    }
}

function grep {
    $argv = @(Split-Flags $args)
    $opt = @{ i = $false; v = $false; n = $false; r = $false; l = $false; c = $false; F = $false; o = $false; w = $false; H = $false }
    $pattern = $null; $paths = @()
    for ($i = 0; $i -lt $argv.Count; $i++) {
        $a = $argv[$i]
        if ($a -eq '-e') { $pattern = $argv[++$i]; continue }
        if ($a -cmatch '^-([ivnrlcFowHER])$') {
            $f = $Matches[1]
            if ($f -in 'E') { continue }
            if ($f -eq 'R') { $f = 'r' }
            $opt[$f] = $true; continue
        }
        if ($a -in '--ignore-case') { $opt.i = $true; continue }
        if ($a -in '--invert-match') { $opt.v = $true; continue }
        if ($a -in '--recursive') { $opt.r = $true; continue }
        if ($null -eq $pattern) { $pattern = $a } else { $paths += $a }
    }
    if ($null -eq $pattern) { throw 'usage: grep [-ivnrlcFowH] PATTERN [FILE...]' }
    $rx = if ($opt.F) { [regex]::Escape($pattern) } else { $pattern }
    if ($opt.w) { $rx = "\b(?:$rx)\b" }
    $ro = if ($opt.i) { [Text.RegularExpressions.RegexOptions]::IgnoreCase } else { [Text.RegularExpressions.RegexOptions]::None }
    $regex = [regex]::new($rx, $ro)

    $scan = {
        param($lines, $label)
        $n = 0; $count = 0
        foreach ($line in $lines) {
            $n++
            $hit = $regex.IsMatch($line)
            if ($hit -eq $opt.v) { continue }
            $count++
            if ($opt.l) { $label; return }
            if ($opt.c) { continue }
            $prefix = ''
            if ($label -and ($opt.H -or $showNames)) { $prefix += "${label}:" }
            if ($opt.n) { $prefix += "${n}:" }
            if ($opt.o -and -not $opt.v) { foreach ($m in $regex.Matches($line)) { "$prefix$($m.Value)" } }
            else { "$prefix$line" }
        }
        if ($opt.c) { if ($label -and $showNames) { "${label}:$count" } else { $count } }
    }

    if ($paths.Count -eq 0 -and -not $opt.r) {
        $showNames = $false
        & $scan @($input | Out-String -Stream) $null
        return
    }
    if ($paths.Count -eq 0) { $paths = @('.') }
    $files = foreach ($p in $paths) {
        if (Test-Path -LiteralPath $p -PathType Container) {
            if (-not $opt.r) { Write-Error "grep: ${p}: Is a directory"; continue }
            Get-ChildItem -LiteralPath $p -File -Recurse | ForEach-Object FullName
        } else { $p }
    }
    $showNames = @($files).Count -gt 1 -or $opt.r
    foreach ($f in $files) {
        $label = if ($opt.r) { Resolve-Path -Relative -LiteralPath $f } else { $f }
        & $scan (Get-Content -LiteralPath $f -ErrorAction Continue) $label
    }
}

function Get-Lines {
    # head/tail: lines of the given files, else the pipeline objects unchanged
    # (so `1..50 | head -5` stays numbers, unlike text-only unix pipes).
    param($Files, $PipelineInput)
    if ($Files.Count -gt 0) { foreach ($f in $Files) { Get-Content -LiteralPath $f } }
    else { $PipelineInput }
}

function Read-CountFlag {
    param([object[]] $Arguments, [int] $Default)
    $n = $Default; $rest = @()
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $a = [string]$Arguments[$i]
        if ($a -eq '-n') { $n = [int]$Arguments[++$i] }
        elseif ($a -match '^-n?(\d+)$') { $n = [int]$Matches[1] }
        else { $rest += $a }
    }
    Write-Output $n
    Write-Output -NoEnumerate $rest
}

function head {
    $n, $files = Read-CountFlag $args 10
    Get-Lines $files $input | Select-Object -First $n
}

function tail {
    $n, $files = Read-CountFlag $args 10
    Get-Lines $files $input | Select-Object -Last $n
}

function wc {
    $flags = @($args | Where-Object { $_ -match '^-[lwc]+$' }) -join '' -replace '-', ''
    $files = @($args | Where-Object { $_ -notmatch '^-[lwc]+$' })
    if (-not $flags) { $flags = 'lwc' }
    $text = if ($files.Count) { ($files | ForEach-Object { Get-Content -Raw -LiteralPath $_ }) -join '' } else { ($input | Out-String) }
    $cols = @()
    if ($flags.Contains('l')) { $cols += ([regex]::Matches($text, "`n")).Count }
    if ($flags.Contains('w')) { $cols += @($text -split '\s+' | Where-Object { $_ }).Count }
    if ($flags.Contains('c')) { $cols += [Text.Encoding]::UTF8.GetByteCount($text) }
    ($cols -join ' ') + $(if ($files.Count -eq 1) { " $($files[0])" } else { '' })
}

function touch {
    foreach ($f in $args) {
        if (Test-Path -LiteralPath $f) { (Get-Item -LiteralPath $f).LastWriteTime = Get-Date }
        else { $null = New-Item -ItemType File -Path $f }
    }
}

function which {
    foreach ($name in $args) {
        $c = Get-Command -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $c) { Write-Error "which: no $name"; continue }
        switch ($c.CommandType) {
            'Alias' { "${name}: alias for $($c.Definition)" }
            'Function' { "${name}: function (Trenal shim or profile)" }
            'Cmdlet' { "${name}: cmdlet from $($c.Source)" }
            default { $c.Source }
        }
    }
}

function env { Get-ChildItem Env: | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" } }

function export {
    # export NAME=value
    foreach ($a in $args) {
        $k, $v = ([string]$a) -split '=', 2
        Set-Item -Path "Env:$k" -Value $v
    }
}

function whoami { if ($env:USER) { $env:USER } else { [Environment]::UserName } }

function uname {
    if ($args -contains '-a') { "$([Environment]::OSVersion.VersionString) $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
    else { [Runtime.InteropServices.RuntimeInformation]::OSDescription }
}

function ssh {
    # ssh [-p port] [-i identity] [user@]host [command...]
    $argv = @($args); $p = @{}; $rest = @()
    for ($i = 0; $i -lt $argv.Count; $i++) {
        $a = [string]$argv[$i]
        if ($rest.Count -eq 0 -and $a -ceq '-p') { $p.Port = [int]$argv[++$i] }
        elseif ($rest.Count -eq 0 -and $a -ceq '-i') { $p.IdentityFile = $argv[++$i] }
        elseif ($rest.Count -eq 0 -and $a -cmatch '^-[tTAqvCN46]+$') { }
        elseif ($rest.Count -eq 0 -and $a -cmatch '^-o$') { $i++ }
        else { $rest += $a }
    }
    if ($rest.Count -eq 0) { throw 'usage: ssh [-p port] [-i identity] [user@]host [command]' }
    $p.Target = $rest[0]
    if ($rest.Count -gt 1) { $p.Command = $rest[1..($rest.Count - 1)] }
    Enter-SshSession @p
}

function scp {
    # scp [-P port] [-i identity] [-r] SOURCE DEST   (one side user@host:path)
    $argv = @(Split-Flags $args); $p = @{}; $paths = @()
    for ($i = 0; $i -lt $argv.Count; $i++) {
        switch -CaseSensitive ([string]$argv[$i]) {
            '-P' { $p.Port = [int]$argv[++$i]; continue }
            '-i' { $p.IdentityFile = $argv[++$i]; continue }
            '-r' { $p.Recurse = $true; continue }
            { $_ -in '-q', '-p', '-C', '-v' } { continue }
            default { $paths += $argv[$i] }
        }
    }
    if ($paths.Count -ne 2) { throw 'usage: scp [-P port] [-i identity] [-r] SOURCE DEST' }
    Copy-SshItem -Source $paths[0] -Destination $paths[1] @p
}

function ssh-keygen {
    # ssh-keygen [-t ed25519] [-f file] [-C comment] [-N ''] ; only ed25519 is generated
    $argv = @($args); $p = @{}
    for ($i = 0; $i -lt $argv.Count; $i++) {
        switch -CaseSensitive ([string]$argv[$i]) {
            '-f' { $p.Path = $argv[++$i]; continue }
            '-C' { $p.Comment = $argv[++$i]; continue }
            '-t' { $t = $argv[++$i]; if ($t -ne 'ed25519') { Write-Warning "only ed25519 keys are generated (asked for $t)" }; continue }
            '-N' { $i++; continue }
            '-q' { continue }
            default { throw "ssh-keygen: unsupported option $_" }
        }
    }
    New-SshKey @p
}

function git {
    # Raw $args keep git's flags (-m, -b, --depth) away from PowerShell parameter binding.
    Invoke-Git -Arguments $args
}

Export-ModuleMember -Function curl, wget, grep, head, tail, wc, touch, which, env, export, whoami, uname, ssh, scp, ssh-keygen, git
