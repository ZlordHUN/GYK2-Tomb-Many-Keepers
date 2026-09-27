#!/usr/bin/env bash
# Manual second player; no smoke-test plugins or automatic campaign actions.
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
instance_dir="$repo_dir/artifacts/second-instance"
game_dir="${GYK2_GAME_DIR:-}"
proton="${GYK2_PROTON:-}"
dll=""
prepare_only=false

usage() {
    cat <<'EOF'
Usage: tests/run-second-instance.sh [options] [-- game arguments...]

Build the current mod and launch another Graveyard Keeper 2 through Proton.
Keep Steam running. The installed game is only read; the second instance has
its own game copy, saves, settings and player identity in artifacts/second-instance.
That folder is reused across launches, and the game copy is refreshed each time.
The second instance always opens in a window, 1280x720 unless -screen-width and
-screen-height say otherwise, so it stays visible beside a fullscreen main game.
Each launch resets its display mode to that window; other settings are kept.

Options:
  --game-dir PATH  Steam game folder (or set GYK2_GAME_DIR).
  --proton PATH    Proton executable (or set GYK2_PROTON).
  --dll PATH       Use this DLL instead of building current source.
  --prepare-only   Build and prepare the copy without launching the game.
  -h, --help       Show this help.

Defaults: the usual Steam installation, newest installed GE-Proton (otherwise
Proton Experimental), and a fresh Release build. The game installation must
already contain BepInEx 5. Requires dotnet, rsync, flock and python3.

Examples:
  ./tests/run-second-instance.sh
  ./tests/run-second-instance.sh --dll bin/Release/netstandard2.1/GYK2.TombManyKeepers.dll
  ./tests/run-second-instance.sh -- -screen-width 1920 -screen-height 1080
EOF
}

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

while (($#)); do
    case "$1" in
        --game-dir|--proton|--dll)
            (($# >= 2)) && [[ -n "$2" ]] || fail "$1 needs a path."
            case "$1" in
                --game-dir) game_dir="$2" ;;
                --proton) proton="$2" ;;
                --dll) dll="$2" ;;
            esac
            shift 2 ;;
        --prepare-only) prepare_only=true; shift ;;
        -h|--help) usage; exit 0 ;;
        --) shift; break ;;
        *) fail "Unknown option: $1 (see --help)." ;;
    esac
done

