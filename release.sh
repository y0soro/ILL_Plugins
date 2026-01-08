#!/usr/bin/env bash
set -e

DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)

cd $DIR

# set -p:PublishTrimmed=false to avoid triggering virus false-positive,
# see https://github.com/dotnet/runtime/issues/33745
dotnet build --configuration Release --no-incremental \
    -p:ContinuousIntegrationBuild=true \
    -p:PublishTrimmed=false \
    -p:SignAssembly=true \
    -p:AssemblyOriginatorKeyFile="$ILL_SN_KEY"

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

    if [[ $name] == "Shared_"* ]]; then
        continue
    fi

    namespace=$(sed -n 's/.*<RootNamespace>\(.*\)<.*/\1/p' "src/${name}/"*.csproj | tr -d '\n')

    version_prefix=$name
    dist_dir=
    if [[ $namespace] == "ILL_"* ]]; then
        version_prefix=$namespace
        dist_dir="$namespace/"
    fi

    version=$(git_version $version_prefix)

    tmp=$(mktemp -d)

    dist="src/${name}/dist"
    if [[ -d $dist ]]; then
        cp -r "${dist}/." $tmp
    fi

    pushd $tmp

    dest_dir=./BepInEx/plugins
    if [[ ${name} == *"Patcher"* ]]; then
        dest_dir=./BepInEx/patchers
    fi

    install -D "${DIR}/artifacts/bin/${name}/release/"*.dll -t $dest_dir

    outdir="$DIR/artifacts/${dist_dir}"
    mkdir -p $outdir

    zipFile="${outdir}${name}-${version}.zip"

    rm -f $zipFile
    zip -r $zipFile .
    strip-nondeterminism -T $(TZ=UTC date -d "today 12:00:00" +%s) $zipFile
    popd
done
