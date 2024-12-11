#!/usr/bin/env bash
set -e

DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)

# set -p:PublishTrimmed=false to avoid triggering virus false-positive,
# see https://github.com/dotnet/runtime/issues/33745
dotnet build --configuration Release -p:ContinuousIntegrationBuild=true -p:PublishTrimmed=false

for i in src/*; do
    name="${i#src/}"
    name="${name%/}"

    version=$(sed -n 's/.*<Version>\(.*\)<.*/\1/p' "src/${name}/"*.csproj | tr -d '\n')

    echo $name

    tmp=$(mktemp -d)
    cd $tmp

    install -D "${DIR}/artifacts/bin/${name}/release/"*.dll -t ./BepInEx/plugins

    zipFile="$DIR/artifacts/${name}-v${version}.zip"

    rm -f $zipFile
    zip -r $zipFile .
done
