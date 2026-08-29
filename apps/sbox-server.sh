#!/bin/sh
# Launch the s&box dedicated server. Without a "game" argument it prints help.
#
# ampersand: name=Dedicated Server (sbox-server)
# ampersand: sniper=optional
#
# The server's status-bar overlay (DedicatedServerConsole) is skipped whenever
# output is redirected - engine/Launcher/SboxServer/Launcher.cs:46 - which it
# always is here, so no cursor-positioning escapes reach the output pane.

set -eu

. "$( dirname "$0" )/_common.sh"

sbox_exec sbox-server "$@"
