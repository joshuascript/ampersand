#!/bin/sh
# Build s&box from source - thin wrapper around the engine's Setup.sh.
#
# ampersand: name=Build S&Box
# ampersand: runtime=never
#
# History: this used to be a full port of sbox-public/bootstrap.sh (fetch
# natives, ldd sweep, drive SboxBuild step by step). The engine now owns that
# flow in Setup.sh (git hooks, per-platform artifacts, bindings, managed,
# shaders, content), so this just locates the checkout and hands over. For the
# readable per-binary ldd report use Ampersand's Check-dependencies tool
# (or `ampersand --dependency-check`). The C# Tool (Bootstrap.cs,
# --bootstrap) is the primary entry point - it runs Setup.sh plus the ldd
# report; this script is the loose-file copy that ships to
# <OutDir>/scripts/ via ampersand.csproj.

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

# Via sh, not ./. : Setup.sh is stored without the exec bit upstream.
if [ ! -f "$ROOT/Setup.sh" ]; then
	echo "error: $ROOT/Setup.sh not found - this checkout predates the engine's Setup.sh workflow" >&2
	echo "hint: pull sbox-public, then rerun" >&2
	exit 1
fi

case "${1:-}" in
	-h|--help)
		echo "Usage: $0 [--verbose]"
		echo "  Run the engine's Setup.sh (full build incl. shaders and content)."
		echo "  Pass --verbose through for full build output."
		exit 0 ;;
esac

# Before the cd: $0 may be relative, and dirname would then resolve against
# the checkout instead of wherever the script actually lives.
_script_dir="$( cd "$( dirname "$0" )" && pwd )"

# Shadowing twin preflight. The content build publishes compiled outputs into
# lowercased paths, and a lowercase directory beside an authored shader include
# directory makes the shader preprocessor resolve #include into the twin and
# fail every include with a misleading "file not found" (see ContentTwins.cs).
# Deliberately before the lock: the check only reads the tree, and a launcher
# too old to know the switch opens a window instead - holding the build lock
# while that window waits for a click would block every other build. The record
# is cleared further down, after this, so a stop here keeps the verified tree.
#
# The binary is whichever one ships beside these scripts, and scripts are loose
# files: a copied-over scripts/ dir can be newer than the launcher next to it.
# Exit 1 is "twins found"; anything else means the binary could not answer, and
# saying so is better than reading it as success.
_amp="$_script_dir/../ampersand"
if [ -x "$_amp" ] && [ ! -d "$_amp" ]; then
	_twins=0
	"$_amp" --content-twins "$ROOT" || _twins=$?
	case "$_twins" in
		0) ;;
		1)
			echo "error: shader include directories are shadowed (see above)" >&2
			echo "fix: $_amp --repair-content-twins \"$ROOT\"  (adds include links; deletes nothing)" >&2
			exit 1 ;;
		*)
			echo "warning: $_amp could not run --content-twins (exit $_twins)" >&2
			echo "warning: the launcher beside these scripts is probably older than them - rebuild or reinstall ampersand" >&2
			echo "warning: skipping the twin preflight; a shadowing twin will fail the shader stage with 'file not found'" >&2 ;;
	esac
else
	echo "note: no ampersand binary beside these scripts - skipping the twin preflight (ampersand --bootstrap runs it)" >&2
fi

# Single-flight build. Setup.sh deletes and repopulates game/bin/managed, and
# every engine project's output path points there, so two builds at once can
# splice one assembly from both compiles - the launcher then dies with
# InvalidProgramException and no stack on its next boot. Same file and same flock(2)
# mechanism as BuildLock.cs, so the GUI build and this script exclude each
# other. Export AMPERSAND_BUILD_LOCK to point both at a different path.
if [ -d "$ROOT/.git" ]; then
	STATE_DIR="$ROOT/.git"
else
	STATE_DIR="$ROOT"
fi

LOCK="${AMPERSAND_BUILD_LOCK:-$STATE_DIR/sbox-build.lock}"
RECORD="$STATE_DIR/sbox-managed.json"

