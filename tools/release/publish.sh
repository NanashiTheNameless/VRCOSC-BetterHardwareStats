#!/usr/bin/env bash
set -euo pipefail
tag="v$VERSION"
zip_name="$(basename "$ZIP")"
checksum_name="$(basename "$CHECKSUM")"

# Listing failures must stop publication instead of being treated as a missing release.
tags="$(gh release list --limit 1000 --json tagName --jq '.[].tagName')"
if grep -Fxq "$tag" <<< "$tags"; then
  assets="$(gh release view "$tag" --json assets --jq '.assets[].name')"
  has_zip=false
  has_checksum=false
  grep -Fxq "$zip_name" <<< "$assets" && has_zip=true
  grep -Fxq "$checksum_name" <<< "$assets" && has_checksum=true
  if [ "$has_zip" = true ] && [ "$has_checksum" = true ]; then
    echo "Release $tag is already published."
    exit 0
  fi

  temporary="$(mktemp -d)"
  trap 'rm -rf "$temporary"' EXIT
  if [ "$has_zip" = true ]; then
    gh release download "$tag" --pattern "$zip_name" --dir "$temporary"
    (cd "$temporary" && sha256sum "$zip_name" > "$checksum_name")
    gh release upload "$tag" "$temporary/$checksum_name"
  elif [ "$has_checksum" = true ]; then
    gh release download "$tag" --pattern "$checksum_name" --dir "$temporary"
    expected="$(awk '{print $1}' "$temporary/$checksum_name")"
    actual="$(sha256sum "$ZIP" | awk '{print $1}')"
    [[ "$expected" == "$actual" ]] || { echo "Existing checksum does not match the rebuilt ZIP; refusing to replace release assets." >&2; exit 1; }
    gh release upload "$tag" "$ZIP"
  else
    gh release upload "$tag" "$ZIP" "$CHECKSUM"
  fi
  exit 0
fi

if [ "$NIGHTLY" = true ]; then
  notes="$(mktemp)"
  trap 'rm -f "$notes"' EXIT
  printf 'Nightly build of commit %s (%s).\n\n' "$SHA7" "$GITHUB_SHA" > "$notes"
  git log -1 --format='%s' >> "$notes"
  gh release create "$tag" "$ZIP" "$CHECKSUM" --target "$GITHUB_SHA" --latest \
    --title "$tag (nightly build of $SHA7)" --notes-file "$notes"
else
  previous="$(git tag --list 'v*' | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | grep -v -x "$tag" | sort -V | tail -n 1 || true)"
  args=(--verify-tag --latest --title "$tag" --generate-notes)
  if [ -n "$previous" ]; then args+=(--notes-start-tag "$previous"); fi
  gh release create "$tag" "$ZIP" "$CHECKSUM" "${args[@]}"
fi
