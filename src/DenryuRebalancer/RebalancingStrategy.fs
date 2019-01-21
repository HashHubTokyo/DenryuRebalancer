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

let extecute (client: LndClient) (balancerInfo: LightningNodeInformation) (custodyInfo: LightningNodeInformation seq) =
  task {
    let! _ hoge = client.SwaggerClient
    return Ok { NodeName = "HogeClient"; Amount = Money.Coins(0.01m).Satoshi }
  }