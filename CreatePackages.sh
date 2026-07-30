#!/usr/bin/env bash

PackageVersion="4.1.4"
Configuration="Debug"

rm -f nupkg/*.nupkg
rm -f nupkg/*.snupkg

dotnet pack AsyncLock.slnx \
    -c "$Configuration" \
    -p:Version="$PackageVersion" \
    -p:FileVersion="$PackageVersion" \
    -p:AssemblyVersion="$PackageVersion"