if command -v flock >/dev/null 2>&1; then
	exec 9>"$LOCK" || { echo "error: cannot open build lock $LOCK" >&2; exit 1; }
	if ! flock -n 9; then
		echo "error: another build is already running (lock held: $LOCK)" >&2
		echo "hint: wait for it to finish. Building now would put a second writer on game/bin/managed." >&2
		exit 1
	fi
else
	echo "warning: flock not found - a concurrent build cannot be excluded" >&2
fi

# Freshly downloaded artifacts lose the executable bit, and the engine only
# repairs contentbuilder itself - restore both toolchain bits best-effort so
# the content step can spawn resourcecompiler. Silent on purpose (missing
# files just mean Setup.sh hasn't downloaded them yet); never fails the build.
chmod +x "$ROOT/game/bin/linuxsteamrt64/contentbuilder" "$ROOT/game/bin/linuxsteamrt64/resourcecompiler" 2>/dev/null || true

# The casefold shim is part of the build, not of the launch (see README):
# without it the native model compiler fails on the engine's wrong-case FBX
# references (citizen: 4 assets) and the run ends "Content: Failed" for a
# reason that is not in the checkout. Bootstrap.cs builds it on demand; this
# script compiles the same vendored source when it has to, so both entry
# points behave alike. A prebuilt/custom SBOX_CASEFOLD_SO is never overwritten.
_shim="${SBOX_CASEFOLD_SO:-${XDG_CACHE_HOME:-$HOME/.cache}/sbox-ampersand/libcasefold.so}"
_shim_src="$_script_dir/patches/casefold.c"
if [ -z "${SBOX_CASEFOLD_SO:-}" ] && [ -f "$_shim_src" ] && command -v gcc >/dev/null 2>&1; then
	if [ ! -f "$_shim" ] || [ "$_shim_src" -nt "$_shim" ]; then
		mkdir -p "${_shim%/*}" 2>/dev/null || true
		gcc -shared -fPIC -O2 -o "$_shim" "$_shim_src" -ldl >/dev/null 2>&1 || true
	fi
fi
if [ -f "$_shim" ]; then
	LD_PRELOAD="$_shim${LD_PRELOAD:+:$LD_PRELOAD}"
	export LD_PRELOAD
	echo "casefold shim: $_shim"
else
	echo "warning: casefold shim unavailable ($_shim)" >&2
	echo "warning: the content step will fail on wrong-case FBX references (citizen: 4 assets)" >&2
	echo "warning: that failure is not in the checkout - install gcc or set SBOX_CASEFOLD_SO" >&2
fi

# The managed tree is about to be rewritten: forget the verified fingerprint so
# an interrupted build leaves it unverified and the next launch offers a rebuild.
rm -f "$RECORD"

cd -- "$ROOT"

# `9>&-` closes the lock fd for Setup.sh's process tree. Without it, dotnet's
# MSBuild worker nodes inherit fd 9 (node reuse keeps them alive for ~15
# minutes after the build) and the next build reads as "another build is
# already running" long after this one finished. This script holds the lock
# itself for the whole run; the children do not need it.
# `|| _code=$?` rather than a bare call: set -e exits on a failed Setup.sh
# before the status can be read, which would swallow the message below.
_code=0
sh Setup.sh "$@" 9>&- || _code=$?

if [ "$_code" -ne 0 ]; then
	echo "managed tree left unverified (build exited $_code) - the next launch will offer to rebuild" >&2
	exit "$_code"
fi

# Record the tree Ampersand would verify, when the binary that ships this
# script is next to it (built layout: scripts/../ampersand). Repo layout has a
# directory there, so a hand-run build just stays unverified until the launcher
# is told to trust it once.
if [ -x "$_amp" ] && [ ! -d "$_amp" ]; then
	_record=0
	"$_amp" --record-managed "$ROOT" || _record=$?
	if [ "$_record" -ne 0 ]; then
		echo "warning: could not mark game/bin/managed verified (exit $_record)" >&2
		echo "warning: the next launch will offer to rebuild; run: $_amp --record-managed \"$ROOT\"" >&2
	fi
else
	echo "note: run 'ampersand --record-managed $ROOT' to mark this build verified" >&2
fi

exit 0
