#!/bin/sh
# Build s&box from source - port of sbox-public/bootstrap.sh for Ampersand.
#
# ampersand: name=Build S&Box
# ampersand: sniper=never
#
# Fetches prebuilt natives, checks they resolve on this host, then drives
# SboxBuild through build / build-shaders / build-content - the same
# sequence as sbox-public/bootstrap.sh. The C# Tool (Bootstrap.cs,
# --bootstrap) is the primary entry point; this script is the loose-file
# copy that ships to <OutDir>/scripts/ via ampersand.csproj so it appears
# in bin/Release/net10.0/scripts/ alongside sbox.sh / sbox-dev.sh.

set -eu

# Resolve repo root the same way _common.sh does: launcher passes
# SBOX_REPO_ROOT; manual runs fall back to dirname traversal.
ROOT="${SBOX_REPO_ROOT:-}"
if [ -z "$ROOT" ] || [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
	ROOT="$( cd "$( dirname "$0" )/.." 2>/dev/null && pwd )"
	if [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
		ROOT="$( cd "$( dirname "$0" )/../.." 2>/dev/null && pwd )"
	fi
	# When running from the built output (ampersand/bin/Release/net10.0/scripts/),
	# parent traversal lands in the ampersand checkout, not the sbox checkout.
	# If that tree has no game/engine, it is still not the right root; leave
	# ROOT as-is and let the missing project check below emit the hint.
fi

if [ ! -d "$ROOT/game" ] || [ ! -d "$ROOT/engine" ]; then
	echo "error: cannot locate sbox repo root (expected game/ and engine/ under \$ROOT)" >&2
	echo "hint: set SBOX_REPO_ROOT or run via Ampersand's Build S&Box tool" >&2
	echo "tried: $ROOT" >&2
	exit 1
fi

cd -- "$ROOT"

SBOXBUILD_PROJ="$ROOT/engine/Tools/SboxBuild/SboxBuild.csproj"
BIN_DIR="game/bin/linuxsteamrt64"
ASSUME_YES=0
SKIP_DEPS=0

while [ $# -gt 0 ]; do
	case "$1" in
		-y|--yes)     ASSUME_YES=1; shift ;;
		--skip-deps)  SKIP_DEPS=1; shift ;;
		-h|--help)
			echo "Usage: $0 [-y|--yes] [--skip-deps]"
			echo "  Fetch natives, check dependencies, then build (Developer)."
			echo "  Mirrors sbox-public/bootstrap.sh."
			exit 0 ;;
		*) echo "Unknown option: $1" >&2; exit 2 ;;
	esac
done

if ! command -v dotnet >/dev/null 2>&1; then
	echo "error: dotnet not on PATH - install .NET 10 SDK" >&2
	exit 1
fi
if [ ! -f "$SBOXBUILD_PROJ" ]; then
	echo "error: SboxBuild not found: $SBOXBUILD_PROJ" >&2
	exit 1
fi

sboxbuild() {
	dotnet run --project "$SBOXBUILD_PROJ" -- "$@"
}

if [ -t 1 ] && [ -z "${NO_COLOR-}" ]; then
	C_RESET=$(printf '\033[0m'); C_BOLD=$(printf '\033[1m'); C_DIM=$(printf '\033[2m')
	C_RED=$(printf '\033[31m'); C_GREEN=$(printf '\033[32m'); C_YELLOW=$(printf '\033[33m')
else
	C_RESET=''; C_BOLD=''; C_DIM=''
	C_RED=''; C_GREEN=''; C_YELLOW=''
fi

hr() { printf '%s\n' "${C_DIM}--------------------------------------------------------------------------${C_RESET}"; }

