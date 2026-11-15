#!/bin/sh
# Builds Wallpapers.exe (for Windows, .NET Framework 4.x) with Mono's C# compiler: apt install mono-devel
set -e
cd "$(dirname "$0")"
mkdir -p ../out
mcs -nologo -optimize+ -target:winexe -platform:anycpu -sdk:4.5 -win32icon:app.ico -codepage:utf8 \
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll \
    -out:../out/Wallpapers-Windows.exe Wallpapers.cs
