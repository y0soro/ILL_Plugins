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

    shared_name=$name
    shared_src=$name
    shared_dir=
    if [[ $namespace] == "ILL_"* ]]; then
        shared_name=$namespace
        shared_src="Shared_${namespace#ILL_}"
        shared_dir="$namespace/"
    fi

    version=$(git_version $shared_name)

    tmp=$(mktemp -d)

    dist="src/${name}/dist"
    if [[ -d $dist ]]; then
        cp -r "${dist}/." $tmp
    fi

    pushd $tmp

    plugin_dir=./BepInEx/plugins
    if [[ ${name} == *"Patcher"* ]]; then
        plugin_dir=./BepInEx/patchers
    fi
    plugin_dir="${plugin_dir}/${shared_name}"

    install -D "${DIR}/artifacts/bin/${name}/release/"*.dll -t $plugin_dir
    install -D "${DIR}/LICENSE" -t $plugin_dir

    # TODO: resolve and replace relative links in markdown
    readme="${DIR}/src/${shared_src}/README.md"
    if [[ -e $readme ]]; then
        install -D $readme -t $plugin_dir
    fi

    outdir="$DIR/artifacts/${shared_dir}"
    mkdir -p $outdir

    zipFile="${outdir}${name}-${version}.zip"

    rm -f $zipFile
    zip -r $zipFile .
    strip-nondeterminism -T $(TZ=UTC date -d "today 12:00:00" +%s) $zipFile
    popd
done