check_native_deps()
{
	if [ "${BASH_VERSINFO[0]:-0}" -lt 4 ]; then
		printf '  %sskipped: needs bash 4+ for the scan (found %s)%s\n' \
			"$C_YELLOW" "${BASH_VERSION:-unknown}" "$C_RESET"
		return 2
	fi
	if ! command -v ldd >/dev/null 2>&1; then
		printf '  %sskipped: ldd not on PATH -- it ships in glibc'"'"'s libc-bin package%s\n' \
			"$C_YELLOW" "$C_RESET"
		return 2
	fi
	if [ ! -d "$BIN_DIR" ]; then
		printf '  %sskipped: %s does not exist -- the fetch above did not produce it%s\n' \
			"$C_YELLOW" "$BIN_DIR" "$C_RESET"
		return 2
	fi
	local -A seen_real=() seen_copy=() consumers=()
	local -a version_errors=() ldd_errors=()
	local path rel real size copykey out line lib stem
	local checked=0 failed=0
	while IFS= read -r -d '' path; do
		rel=${path#"$BIN_DIR"/}
		real=$(readlink -f -- "$path" 2>/dev/null) || real="$path"
		[ -n "${seen_real[$real]-}" ] && continue
		[ -r "$path" ] || { ldd_errors+=( "$rel: not readable" ); continue; }
		[ "$(head -c 4 -- "$path" 2>/dev/null | od -An -tx1 2>/dev/null | tr -d ' \n')" = "7f454c46" ] || continue
		size=$(stat -c %s -- "$path" 2>/dev/null || echo 0)
		stem=$(printf '%s' "$(basename -- "$rel")" | sed -E 's/\.so(\.[0-9]+)*$/.so/')
		copykey="$(dirname -- "$rel")|$stem|$size"
		[ -n "${seen_copy[$copykey]-}" ] && continue
		seen_real[$real]="$rel"
		seen_copy[$copykey]="$rel"
		out=$(ldd -- "$path" 2>&1)
		case "$out" in
			*"not a dynamic executable"*|*"statically linked"*) continue ;;
		esac
		checked=$(( checked + 1 ))
		local miss=''
		while IFS= read -r line; do
			case "$line" in
				*"=> not found"*)
					lib=${line#"${line%%[![:space:]]*}"}; lib=${lib%% *}
					miss="$miss $lib"
					consumers[$lib]="${consumers[$lib]-} $rel"
					;;
				*"version \`"*"' not found"*)
					version_errors+=( "$rel: ${line#*: }" )
					;;
				*"error while loading"*|*"cannot open shared object"*)
					ldd_errors+=( "$rel: ${line# }" )
					;;
			esac
		done <<< "$out"
		if [ -n "$miss" ]; then
			failed=$(( failed + 1 ))
			printf '  %sFAIL%s  %-34s %s%s%s\n' "$C_RED" "$C_RESET" "$rel" "$C_RED" "${miss# }" "$C_RESET"
		else
			printf '  %sOK%s    %s\n' "$C_GREEN" "$C_RESET" "$rel"
		fi
	done < <( find "$BIN_DIR" \( -type f -o -type l \) -print0 2>/dev/null | sort -z )
	if [ "$checked" -eq 0 ]; then
		printf '  %sskipped: no dynamically linked binaries found in %s%s\n' \
			"$C_YELLOW" "$BIN_DIR" "$C_RESET"
		return 2
	fi
	printf '\n  %d OK, %d with missing libraries.\n' "$(( checked - failed ))" "$failed"
	if [ ${#consumers[@]} -gt 0 ]; then
		printf '\n'
		hr
		printf '%s\n' "${C_RED}${C_BOLD}MISSING LIBRARIES${C_RESET}"
		hr
		for lib in $( printf '%s\n' "${!consumers[@]}" | sort ); do
			# shellcheck disable=SC2086
			set -- ${consumers[$lib]}
			printf '  %s%s%s  %sneeded by %d: %s%s\n' "$C_RED" "$lib" "$C_RESET" "$C_DIM" "$#" "$*" "$C_RESET"
		done
	fi
	if [ ${#version_errors[@]} -gt 0 ]; then
		printf '\n'
		hr
		printf '%s\n' "${C_RED}${C_BOLD}UNSATISFIABLE SYMBOL VERSIONS${C_RESET}"
		hr
		printf '  %sthe library is present but older than the binary needs%s\n' "$C_DIM" "$C_RESET"
		for line in "${version_errors[@]}"; do
			printf '  %s%s%s\n' "$C_RED" "$line" "$C_RESET"
		done
	fi
	if [ ${#ldd_errors[@]} -gt 0 ]; then
		printf '\n  %sloader errors:%s\n' "$C_YELLOW" "$C_RESET"
		for line in "${ldd_errors[@]}"; do
			printf '    %s\n' "$line"
		done
	fi
	[ ${#consumers[@]} -eq 0 ] && [ ${#version_errors[@]} -eq 0 ] && [ ${#ldd_errors[@]} -eq 0 ]
}

if [ "$SKIP_DEPS" -eq 0 ]; then
	printf '%s\n' "${C_BOLD}Fetching native binaries${C_RESET}"
	hr
	if ! sboxbuild download-public-artifacts --native-only; then
		printf '  %swarning: download failed -- checking whatever is already on disk%s\n' \
			"$C_YELLOW" "$C_RESET"
	fi
	printf '\n'
	printf '%s\n' "${C_BOLD}Checking native dependencies in $BIN_DIR${C_RESET}"
	hr
	deps_rc=0
	check_native_deps || deps_rc=$?
	if [ "$deps_rc" -eq 1 ]; then
		printf '\n  %sThese are prebuilt binaries that cannot be rebuilt here, so the managed build\n' "$C_YELLOW"
		printf '  below will still succeed -- but the editor will not run until they resolve.%s\n\n' "$C_RESET"
		if [ "$ASSUME_YES" -eq 1 ]; then
			echo "Continuing anyway (-y)."
		elif [ ! -t 0 ]; then
			echo "Not an interactive terminal, continuing anyway."
		else
			read -r -p "Continue with the build anyway? [y/N] " reply
			case "$reply" in
				[yY]|[yY][eE][sS]) ;;
				*) echo "Aborted."; exit 1 ;;
			esac
		fi
	fi
	printf '\n'
fi

sboxbuild build --config Developer

# build-shaders and build-content look for game/bin/managed/shadercompiler and
# game/bin/win64/contentbuilder which don't exist on Linux - warn and continue.
sboxbuild build-shaders || echo "warning: build-shaders failed (not supported on Linux yet), continuing"
sboxbuild build-content || echo "warning: build-content failed (not supported on Linux yet), continuing"
