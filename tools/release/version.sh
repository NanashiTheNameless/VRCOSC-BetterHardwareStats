#!/usr/bin/env bash
# Print version, nightly and sha7 outputs for the release workflow.
# Nightly tags use the commit timestamp for ordering and a prefixed SHA for valid semver.
# The module version stays at the base defined in Directory.Build.props.
set -euo pipefail
ref_type="${GITHUB_REF_TYPE:-branch}"
ref_name="${GITHUB_REF_NAME:-main}"
sha7="$(git rev-parse --short=7 "${GITHUB_SHA:-HEAD}")"

base="$(sed -n 's:.*<Version>\([0-9]*\.[0-9]*\.[0-9]*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
[[ -n "$base" ]] || { echo "No Version in Directory.Build.props" >&2; exit 1; }

if [[ "$ref_type" == "tag" ]]; then
  if [[ ! "$ref_name" =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
    echo "Tag '$ref_name' is not vMAJOR.MINOR.PATCH" >&2
    exit 1
  fi
  [[ "${ref_name#v}" == "$base" ]] || { echo "Tag must match the configured version $base in Directory.Build.props" >&2; exit 1; }
  echo "version=$base"
  echo "nightly=false"
  echo "sha7=${sha7}"
  exit 0
fi

stamp="$(TZ=UTC git show -s --format=%cd --date=format-local:%Y%m%d%H%M%S "${GITHUB_SHA:-HEAD}")"
echo "version=${base}-nightly.${stamp}.g${sha7}"
echo "nightly=true"
echo "sha7=${sha7}"