width=1280
height=720
game_args=("$@")
for ((index = 0; index + 1 < ${#game_args[@]}; index++)); do
    case "${game_args[index]}" in
        -screen-width) width="${game_args[index + 1]}" ;;
        -screen-height) height="${game_args[index + 1]}" ;;
    esac
done
[[ "$width" =~ ^[1-9][0-9]{0,4}$ && "$height" =~ ^[1-9][0-9]{0,4}$ ]] ||
    fail "-screen-width and -screen-height need a size in pixels."

for command in rsync flock realpath sha256sum python3; do
    command -v "$command" >/dev/null || fail "Required command not found: $command"
done

steam_dir=""
for candidate in "$HOME/.steam/steam" "$HOME/.steam/root" "$HOME/.local/share/Steam" \
                 "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
    if [[ -d "$candidate/steamapps" ]]; then
        steam_dir="$(realpath -- "$candidate")"
        break
    fi
done
[[ -n "$steam_dir" ]] || fail "Steam installation not found."
game_dir="$(realpath -m -- "${game_dir:-$steam_dir/steamapps/common/Graveyard Keeper 2}")"
[[ -f "$game_dir/GraveyardKeeper2.exe" && -f "$game_dir/GraveyardKeeper2_Data/Managed/Assembly-CSharp.dll" ]] ||
    fail "Game not found at $game_dir. Use --game-dir for another Steam library."
for file in winhttp.dll doorstop_config.ini BepInEx/core/BepInEx.dll BepInEx/core/0Harmony.dll; do
    [[ -f "$game_dir/$file" ]] || fail "Missing $game_dir/$file. Install BepInEx 5 in the source game first."
done

if [[ -z "$proton" ]]; then
    shopt -s nullglob
    candidates=("$steam_dir"/compatibilitytools.d/GE-Proton*/proton
                "$HOME"/.local/share/Steam/compatibilitytools.d/GE-Proton*/proton)
    if ((${#candidates[@]})); then
        while IFS=$'\t' read -r version_name candidate; do
            if [[ -x "$candidate" ]]; then proton="$candidate"; break; fi
        done < <(
            for candidate in "${candidates[@]}"; do
                version_name="${candidate%/proton}"
                printf '%s\t%s\n' "${version_name##*/}" "$candidate"
            done | sort -t $'\t' -k1,1Vr
        )
    fi
    proton="${proton:-$steam_dir/steamapps/common/Proton - Experimental/proton}"
fi
proton="$(realpath -m -- "$proton")"
[[ -x "$proton" ]] || fail "Proton executable not found: $proton. Use --proton."
wineserver="$(dirname -- "$proton")/files/bin/wineserver"
[[ -x "$wineserver" ]] || fail "Proton's wineserver not found: $wineserver"
[[ -z "$dll" ]] || dll="$(realpath -m -- "$dll")"

mkdir -p -- "$instance_dir"
instance_dir="$(realpath -- "$instance_dir")"
copy_dir="$instance_dir/game"
[[ "$game_dir/" != "$instance_dir/"* && "$instance_dir/" != "$game_dir/"* ]] ||
    fail "The source game and second-instance folder must be separate."
exec 9>"$instance_dir/launcher.lock"
flock -n 9 || fail "The second instance is already running or being prepared."
# Only this launcher's marked copy may be refreshed with rsync --delete.
if [[ -e "$copy_dir" && ! -f "$instance_dir/.launcher-owned" ]]; then
    fail "Unrecognized existing game copy: $copy_dir. Move it aside before using this launcher."
fi
[[ ! -L "$copy_dir" ]] || fail "The second-instance game copy must not be a symlink."

if [[ -z "$dll" ]]; then
    command -v dotnet >/dev/null || fail "dotnet is required to build; alternatively use --dll PATH."
    printf 'Building the current mod...\n'
    # Keep build output separate from another development session's build.
    if ! (cd -- "$repo_dir" && dotnet build GYK2.TombManyKeepers.csproj -c Release --nologo \
        -p:GameDir="$game_dir" -p:BepInExDir="$game_dir/BepInEx" \
        -p:BaseIntermediateOutputPath="$instance_dir/obj/" -o "$instance_dir/build"); then
        fail "Build failed; no game was launched. Fix the build or select a tested DLL with --dll PATH."
    fi
    dll="$instance_dir/build/GYK2.TombManyKeepers.dll"
fi
[[ -s "$dll" && "$(basename -- "$dll")" == GYK2.TombManyKeepers.dll ]] ||
    fail "Expected a nonempty GYK2.TombManyKeepers.dll: $dll"

printf 'Refreshing the second game copy...\n'
touch "$instance_dir/.launcher-owned"
mkdir -p -- "$copy_dir/BepInEx/core" "$copy_dir/BepInEx/plugins" "$instance_dir/prefix"
rsync -a --delete --exclude='/BepInEx/' --exclude='*.log' -- "$game_dir/" "$copy_dir/"
rsync -a --delete -- "$game_dir/BepInEx/core/" "$copy_dir/BepInEx/core/"
# Never copy the installed game's plugins, patchers, configuration or save prefix.
cp -- "$dll" "$copy_dir/BepInEx/plugins/GYK2.TombManyKeepers.dll"
printf 'Mod source: %s\nProton: %s\nSecond-instance data: %s\n' "$dll" "$proton" "$instance_dir"
sha256sum "$copy_dir/BepInEx/plugins/GYK2.TombManyKeepers.dll"
if "$prepare_only"; then
    printf 'Ready. Run again without --prepare-only to open the second instance.\n'
    exit 0
fi

compat_env=(STEAM_COMPAT_DATA_PATH="$instance_dir/prefix" STEAM_COMPAT_CLIENT_INSTALL_PATH="$steam_dir"
    SteamAppId=4358690 SteamGameId=4358690 WINEDLLOVERRIDES='winhttp=n,b' DISABLE_LSFG=1)
user_reg="$instance_dir/prefix/pfx/user.reg"
if [[ ! -f "$user_reg" ]]; then
    printf 'Creating the second-instance Wine prefix...\n'
    # Every Proton command but runinprefix sets up the prefix; getcompatpath then runs only winepath.
    env "${compat_env[@]}" "$proton" getcompatpath "$copy_dir" >/dev/null 2>&1 ||
        fail "Proton could not create the Wine prefix in $instance_dir/prefix."
fi
# Wine writes user.reg when its server exits, so wait for the last run's server first.
WINEPREFIX="$instance_dir/prefix/pfx" "$wineserver" -w
[[ -f "$user_reg" ]] || fail "Wine prefix registry not found: $user_reg"
# After Unity starts, the game applies its saved display settings over -screen-fullscreen 0;
# without saved settings it opens borderless fullscreen across the whole desktop.
python3 - "$user_reg" "$width" "$height" <<'EOF' || fail "Could not set windowed mode in $user_reg."
import json, math, re, sys, time

path, width, height = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
key = r'[Software\\Lazy Bear Games\\Graveyard Keeper 2]'
name = '"settings_h1277500064"'  # Unity's registry name for the game's "settings" JSON
with open(path, encoding='utf-8', errors='surrogateescape', newline='') as file:
    text = file.read()
if '\n' + key + ' ' not in text:
    text = text.rstrip('\n') + f'\n\n{key} {int(time.time())}\n'
start = text.index('\n' + key + ' ')
end = text.find('\n\n', start + 1)
end = len(text) if end < 0 else end
block = text[start:end].rstrip('\n')
value = re.search('^' + re.escape(name) + r'=((?:.*\\\n)*.*)', block, re.M)
# Without saved settings the game keeps its defaults and detects graphics as on a first run.
settings = {'gpuGraphicsDefaultApplied': False}
if value and value.group(1).startswith('hex:'):
    data = bytes.fromhex(re.sub('[^0-9a-f]', '', value.group(1)[4:]))
    settings = json.loads(data.rstrip(b'\0').decode('utf-8'))
# As ResolutionConfig.GetPixelSize; UI size 0 is Big, 1 Small.
pixel = 1 if height < 720 else math.ceil(height / 540) if height > 1440 else 2
settings['resolutionConfig'] = {
    'width': width, 'height': height, 'pixelSize': pixel, 'customAdditinalString': '',
    'windowSizeType': 1 if height < 720 or height // pixel <= 360 else 0,
    'useMainMenuScaleX2': False, 'isFakeResolution': False, 'fakeWidth': 0, 'fakeHeight': 0}
settings['screenMode'] = 1  # ScreenMode.Windowed
data = json.dumps(settings, separators=(',', ':')).encode('utf-8') + b'\0'
# A REG_BINARY value, wrapped as Wine writes it.
line, lines = name + '=hex:', []
for index, byte in enumerate(data):
    part = f'{byte:02x}' + (',' if index + 1 < len(data) else '')
    if len(line) + len(part) > 78:
        lines.append(line + '\\')
        line = '  '
    line += part
lines.append(line)
entry = '\n'.join(lines)
block = block[:value.start()] + entry + block[value.end():] if value else block + '\n' + entry
with open(path, 'w', encoding='utf-8', errors='surrogateescape', newline='') as file:
    file.write(text[:start] + block + (text[end:] or '\n'))
EOF

printf 'Launching the second instance in a %sx%s window. Logs: %s/BepInEx/LogOutput.log\n' \
    "$width" "$height" "$copy_dir"
cd -- "$copy_dir"
exec env "${compat_env[@]}" "$proton" run "$copy_dir/GraveyardKeeper2.exe" \
    -screen-fullscreen 0 -screen-width "$width" -screen-height "$height" "$@"
