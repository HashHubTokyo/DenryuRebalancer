#!/usr/bin/env bash
set -u

docker exec -ti denryurebalancertests_lnd_in_custody_1 lncli -n regtest -no-macaroons "$@"
