module DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open BTCPayServer.Lightning.LND
open FSharp.Control.Tasks
open System.Threading.Tasks
open NBitcoin
open BTCPayServer.Lightning.LND
open BTCPayServer.Lightning.LND
open System.Threading

type RebalanceOperationReturnValue =
  {
    NodeName: string
    Amount: int64
  }

type RebalanceResult = Async<Result<RebalanceOperationReturnValue, string>>
exception CustodyNotSupportedException of string

let extecuteRebalance (client: LndClient)
                      (custodyClient: ILightningClient)
                      (token: CancellationToken): RebalanceResult =
  async {
    try
      let custodyLndClient = match custodyClient with
                             | :? LndClient as l -> l
                             | _ -> raise (CustodyNotSupportedException "The rebalancer currently only supports lnd for its custody!")
      let! channelInfo = custodyLndClient.SwaggerClient.ChannelBalanceAsync() |> Async.AwaitTask
      return Ok { NodeName = "hoge"; Amount = 1L }
    with
      | CustodyNotSupportedException msg -> return Error (sprintf "Failed to rebalance! %s" msg)
      | _ -> return Error(sprintf "Failed to rebalance")
  }