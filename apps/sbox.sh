#!/bin/sh
# Launch the s&box game client.
#
# ampersand: name=Client (sbox)
# ampersand: sniper=optional

set -eu

. "$( dirname "$0" )/_common.sh"

sbox_exec sbox "$@"
