module DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open BTCPayServer.Lightning.LND
open FSharp.Control.Tasks
open System.Threading.Tasks
open NBitcoin

type RebalanceOperationReturnValue =
  {
    NodeName: string
    Amount: int64
  }

type RebalanceResult = Task<Result<RebalanceOperationReturnValue, string>>

let extecuteRebalance (client: LndClient)
                      (custodyClient: ILightningClient): RebalanceResult =
  task {
    return Error "Failed to rebalance"
  }