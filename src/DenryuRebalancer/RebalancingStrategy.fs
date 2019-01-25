module DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open BTCPayServer.Lightning.LND
open FSharp.Control.Tasks
open NBitcoin

type RebalanceOperationResult =
  {
    NodeName: string
    Amount: int64
  }

let extecuteRebalance (client: LndClient)
                      (custodyClient: ILightningClient) =
  task {
    return Ok { NodeName = "HogeClient"; Amount = Money.Coins(0.01m).Satoshi }
  }