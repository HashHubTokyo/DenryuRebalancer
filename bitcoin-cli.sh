#!/usr/bin/env bash
set -u

docker exec -it denryurebalancer_bitcoind_1 bitcoin-cli -rpcuser=0I5rfLbJEXsg -rpcpassword=yJt7h7D8JpQy -rpcport=43782 -regtest "$@"
