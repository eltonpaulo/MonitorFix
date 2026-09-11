[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = (Get-Location).Path,

    [Parameter(Mandatory = $false)]
    [ValidateRange(10, 3600)]
    [int]$IntervalSeconds = 15,

    [Parameter(Mandatory = $false)]
    [switch]$Once
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-GitExecutable {
    $command = Get-Command git.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) { return $command.Path }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\bin\git.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    throw 'Git nao foi encontrado no PATH nem nos locais padrao do Windows.'
}

function Write-SyncLog {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet('Info', 'Success', 'Warning', 'Error')][string]$Level = 'Info'
    )

    $color = switch ($Level) {
        'Success' { 'Green' }
        'Warning' { 'Yellow' }
        'Error' { 'Red' }
        default { 'Cyan' }
    }
    Write-Host "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $Message" -ForegroundColor $color
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $previousErrorAction = $ErrorActionPreference
    try {
        # Windows PowerShell converte qualquer stderr nativo em ErrorRecord quando
        # ErrorActionPreference=Stop, inclusive avisos inofensivos do Git.
        $ErrorActionPreference = 'Continue'
        $output = @(& $script:GitExe -C $script:ResolvedProject @Arguments 2>&1)
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    if ($code -ne 0 -and -not $AllowFailure) {
        $details = ($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "Falha ao executar git $($Arguments -join ' ')`n$details"
    }

    return [pscustomobject]@{
        Code = $code
        Output = $output
    }
}

function Get-GitText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $result = Invoke-Git -Arguments $Arguments
    return (($result.Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
}

function Test-SensitiveUntrackedFiles {
    $result = Invoke-Git -Arguments @('ls-files', '--others', '--exclude-standard')
    $blocked = @()

    foreach ($relativePath in $result.Output) {
        $relative = $relativePath.ToString().Trim()
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }

        $name = [IO.Path]::GetFileName($relative)
        $isTemplate = $name -match '^\.env\.(example|sample|template)$'
        $looksSensitive = (
            ($name -match '^\.env($|\.)' -and -not $isTemplate) -or
            $name -match '\.(pem|p12|pfx|key)$' -or
            $name -match '^(credentials|secrets?)(\.|$)' -or
            $name -match '^id_(rsa|ed25519)(\.|$)'
        )

        if ($looksSensitive) { $blocked += $relative }
    }

    if ($blocked.Count -gt 0) {
        Write-SyncLog -Level Error -Message ("Sincronizacao pausada: arquivos possivelmente secretos nao ignorados: " + ($blocked -join ', '))
        Write-SyncLog -Level Warning -Message 'Adicione esses arquivos ao .gitignore ou confirme manualmente que podem ser publicados.'
        return $true
    }

    return $false
}

function Test-GitOperationInProgress {
    $gitDir = Get-GitText -Arguments @('rev-parse', '--absolute-git-dir')
    $markers = @(
        (Join-Path $gitDir 'MERGE_HEAD'),
        (Join-Path $gitDir 'CHERRY_PICK_HEAD'),
        (Join-Path $gitDir 'REVERT_HEAD'),
        (Join-Path $gitDir 'rebase-merge'),
        (Join-Path $gitDir 'rebase-apply')
    )
    return @($markers | Where-Object { Test-Path -LiteralPath $_ }).Count -gt 0
}

function Save-LocalChanges {
    $status = Get-GitText -Arguments @('status', '--porcelain', '--untracked-files=all')
    if ([string]::IsNullOrWhiteSpace($status)) { return $true }

    if (Test-SensitiveUntrackedFiles) { return $false }

    $nameResult = Invoke-Git -Arguments @('config', '--get', 'user.name') -AllowFailure
    $emailResult = Invoke-Git -Arguments @('config', '--get', 'user.email') -AllowFailure
    $name = (($nameResult.Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
    $email = (($emailResult.Output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($email)) {
        Write-SyncLog -Level Error -Message 'Sincronizacao pausada: configure user.name e user.email do Git para criar commits automaticos.'
        return $false
    }

    [void](Invoke-Git -Arguments @('add', '-A'))
    $diff = Invoke-Git -Arguments @('diff', '--cached', '--quiet') -AllowFailure
    if ($diff.Code -eq 1) {
        $device = if ([string]::IsNullOrWhiteSpace($env:COMPUTERNAME)) { 'Windows' } else { $env:COMPUTERNAME }
        $message = "sync automatico [$device] $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        [void](Invoke-Git -Arguments @('commit', '-m', $message))
        Write-SyncLog -Level Success -Message "Alteracoes locais salvas: $message"
    }
    elseif ($diff.Code -ne 0) {
        throw 'Nao foi possivel verificar as alteracoes preparadas para commit.'
    }

    return $true
}

function Sync-Once {
    try {
        if (Test-GitOperationInProgress) {
            Write-SyncLog -Level Warning -Message 'Ha merge, rebase ou cherry-pick em andamento. Sincronizacao aguardando resolucao manual.'
            return
        }

        $branchResult = Invoke-Git -Arguments @('symbolic-ref', '--quiet', '--short', 'HEAD') -AllowFailure
        if ($branchResult.Code -ne 0) {
            Write-SyncLog -Level Warning -Message 'Repositorio em detached HEAD. Selecione uma branch antes de sincronizar.'
            return
        }
        $branch = (($branchResult.Output | ForEach-Object { $_.ToString() }) -join '').Trim()

        $origin = Invoke-Git -Arguments @('remote', 'get-url', 'origin') -AllowFailure
        if ($origin.Code -ne 0) {
            Write-SyncLog -Level Warning -Message 'Remote origin nao configurado. Nenhum dado foi enviado.'
            return
        }

        $fetch = Invoke-Git -Arguments @('fetch', 'origin', '--prune') -AllowFailure
        if ($fetch.Code -ne 0) {
            Write-SyncLog -Level Error -Message ('Falha ao buscar o GitHub: ' + (($fetch.Output | ForEach-Object { $_.ToString() }) -join ' '))
            return
        }

        if (-not (Save-LocalChanges)) { return }

        $headResult = Invoke-Git -Arguments @('rev-parse', 'HEAD') -AllowFailure
        if ($headResult.Code -ne 0) {
            Write-SyncLog -Level Warning -Message 'Repositorio sem commit inicial. Crie um arquivo para iniciar a sincronizacao.'
            return
        }
        $head = (($headResult.Output | ForEach-Object { $_.ToString() }) -join '').Trim()

        $remoteRef = "refs/remotes/origin/$branch"
        $remoteExists = Invoke-Git -Arguments @('show-ref', '--verify', '--quiet', $remoteRef) -AllowFailure
        if ($remoteExists.Code -ne 0) {
            $publish = Invoke-Git -Arguments @('push', '--set-upstream', 'origin', $branch) -AllowFailure
            if ($publish.Code -eq 0) {
                Write-SyncLog -Level Success -Message "Branch '$branch' publicada no GitHub."
            }
            else {
                Write-SyncLog -Level Error -Message ('Falha ao publicar a branch: ' + (($publish.Output | ForEach-Object { $_.ToString() }) -join ' '))
            }
            return
        }

        $remoteHead = Get-GitText -Arguments @('rev-parse', "origin/$branch")
        if ($head -eq $remoteHead) { return }

        $localBehind = Invoke-Git -Arguments @('merge-base', '--is-ancestor', 'HEAD', "origin/$branch") -AllowFailure
        if ($localBehind.Code -eq 0) {
            [void](Invoke-Git -Arguments @('pull', '--ff-only', 'origin', $branch))
            Write-SyncLog -Level Success -Message 'Alteracoes do GitHub aplicadas localmente por fast-forward.'
            return
        }

        $remoteBehind = Invoke-Git -Arguments @('merge-base', '--is-ancestor', "origin/$branch", 'HEAD') -AllowFailure
        if ($remoteBehind.Code -eq 0) {
            $push = Invoke-Git -Arguments @('push', 'origin', $branch) -AllowFailure
            if ($push.Code -eq 0) {
                Write-SyncLog -Level Success -Message 'Alteracoes locais enviadas ao GitHub.'
            }
            else {
                Write-SyncLog -Level Warning -Message 'O push encontrou uma atualizacao concorrente; uma nova tentativa sera feita no proximo ciclo.'
            }
            return
        }

        Write-SyncLog -Level Warning -Message 'As duas maquinas possuem commits diferentes. Tentando rebase seguro, sem force push.'
        $rebase = Invoke-Git -Arguments @('rebase', "origin/$branch") -AllowFailure
        if ($rebase.Code -ne 0) {
            [void](Invoke-Git -Arguments @('rebase', '--abort') -AllowFailure)
            Write-SyncLog -Level Error -Message 'Conflito real detectado. O rebase foi abortado e os arquivos locais foram preservados.'
            return
        }

        $pushAfterRebase = Invoke-Git -Arguments @('push', 'origin', $branch) -AllowFailure
        if ($pushAfterRebase.Code -eq 0) {
            Write-SyncLog -Level Success -Message 'Historicos conciliados por rebase e enviados ao GitHub.'
        }
        else {
            Write-SyncLog -Level Warning -Message 'Outra atualizacao ocorreu durante o rebase; o proximo ciclo tentara novamente.'
        }
    }
    catch {
        Write-SyncLog -Level Error -Message $_.Exception.Message
    }
}

if (-not (Test-Path -LiteralPath $ProjectPath -PathType Container)) {
    throw "Projeto nao encontrado: $ProjectPath"
}

$script:ResolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$script:GitExe = Resolve-GitExecutable

$inside = Invoke-Git -Arguments @('rev-parse', '--is-inside-work-tree') -AllowFailure
if ($inside.Code -ne 0 -or (($inside.Output | ForEach-Object { $_.ToString() }) -join '').Trim() -ne 'true') {
    Write-SyncLog -Level Warning -Message 'A pasta aberta nao e um repositorio Git. Sincronizador encerrado sem alteracoes.'
    exit 0
}

$hashBytes = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($script:ResolvedProject.ToLowerInvariant()))
$mutexName = 'Local\VSCodeGitSync-' + (($hashBytes | ForEach-Object { $_.ToString('x2') }) -join '').Substring(0, 24)
$mutex = New-Object Threading.Mutex($false, $mutexName)
$hasLock = $false

try {
    $hasLock = $mutex.WaitOne(0)
    if (-not $hasLock) {
        Write-SyncLog -Level Info -Message 'Ja existe um sincronizador ativo para este projeto.'
        exit 0
    }

    if ($Once) {
        Sync-Once
        exit 0
    }

    Write-SyncLog -Level Success -Message "Sincronizacao continua ativa em '$script:ResolvedProject' a cada $IntervalSeconds segundos."
    Write-SyncLog -Level Info -Message 'Nao usa force push nem reset --hard. Conflitos reais exigem resolucao manual.'
    while ($true) {
        Sync-Once
        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    if ($hasLock) { [void]$mutex.ReleaseMutex() }
    $mutex.Dispose()
}
