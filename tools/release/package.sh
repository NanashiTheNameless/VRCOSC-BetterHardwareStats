#!/usr/bin/env bash
# Builds the module in Release and writes dist/NanashiBetterHardwareStats-<version>.zip plus its .sha256.
# The zip holds the module DLLs at its root: VRCOSC extracts release zips straight into the package folder.
# Bundles the stable sensor library and its Windows x64 dependencies; the host supplies the SDK.
# Usage: tools/release/package.sh <version>
set -euo pipefail
version="$1"
numeric="$(sed -n 's:.*<Version>\([0-9]*\.[0-9]*\.[0-9]*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
[[ -n "$numeric" && "${version%%-*}" == "$numeric" ]] || { echo "Package version must match the configured Version" >&2; exit 1; }
out="artifacts/module"
rm -rf "$out" dist
dotnet build src/BetterHardwareStats.Module/BetterHardwareStats.Module.csproj -c Release -r win-x64 --self-contained false -o "$out" \
  -p:Version="$numeric" -p:AssemblyVersion="$numeric.0" -p:FileVersion="$numeric.0" \
  -p:InformationalVersion="$version+$(git rev-parse --short=7 "${GITHUB_SHA:-HEAD}")" >&2

stage="artifacts/stage"
rm -rf "$stage" && mkdir -p "$stage" dist
for f in BetterHardwareStats.Module.dll BetterHardwareStats.Core.dll BetterHardwareStats.Windows.dll; do
  [[ -f "$out/$f" ]] || { echo "missing $out/$f" >&2; exit 1; }
  cp "$out/$f" "$stage/"
done
mkdir -p "$stage/lhm"
for f in LibreHardwareMonitorLib.dll HidSharp.dll DiskInfoToolkit.dll RAMSPDToolkit-NDD.dll BlackSharp.Core.dll \
  Mono.Posix.NETStandard.dll MonoPosixHelper.dll libMonoPosixHelper.dll System.Management.dll System.IO.Ports.dll; do
  [[ -f "$out/lhm/$f" ]] || { echo "missing $out/lhm/$f" >&2; exit 1; }
  cp "$out/lhm/$f" "$stage/lhm/"
done
cp LICENSE THIRD-PARTY-NOTICES.md "$stage/"
cp -R licenses "$stage/"

zip_name="NanashiBetterHardwareStats-${version}.zip"
"${PYTHON:-python3}" - "$stage" "dist/$zip_name" <<'PY'
from pathlib import Path
import sys
import zipfile
root = Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], "w", zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(root.rglob("*")):
        if path.is_file():
            archive.write(path, path.relative_to(root).as_posix())
PY
(cd dist && sha256sum "$zip_name" > "$zip_name.sha256")
echo "zip=dist/$zip_name"
echo "sha=dist/$zip_name.sha256"
