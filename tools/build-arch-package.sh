#!/usr/bin/env bash
# Package the current committed checkout, with its revision and exact archive
# checksum. Does not install packages or enable/restart any user service.
set -euo pipefail
repo=$(git rev-parse --show-toplevel)
version=$(sed -n 's/^pkgver=//p' "$repo/packaging/arch/PKGBUILD")
build=$(mktemp -d -t openxlr-arch-build.XXXXXXXX)
printf 'Building in %s (retained for inspection)\n' "$build"
export OPENXLR_BUILD_REVISION
OPENXLR_BUILD_REVISION=$(git rev-parse HEAD)
git archive --format=tar.gz --prefix="openxlr-$version/" -o "$build/openxlr-$version.tar.gz" HEAD
cp "$repo/packaging/arch/PKGBUILD" "$build/PKGBUILD"
export OPENXLR_SOURCE_SHA256
OPENXLR_SOURCE_SHA256=$(sha256sum "$build/openxlr-$version.tar.gz" | cut -d ' ' -f1)
(cd "$build" && makepkg --noconfirm)
mkdir -p "$repo/dist"
cp "$build"/openxlr-*.pkg.tar.zst "$repo/dist/"
