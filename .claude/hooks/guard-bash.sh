#!/usr/bin/env bash
# PreToolUse (Bash): защита main. Блокирует push в main/master, любой force-push,
# удаление main/master, reset --hard на main/master.
set -u
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
hook_init
[ -z "$HOOK_CMD" ] && exit 0

is_main() { case $1 in main|master) return 0 ;; esac; return 1; }

cur_branch() {
  if [ -n "${1:-}" ]; then
    git -C "$1" branch --show-current 2>/dev/null | tr -d '\r'
  else
    git branch --show-current 2>/dev/null | tr -d '\r'
  fi
}

# refspec -> целевая ветка: [+]src:dst, refs/heads/x
dst_is_main() {
  local a=${1#+}
  a=${a##*:}
  a=${a#refs/heads/}
  is_main "$a"
}

check_git() {
  local -a t=("$@")
  local i=1 dir="" sub="" a
  while [ $i -lt ${#t[@]} ]; do
    a=${t[$i]}
    case $a in
      -C) dir=${t[$((i + 1))]:-}; i=$((i + 2)) ;;
      -c|--git-dir|--work-tree|--namespace) i=$((i + 2)) ;;
      -*) i=$((i + 1)) ;;
      *) sub=$a; i=$((i + 1)); break ;;
    esac
  done
  [ -z "$sub" ] && return 0
  local -a args=()
  [ $i -lt ${#t[@]} ] && args=("${t[@]:$i}")

  case $sub in
    push)
      local -a nonopt=()
      for a in ${args[@]+"${args[@]}"}; do
        case $a in
          --force*|-f) hook_block "git push --force / -f / --force-with-lease запрещены." ;;
          --delete|-d) ;;
          --all|--mirror) hook_block "git push $a затрагивает main; пушьте конкретную ветку." ;;
          --*) ;;
          -*f*) hook_block "git push с -f (force) запрещён: $a" ;;
          -*) ;;
          +*) hook_block "refspec с '+' (force-push) запрещён: $a" ;;
          *) nonopt+=("$a") ;;
        esac
      done
      local cur; cur=$(cur_branch "$dir")
      if [ ${#nonopt[@]} -le 1 ]; then
        if is_main "$cur"; then
          hook_block "git push без явной ветки при текущей ветке '$cur' запрещён. В main — только через PR."
        fi
      fi
      local n=${#nonopt[@]} j
      for ((j = 1; j < n; j++)); do
        a=${nonopt[$j]}
        if dst_is_main "$a"; then hook_block "git push в main/master запрещён ($a). В main — только через PR."; fi
        if [ "$a" = HEAD ] && is_main "$cur"; then hook_block "git push HEAD на ветке '$cur' запрещён. В main — только через PR."; fi
      done
      ;;
    branch)
      local del=0
      local -a names=()
      for a in ${args[@]+"${args[@]}"}; do
        case $a in
          --delete) del=1 ;;
          --*) ;;
          -*[dD]*) del=1 ;;
          -*) ;;
          *) names+=("$a") ;;
        esac
      done
      if [ $del = 1 ]; then
        for a in ${names[@]+"${names[@]}"}; do
          is_main "$a" && hook_block "удаление ветки '$a' запрещено."
        done
      fi
      ;;
    reset)
      local hard=0
      for a in ${args[@]+"${args[@]}"}; do [ "$a" = --hard ] && hard=1; done
      if [ $hard = 1 ]; then
        local cur; cur=$(cur_branch "$dir")
        is_main "$cur" && hook_block "git reset --hard на ветке '$cur' запрещён."
      fi
      ;;
  esac
  return 0
}

check_command() {
  local cmd=$1 seg
  cmd=${cmd//\"/}
  cmd=${cmd//\'/}
  local lines
  lines=$(printf '%s\n' "$cmd" | sed -e 's/&&/\n/g' -e 's/||/\n/g' -e 's/[;|&()`]/\n/g' | tr -d '\r')
  while IFS= read -r seg; do
    [ -z "${seg//[[:space:]]/}" ] && continue
    local -a w
    set -f
    read -r -a w <<< "$seg"
    set +f
    local k=0
    # пропуск env-присваиваний и обёрток
    while [ $k -lt ${#w[@]} ]; do
      case ${w[$k]} in
        *=*|sudo|command|env|nohup|time|exec) k=$((k + 1)) ;;
        *) break ;;
      esac
    done
    [ $k -ge ${#w[@]} ] && continue
    local first=${w[$k]##*/}
    case $first in
      git|git.exe) check_git "git" "${w[@]:$((k + 1))}" ;;
      bash|sh|zsh|eval)
        local rest="" m
        for ((m = k + 1; m < ${#w[@]}; m++)); do
          case ${w[$m]} in -*) continue ;; esac
          rest+="${w[$m]} "
        done
        [ -n "$rest" ] && check_command "$rest"
        ;;
    esac
  done <<< "$lines"
}

check_command "$HOOK_CMD"
exit 0
