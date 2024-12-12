#!/usr/bin/env bash
set -e

DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)

cd $DIR

# set -p:PublishTrimmed=false to avoid triggering virus false-positive,
# see https://github.com/dotnet/runtime/issues/33745
dotnet build --configuration Release -p:ContinuousIntegrationBuild=true -p:PublishTrimmed=false

git_version() {
    local prefix=$1
    (
        set -o pipefail
        git describe --tags --abbrev=7 --match="${prefix}*" 2>/dev/null | sed 's/^[^-]*-//;s/\([^-]*-g\)/r\1/;s/-/./g' ||
            printf "r%s.%s" "$(git rev-list --count HEAD)" "$(git rev-parse --short=7 HEAD)"
    )
}

for i in src/*; do
    name="${i#src/}"
    name="${name%/}"

    # version=v$(sed -n 's/.*<Version>\(.*\)<.*/\1/p' "src/${name}/"*.csproj | tr -d '\n')

    version=$(git_version $name)

    tmp=$(mktemp -d)
    pushd $tmp

    install -D "${DIR}/artifacts/bin/${name}/release/"*.dll -t ./BepInEx/plugins

    zipFile="$DIR/artifacts/${name}-${version}.zip"

    rm -f $zipFile
    zip -r $zipFile .
    popd
done
