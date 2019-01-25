#!/usr/bin/env bash
set -u

docker exec -ti denryurebalancertests_lnd_for_balancer_1 lncli -n regtest -no-macaroons "$@"
