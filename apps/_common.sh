#!/bin/sh
# Shared environment for the ampersand core launch scripts.
#
# The leading underscore keeps this out of the launcher's scanned script list -
# it is sourced, never launched.
#
# Everything here exists to patch a known Linux issue:
#
#   LD_PRELOAD   the engine ships libHarfBuzzSharp.so (SkiaSharp's statically
#                linked HarfBuzz), but a second libharfbuzz.so.0 reaches the
#                process through Qt's xcb platform plugin and fontconfig. Both
#                export the same unversioned hb_* symbols, so a buffer
#                allocated by one is handed to the other's free() and glibc
#                aborts with "free(): invalid pointer". Preloading the engine's
#                copy puts a single implementation first in the global symbol
#                scope.
#
#                Required on the host AND inside the Steam runtime, which ships
#                its own libharfbuzz.so.0 2.7.4 - the container changes which
#                system HarfBuzz you collide with, not whether you collide.
#
#   cwd          the engine resolves content paths relative to the working
#                directory.
#
#   SBOX_SNIPER_COMPAT is set by the launcher only when running inside the
#                Steam runtime, pointing at cached copies of the libunwind and
#                OpenSSL 3 libraries sniper does not ship. Ordinary environment
#                variables cross the container boundary but LD_LIBRARY_PATH
#                does not, which is why it is appended here - inside - rather
#                than exported by the launcher.

set -eu

# The launcher passes SBOX_REPO_ROOT; the fallback keeps the script usable by
# hand from ampersand/apps/.
ROOT="${SBOX_REPO_ROOT:-$( cd "$( dirname "$0" )/../.." && pwd )}"
GAME_DIR="$ROOT/game"
NATIVE_DIR="$GAME_DIR/bin/linuxsteamrt64"

sbox_exec()
{
	_exe_name="$1"
	shift

	_exe="$GAME_DIR/$_exe_name"
	if [ ! -x "$_exe" ]; then
		echo "error: $_exe not found or not executable - run ./bootstrap.sh first" >&2
		exit 1
	fi

	_harfbuzz="$NATIVE_DIR/libHarfBuzzSharp.so"
	if [ ! -f "$_harfbuzz" ]; then
		echo "error: HarfBuzz not found at $_harfbuzz - run ./bootstrap.sh first" >&2
		exit 1
	fi

	LD_PRELOAD="$_harfbuzz${LD_PRELOAD:+:$LD_PRELOAD}"
	LD_LIBRARY_PATH="$NATIVE_DIR${SBOX_SNIPER_COMPAT:+:$SBOX_SNIPER_COMPAT}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
	export LD_PRELOAD LD_LIBRARY_PATH

	# Wayland's Qt platform plugin is not shipped/unstable; force X11 (xcb)
	# via XWayland (only xcb is bundled - "Available platform plugins are: xcb").
	QT_QPA_PLATFORM=xcb
	export QT_QPA_PLATFORM

	cd "$GAME_DIR"
	exec "$_exe" "$@"
}
