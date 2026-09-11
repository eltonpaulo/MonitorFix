#!/usr/bin/env bash
# Versao Linux/macOS de continuous-git-sync.ps1
# Sincroniza o repositorio com o GitHub: nunca usa force push nem reset --hard.
# Conflitos reais exigem resolucao manual (o rebase automatico e abortado).

set -u

usage() {
    cat <<'EOF'
Uso: continuous-git-sync.sh [--project-path <caminho>] [--interval-seconds <10-3600>] [--once]

  --project-path      Caminho do repositorio (padrao: diretorio atual)
  --interval-seconds  Intervalo entre ciclos de sincronizacao (padrao: 15)
  --once              Executa um unico ciclo e encerra
EOF
}

PROJECT_PATH="$(pwd)"
INTERVAL_SECONDS=15
ONCE=0

while [ $# -gt 0 ]; do
    case "$1" in
        --project-path)
            PROJECT_PATH="$2"
            shift 2
            ;;
        --project-path=*)
            PROJECT_PATH="${1#*=}"
            shift
            ;;
        --interval-seconds)
            INTERVAL_SECONDS="$2"
            shift 2
            ;;
        --interval-seconds=*)
            INTERVAL_SECONDS="${1#*=}"
            shift
            ;;
        --once)
            ONCE=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Argumento desconhecido: $1" >&2
            usage
            exit 1
            ;;
    esac
done

if ! [[ "$INTERVAL_SECONDS" =~ ^[0-9]+$ ]] || [ "$INTERVAL_SECONDS" -lt 10 ] || [ "$INTERVAL_SECONDS" -gt 3600 ]; then
    echo "IntervalSeconds deve ser um numero entre 10 e 3600." >&2
    exit 1
fi

if [ ! -d "$PROJECT_PATH" ]; then
    echo "Projeto nao encontrado: $PROJECT_PATH" >&2
    exit 1
fi

RESOLVED_PROJECT="$(cd "$PROJECT_PATH" && pwd -P)"

if ! command -v git >/dev/null 2>&1; then
    echo "Git nao foi encontrado no PATH." >&2
    exit 1
fi

log() {
    local level="$1"; shift
    local color
    case "$level" in
        Success) color=$'\033[32m' ;;
        Warning) color=$'\033[33m' ;;
        Error)   color=$'\033[31m' ;;
        *)       color=$'\033[36m' ;;
    esac
    printf '%s[%s] %s%s\n' "$color" "$(date '+%Y-%m-%d %H:%M:%S')" "$*" $'\033[0m'
}

# Executa git e preenche GIT_OUTPUT / GIT_CODE sem interromper o script.
run_git() {
    GIT_OUTPUT=$(git -C "$RESOLVED_PROJECT" "$@" 2>&1)
    GIT_CODE=$?
}

# Como run_git, mas registra e retorna falha em vez de lancar excecao.
require_git() {
    run_git "$@"
    if [ "$GIT_CODE" -ne 0 ]; then
        log Error "Falha ao executar git $*: $GIT_OUTPUT"
        return 1
    fi
    return 0
}

has_sensitive_untracked_files() {
    run_git ls-files --others --exclude-standard
    [ "$GIT_CODE" -ne 0 ] && return 1

    local blocked=()
    local rel name
    while IFS= read -r rel; do
        [ -z "$rel" ] && continue
        name="$(basename -- "$rel")"

        if [[ "$name" =~ ^\.env\.(example|sample|template)$ ]]; then
            continue
        fi
        if [[ "$name" =~ ^\.env($|\.) ]] \
            || [[ "$name" =~ \.(pem|p12|pfx|key)$ ]] \
            || [[ "$name" =~ ^(credentials|secrets?)(\.|$) ]] \
            || [[ "$name" =~ ^id_(rsa|ed25519)(\.|$) ]]; then
            blocked+=("$rel")
        fi
    done <<< "$GIT_OUTPUT"

    if [ "${#blocked[@]}" -gt 0 ]; then
        local joined
        joined="$(IFS=', '; echo "${blocked[*]}")"
        log Error "Sincronizacao pausada: arquivos possivelmente secretos nao ignorados: $joined"
        log Warning "Adicione esses arquivos ao .gitignore ou confirme manualmente que podem ser publicados."
        return 0
    fi
    return 1
}

is_git_operation_in_progress() {
    run_git rev-parse --absolute-git-dir
    [ "$GIT_CODE" -ne 0 ] && return 1

    local git_dir="$GIT_OUTPUT"
    local marker
    for marker in "$git_dir/MERGE_HEAD" "$git_dir/CHERRY_PICK_HEAD" "$git_dir/REVERT_HEAD" "$git_dir/rebase-merge" "$git_dir/rebase-apply"; do
        [ -e "$marker" ] && return 0
    done
    return 1
}

