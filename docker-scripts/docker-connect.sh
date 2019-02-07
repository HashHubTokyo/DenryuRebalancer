#!/usr/bin/env bash
set -u

readonly VERSION="1.0"
if [[ "$(uname)" == 'Darwin' ]]; then
  readonly SCRIPT_DIR_PATH=$(dirname $(greadlink -f $0))
else
  readonly SCRIPT_DIR_PATH=$(dirname $(readlink -f $0))
fi

custodyId=$(./lncli-custody.sh getinfo | jq -r ".uris[0]")
thirdPartyId=$(./lncli-3rdparty.sh getinfo | jq -r ".uris[0]")
balancerId=$(./lncli-balancer.sh getinfo | jq -r ".uris[0]")
./lncli-balancer.sh connect $thirdPartyId
./lncli-balancer.sh connect $custodyId
./lncli-custody.sh connect $thirdPartyId
