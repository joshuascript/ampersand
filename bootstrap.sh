#!/bin/sh
# Build ampersand into bin/Release/net10.0/ampersand; pass extra dotnet args through.
set -eu
cd "$( dirname "$( readlink -f "$0" )" )"
exec dotnet build -c Release "$@"