save_local_changes() {
    run_git status --porcelain --untracked-files=all
    [ -z "$GIT_OUTPUT" ] && return 0

    if has_sensitive_untracked_files; then
        return 1
    fi

    run_git config --get user.name
    local name="$GIT_OUTPUT"
    run_git config --get user.email
    local email="$GIT_OUTPUT"
    if [ -z "$name" ] || [ -z "$email" ]; then
        log Error "Sincronizacao pausada: configure user.name e user.email do Git para criar commits automaticos."
        return 1
    fi

    run_git add -A

    run_git diff --cached --quiet
    local diff_code=$GIT_CODE
    if [ "$diff_code" -eq 1 ]; then
        local device message
        device="$(hostname 2>/dev/null || echo Linux)"
        message="sync automatico [$device] $(date '+%Y-%m-%d %H:%M:%S')"
        require_git commit -m "$message" || return 1
        log Success "Alteracoes locais salvas: $message"
    elif [ "$diff_code" -ne 0 ]; then
        log Error "Nao foi possivel verificar as alteracoes preparadas para commit."
        return 1
    fi

    return 0
}

sync_once() {
    if is_git_operation_in_progress; then
        log Warning "Ha merge, rebase ou cherry-pick em andamento. Sincronizacao aguardando resolucao manual."
        return
    fi

    run_git symbolic-ref --quiet --short HEAD
    if [ "$GIT_CODE" -ne 0 ]; then
        log Warning "Repositorio em detached HEAD. Selecione uma branch antes de sincronizar."
        return
    fi
    local branch="$GIT_OUTPUT"

    run_git remote get-url origin
    if [ "$GIT_CODE" -ne 0 ]; then
        log Warning "Remote origin nao configurado. Nenhum dado foi enviado."
        return
    fi

    run_git fetch origin --prune
    if [ "$GIT_CODE" -ne 0 ]; then
        log Error "Falha ao buscar o GitHub: $GIT_OUTPUT"
        return
    fi

    save_local_changes || return

    run_git rev-parse HEAD
    if [ "$GIT_CODE" -ne 0 ]; then
        log Warning "Repositorio sem commit inicial. Crie um arquivo para iniciar a sincronizacao."
        return
    fi
    local head="$GIT_OUTPUT"

    local remote_ref="refs/remotes/origin/$branch"
    run_git show-ref --verify --quiet "$remote_ref"
    if [ "$GIT_CODE" -ne 0 ]; then
        run_git push --set-upstream origin "$branch"
        if [ "$GIT_CODE" -eq 0 ]; then
            log Success "Branch '$branch' publicada no GitHub."
        else
            log Error "Falha ao publicar a branch: $GIT_OUTPUT"
        fi
        return
    fi

    run_git rev-parse "origin/$branch"
    local remote_head="$GIT_OUTPUT"
    if [ "$head" = "$remote_head" ]; then
        return
    fi

    run_git merge-base --is-ancestor HEAD "origin/$branch"
    if [ "$GIT_CODE" -eq 0 ]; then
        require_git pull --ff-only origin "$branch" || return
        log Success "Alteracoes do GitHub aplicadas localmente por fast-forward."
        return
    fi

    run_git merge-base --is-ancestor "origin/$branch" HEAD
    if [ "$GIT_CODE" -eq 0 ]; then
        run_git push origin "$branch"
        if [ "$GIT_CODE" -eq 0 ]; then
            log Success "Alteracoes locais enviadas ao GitHub."
        else
            log Warning "O push encontrou uma atualizacao concorrente; uma nova tentativa sera feita no proximo ciclo."
        fi
        return
    fi

    log Warning "As duas maquinas possuem commits diferentes. Tentando rebase seguro, sem force push."
    run_git rebase "origin/$branch"
    if [ "$GIT_CODE" -ne 0 ]; then
        run_git rebase --abort
        log Error "Conflito real detectado. O rebase foi abortado e os arquivos locais foram preservados."
        return
    fi

    run_git push origin "$branch"
    if [ "$GIT_CODE" -eq 0 ]; then
        log Success "Historicos conciliados por rebase e enviados ao GitHub."
    else
        log Warning "Outra atualizacao ocorreu durante o rebase; o proximo ciclo tentara novamente."
    fi
}

run_git rev-parse --is-inside-work-tree
if [ "$GIT_CODE" -ne 0 ] || [ "$GIT_OUTPUT" != "true" ]; then
    log Warning "A pasta aberta nao e um repositorio Git. Sincronizador encerrado sem alteracoes."
    exit 0
fi

# Evita duas instancias simultaneas para o mesmo projeto (equivalente ao Mutex do PowerShell).
LOCK_HASH="$(printf '%s' "$(printf '%s' "$RESOLVED_PROJECT" | tr '[:upper:]' '[:lower:]')" | sha256sum | cut -c1-24)"
LOCK_FILE="${TMPDIR:-/tmp}/vscode-git-sync-${LOCK_HASH}.lock"
exec 200>"$LOCK_FILE"
if ! flock -n 200; then
    log Info "Ja existe um sincronizador ativo para este projeto."
    exit 0
fi

if [ "$ONCE" -eq 1 ]; then
    sync_once
    exit 0
fi

log Success "Sincronizacao continua ativa em '$RESOLVED_PROJECT' a cada $INTERVAL_SECONDS segundos."
log Info "Nao usa force push nem reset --hard. Conflitos reais exigem resolucao manual."
while true; do
    sync_once
    sleep "$INTERVAL_SECONDS"
done
