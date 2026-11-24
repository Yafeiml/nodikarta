#!/usr/bin/env bash
set -euo pipefail

# Run on Linux with the .NET 10 Native AOT toolchain, or package a merged native publish directory.
repo=$(cd "$(dirname "$0")/../.." && pwd)
# Both projects declare their product version as a literal. This also works when
# packaging an existing native publish directory on a machine without the SDK.
project_version() {
    sed -nE 's@^[[:space:]]*<Version>([^<]+)</Version>[[:space:]]*$@\1@p' "$repo/src/$1/$1.csproj"
}
version=$(project_version ProjectHub.Edge)
desktop_version=$(project_version ProjectHub.Edge.Desktop)
if [[ ! $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ || $desktop_version != "$version" ]]; then
    echo "Expected matching release versions in both projects; CLI='$version', desktop='$desktop_version'." >&2
    exit 1
fi
if [[ -n ${EDGE_VERSION:-} && $EDGE_VERSION != "$version" ]]; then
    echo "Requested Edge version '$EDGE_VERSION' differs from the checked-out product version '$version'." >&2
    exit 1
fi
deb_version=${version/-/\~}
out=${1:-"$repo/artifacts/edge-linux/$version"}
publish=${2:-}
mkdir -p -- "$out"
out=$(cd "$out" && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
name="Nodicarta-Edge-$version-linux-x64"
app="$work/package/opt/nodicarta-edge"
mkdir -p -- "$app"
if [[ -n "$publish" ]]; then
    cp -R -- "$publish/." "$app/"
else
    for project in ProjectHub.Edge.Desktop ProjectHub.Edge; do
        dest="$work/publish/$project"
        dotnet publish "$repo/src/$project/$project.csproj" \
            -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:EdgeLinux=true \
            -p:DebugType=None -p:DebugSymbols=false -o "$dest"
        install -m 755 "$dest/$project" "$app/$project"
        for native in "$dest"/*.so*; do
            [[ -f "$native" ]] || continue
            other="$app/$(basename "$native")"
            if [[ -f "$other" ]]; then cmp "$native" "$other"; else cp -- "$native" "$other"; fi
        done
    done
    cp -R -- "$work/publish/ProjectHub.Edge.Desktop/skills" "$app/"
    cp -R -- "$work/publish/ProjectHub.Edge.Desktop/Assets" "$app/"
fi
for binary in ProjectHub.Edge.Desktop ProjectHub.Edge; do
    [[ $(od -An -tx1 -N4 "$app/$binary" | tr -d ' \n') == 7f454c46 ]] || { echo "Missing Linux executable: $binary" >&2; exit 1; }
    [[ $(od -An -tx1 -j18 -N2 "$app/$binary" | tr -d ' \n') == 3e00 ]] || { echo "Expected x86_64: $binary" >&2; exit 1; }
    readelf -SW "$app/$binary" | grep -F ' .dotnet_eh_table ' >/dev/null || { echo "Missing Native AOT code: $binary" >&2; exit 1; }
    [[ $("$app/$binary" --version) == "$version" ]] || { echo "Unexpected version: $binary" >&2; exit 1; }
done
if [[ -n $(find "$app" \( -name 'libclrjit.so' -o -name 'libcoreclr.so' -o -name '*.runtimeconfig.json' -o -name '*.dll' \) -print -quit) ]]; then
    echo 'Unexpected managed application or JIT runtime in Native AOT package.' >&2; exit 1
fi
glibc=$(readelf -WV "$app"/ProjectHub.Edge{,.Desktop} "$app"/*.so | sed -nE 's/.*Name: GLIBC_([0-9.]+) .*/\1/p' | sort -Vu | tail -1)
[[ -n "$glibc" ]]
cp -- "$repo/LICENSE" "$repo/THIRD-PARTY-NOTICES.md" "$app/"
mkdir -p "$app/release"
cp -R -- "$repo/release/licenses" "$app/release/"
cp -- "$repo/deploy/linux/README.md" "$app/README.md"
find "$app" -type d -exec chmod 755 {} +
find "$app" -type f -exec chmod 644 {} +
chmod 755 "$app/ProjectHub.Edge.Desktop" "$app/ProjectHub.Edge"
tar --owner=0 --group=0 --transform="s,^nodicarta-edge,$name," -C "$work/package/opt" \
    -czf "$work/$name.tar.gz" nodicarta-edge

pkg="$work/package"
mkdir -p "$pkg/DEBIAN" "$pkg/usr/share/applications" "$pkg/usr/share/icons/hicolor/scalable/apps" "$pkg/usr/bin"
cp "$repo/deploy/linux/nodicarta-edge.desktop" "$pkg/usr/share/applications/"
cp "$repo/src/ProjectHub.Edge.Desktop/Assets/edge.svg" "$pkg/usr/share/icons/hicolor/scalable/apps/nodicarta-edge.svg"
ln -s /opt/nodicarta-edge/ProjectHub.Edge.Desktop "$pkg/usr/bin/nodicarta-edge"
ln -s /opt/nodicarta-edge/ProjectHub.Edge "$pkg/usr/bin/nodicarta-edge-cli"
cat > "$pkg/DEBIAN/control" <<EOF
Package: nodicarta-edge
Version: $deb_version
Architecture: amd64
Maintainer: Nodicarta contributors
Section: utils
Priority: optional
Depends: libc6 (>= $glibc), libgcc-s1, libstdc++6, libicu78 | libicu76 | libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, zlib1g, libx11-6, libxrandr2, libxi6, libxcursor1, libice6, libsm6, libfontconfig1
Recommends: fonts-noto-cjk, xdg-utils
Description: Nodicarta Edge desktop and MCP client for Linux
EOF
deb="nodicarta-edge_${deb_version}_amd64.deb"
dpkg-deb --root-owner-group -Zgzip --build "$pkg" "$work/$deb"
mv -- "$work/$name.tar.gz" "$work/$deb" "$out/"
(cd "$out" && sha256sum "$name.tar.gz" "$deb" > SHA256SUMS)
echo "$out/$name.tar.gz"
echo "$out/$deb"
