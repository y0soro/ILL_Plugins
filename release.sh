#!/usr/bin/env bash
set -e

DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)

# set -p:PublishTrimmed=false to avoid triggering virus false-positive,
# see https://github.com/dotnet/runtime/issues/33745
dotnet build --configuration Release -p:ContinuousIntegrationBuild=true -p:PublishTrimmed=false

version=$(sed -n 's/.*<Version>\(.*\)<.*/\1/p' Directory.Build.props | tr -d '\n')

for i in src/*; do
    name="${i#src/}"
    name="${name%/}"

    echo $name

    tmp=$(mktemp -d)
    cd $tmp

    install -D "${DIR}/artifacts/bin/${name}/release/"*.dll -t ./BepInEx/plugins

    tar --sort=name --owner=root:0 --group=root:0 --mtime='UTC 1970-01-01' \
        -a -cf "$DIR/artifacts/${name}-v${version}.zip" BepInEx
done
