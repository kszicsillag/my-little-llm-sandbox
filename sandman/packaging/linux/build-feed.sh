#!/usr/bin/env bash
# Build the sandman .deb packages (amd64, arm64) and a signed flat APT repository from them.
#
#   build-feed.sh <version> [out-dir]
#
# Needs: dotnet 10 SDK, nfpm, apt-ftparchive (apt-utils), gpg with the signing key imported (the only secret key in
# the keyring, or set SIGNING_KEY to its id), envsubst (gettext-base).
# Env (optional): MAINTAINER ("Name <email>"), HOMEPAGE.
# Output: <out-dir>/feed/ = sandman_<version>_<arch>.deb, Packages(.gz), Release, InRelease, Release.gpg,
# sandman-archive-keyring.gpg. Upload all of it as release files; apt reads them as a flat repository.
set -euo pipefail

version=${1:?usage: build-feed.sh <version> [out-dir]}
[[ $version =~ ^[0-9] ]] || { echo "version must start with a digit (got '$version')" >&2; exit 2; }
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)
out=$(mkdir -p "${2:-$root/sandman/dist}" && cd "${2:-$root/sandman/dist}" && pwd)
feed="$out/feed"
rm -rf "$feed" && mkdir -p "$feed"

export MAINTAINER=${MAINTAINER:-"Sandman <sandman@users.noreply.github.com>"}
export HOMEPAGE=${HOMEPAGE:-"https://github.com"}
export VERSION=$version ROOT=$root

for rid in linux-x64 linux-arm64; do
  case $rid in linux-x64) ARCH=amd64 ;; linux-arm64) ARCH=arm64 ;; esac
  export ARCH BIN="$out/bin-$rid"
  rm -rf "$BIN"
  for project in Sandman.Cli Sandman.Tray; do
    # Self-contained (no runtime dependency), compressed single file; overrides the csproj's framework-dependent default.
    dotnet publish "$root/sandman/src/$project" -c Release -r "$rid" -o "$BIN" --nologo -v q \
      -p:SelfContained=true -p:EnableCompressionInSingleFile=true -p:Version="$version"
  done
  envsubst '$ARCH $VERSION $BIN $ROOT $MAINTAINER $HOMEPAGE' < "$here/nfpm.yaml" > "$out/nfpm-$ARCH.yaml"
  nfpm package --config "$out/nfpm-$ARCH.yaml" --packager deb --target "$feed/"
done

# Flat repository: Filename without "./", so the URLs are plain <base>/<file> (release download URLs).
cd "$feed"
apt-ftparchive packages . | sed 's|^Filename: \./|Filename: |' > Packages
gzip -9kn Packages
apt-ftparchive \
  -o APT::FTPArchive::Release::Origin=Sandman \
  -o APT::FTPArchive::Release::Label=Sandman \
  -o APT::FTPArchive::Release::Architectures="amd64 arm64" \
  release . > "$out/Release" && mv "$out/Release" Release

key=(); [[ -n ${SIGNING_KEY:-} ]] && key=(--local-user "$SIGNING_KEY")
gpg --batch --yes "${key[@]}" --clearsign -o InRelease Release
gpg --batch --yes "${key[@]}" --armor --detach-sign -o Release.gpg Release
if [[ -n ${SIGNING_KEY:-} ]]; then gpg --export "$SIGNING_KEY"; else gpg --export; fi > sandman-archive-keyring.gpg
[[ -s sandman-archive-keyring.gpg ]] || { echo "no public key exported" >&2; exit 1; }

ls -la "$feed"
