#!/usr/bin/env bash
set -u

docker exec -ti denryurebalancertests_lnd_3rd_party_1 lncli --network regtest -no-macaroons "$@"
