#!/usr/bin/env bash
set -u

readonly VERSION="1.0"
if [[ "$(uname)" == 'Darwin' ]]; then
  readonly SCRIPT_DIR_PATH=$(dirname $(greadlink -f $0))
else
  readonly SCRIPT_DIR_PATH=$(dirname $(readlink -f $0))
fi

addr1=$(./lncli-balancer.sh newaddress p2wkh | jq -r ".address")
addr2=$(./lncli-3rdparty.sh newaddress p2wkh | jq -r ".address")
addr3=$(./lncli-custody.sh newaddress p2wkh | jq -r ".address")
./bitcoin-cli.sh generatetoaddress 1 $addr1
./bitcoin-cli.sh generatetoaddress 1 $addr2
./bitcoin-cli.sh generatetoaddress 1 $addr3
./bitcoin-cli.sh generate 100
