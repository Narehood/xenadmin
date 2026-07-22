#!/bin/bash
# OBSOLETE — do not use for XCP-ng Center builds.
#
# This script previously assumed packages/nuget.exe, TargetFrameworkVersion=v4.6,
# and VisualStudioVersion=13.0. The supported path is the SDK-style solution on the
# development branch, built by GitHub Actions (.github/workflows/test-builds.yml)
# or locally with MSBuild / dotnet against XenAdmin.sln.
#
# See: https://github.com/xcp-ng/xenadmin/wiki/Building
# See: MODERNIZATION.md

echo "ERROR: branding-xcp-ng/build.sh is obsolete." >&2
echo "Build XenAdmin.sln with MSBuild or use GitHub Actions on development." >&2
exit 1
