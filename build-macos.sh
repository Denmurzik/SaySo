#!/bin/bash
# Сборка SaySo для macOS: NativeAOT под arm64 и x86-64, универсальный исполняемый файл (lipo), пакет SaySo.app с иконкой,
# подпись ad-hoc и ZIP для релиза с файлом .sha256. Нужны .NET SDK из global.json и Xcode Command Line Tools
# (clang, ld, lipo, codesign); сам Xcode не нужен.
#   ./build-macos.sh                 → artifacts/macos/SaySo.app, artifacts/release/SaySo-X.Y.Z-macos.zip(.sha256) и .dmg(.sha256)
#   ./build-macos.sh --arch arm64    → только Apple Silicon (быстрее, для разработки)
set -euo pipefail
cd "$(dirname "$0")"

archs=(arm64 x64)
if [[ "${1:-}" == "--arch" ]]; then
    archs=("${2:?после --arch нужно arm64 или x64}")
fi

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Версия '$version' в Directory.Build.props не вида X.Y.Z" >&2
    exit 1
fi
echo "Версия: $version"

out=artifacts/macos
publish=$out/publish
app=$out/SaySo.app
rm -rf "$publish" "$app"
mkdir -p "$publish"

binaries=()
for arch in "${archs[@]}"; do
    echo "NativeAOT: osx-$arch"
    dotnet publish src/SaySo.Mac/SaySo.Mac.csproj -c Release -r "osx-$arch" -o "$publish/$arch" -nologo
    binaries+=("$publish/$arch/SaySo")
done

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
if [[ ${#binaries[@]} -gt 1 ]]; then
    lipo -create "${binaries[@]}" -output "$app/Contents/MacOS/SaySo"
else
    cp "${binaries[0]}" "$app/Contents/MacOS/SaySo"
fi
sed "s/@VERSION@/$version/g" src/SaySo.Mac/Info.plist > "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"

# Иконка: из docs/assets/icon.png (256×256) — набор размеров для .icns.
iconset=$out/SaySo.iconset
rm -rf "$iconset"
mkdir -p "$iconset"
for size in 16 32 64 128 256; do
    sips -z $size $size docs/assets/icon.png --out "$iconset/icon_${size}x${size}.png" >/dev/null
done
cp "$iconset/icon_32x32.png" "$iconset/icon_16x16@2x.png"
cp "$iconset/icon_64x64.png" "$iconset/icon_32x32@2x.png"
cp "$iconset/icon_256x256.png" "$iconset/icon_128x128@2x.png"
rm "$iconset/icon_64x64.png"
iconutil -c icns "$iconset" -o "$app/Contents/Resources/SaySo.icns"
rm -rf "$iconset"

# Подпись ad-hoc: без неё macOS на Apple Silicon не запустит программу; разрешения (микрофон, Универсальный доступ)
# macOS привязывает к подписи пакета.
codesign --force --sign - --identifier io.github.denmurzik.sayso "$app"
codesign --verify --strict "$app"

release=artifacts/release
mkdir -p "$release"
zip="$release/SaySo-$version-macos.zip"
rm -f "$zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
# Формат sha256sum, как у файлов Windows-релиза: хэш в нижнем регистре, два пробела, имя файла.
(cd "$release" && shasum -a 256 "SaySo-$version-macos.zip" > "SaySo-$version-macos.zip.sha256")

# Образ диска для установки перетаскиванием: SaySo.app и ссылка на «Программы».
dmg="$release/SaySo-$version-macos.dmg"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
ditto "$app" "$stage/SaySo.app"
ln -s /Applications "$stage/Applications"
rm -f "$dmg"
hdiutil create -volname "SaySo $version" -srcfolder "$stage" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$dmg"
hdiutil verify "$dmg"
(cd "$release" && shasum -a 256 "SaySo-$version-macos.dmg" > "SaySo-$version-macos.dmg.sha256")

size=$(du -sh "$app" | cut -f1)
echo "Готово: $app ($size, $(lipo -archs "$app/Contents/MacOS/SaySo")), $zip, $dmg"
