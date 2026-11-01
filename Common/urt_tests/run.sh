#!/bin/bash
# URT quality-check tests in many languages. Exit code 0 = all passed.
cd "$(dirname "$0")"
DOTNET_ROOT=${DOTNET_ROOT:-/opt/dotnet} ${DOTNET:-/opt/dotnet/dotnet} run -c Release
