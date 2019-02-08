module DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open BTCPayServer.Lightning.LND
open System.Threading
open System
open System.Collections.Generic
open NBitcoin

type RebalanceOperationReturnValue =
  {
    NodeName: string
    Amount: LightMoney
  }
type MaybeReblanceOperationReturn = RebalanceOperationReturnValue option

type RebalanceResult = Result<MaybeReblanceOperationReturn, string>
exception CustodyNotSupportedException of string

let REBALANCE_UNIT_AMOUNT = 50_000


type RouteStatus = HasActiveRoute | Pending | NoRouteToThirdPartyNode | NoRouteToCustodyNode

let nullOrEmpty (x: ICollection<_>) =
    x = null || x.Count = 0

let checkRoute (client: LndClient) (custodyId: PubKey) (thirdPartyId: PubKey option) (token: CancellationToken): Async<RouteStatus> =
  async { 

    let! pendingC = client.SwaggerClient.PendingChannelsAsync() |> Async.AwaitTask
    if pendingC.Pending_open_channels |> nullOrEmpty |> not then
        return Pending
    else 
        try
            let! _ = client.SwaggerClient.QueryRoutesAsync(custodyId.ToHex(), REBALANCE_UNIT_AMOUNT.ToString(), Nullable<int>()) |> Async.AwaitTask
            return HasActiveRoute
        with
        | :? AggregateException ->
            match thirdPartyId with
            | None ->
                 let nt = Nullable<bool>(true)
                 let nf = Nullable<bool>(false)
                 let! c = client.SwaggerClient.ListChannelsAsync(nt, nf, nf, nf) |> Async.AwaitTask
                 if c.Channels |> nullOrEmpty then
                     return NoRouteToThirdPartyNode
                 else
                     return NoRouteToCustodyNode
            | Some i -> 
                try
                    let! _ = client.SwaggerClient.QueryRoutesAsync(i.ToHex(), REBALANCE_UNIT_AMOUNT.ToString(), Nullable<int>()) |> Async.AwaitTask
                    return NoRouteToCustodyNode
                with
                | :? AggregateException ->
                    return NoRouteToThirdPartyNode
    }

let executeRebalanceCore (client: LndClient)
                          (custodyClient: ILightningClient)
                          (token: CancellationToken): Async<RebalanceResult> =
  async {
    let invoiceid = new Guid()
    let invoiceIdStr = "rebalance-" + invoiceid.ToString()
    let! invoice = custodyClient.CreateInvoice(LightMoney.op_Implicit(REBALANCE_UNIT_AMOUNT), invoiceIdStr, TimeSpan.FromMinutes(5.0), token) |> Async.AwaitTask
    let! result = (client :> ILightningClient).Pay(invoice.BOLT11, token) |> Async.AwaitTask
    match result.Result with
      | PayResult.Ok -> return Ok(Some {NodeName = "node1"; Amount = LightMoney.op_Implicit(REBALANCE_UNIT_AMOUNT)})
      | PayResult.CouldNotFindRoute -> return Error "Could not find route"
      | _ -> return Error "Unknown PayResult"
  }

type WhenNoRouteBehaviour = Default | Custom of (LndClient -> ILightningClient -> Async<RebalanceResult>)
let defaultBehaviourWhenNoRoute (client: LndClient) (custody: ILightningClient) =
  async {
    return Ok (None)
  }
let executeRebalance (client: LndClient)
                      (custodyClient: ILightningClient)
                      (threshold: LightMoney)
                      (token: CancellationToken)
                      (whenNoRoute: WhenNoRouteBehaviour): Async<RebalanceResult> =
  async {
    try
      let custodyLndClient = match custodyClient with
                             | :? LndClient as l -> l
                             | _ -> raise (CustodyNotSupportedException "The rebalancer currently only supports lnd for its custody!")
      let! channelInfo = custodyLndClient.SwaggerClient.ChannelBalanceAsync(token) |> Async.AwaitTask
      let balance = LightMoney.Parse(channelInfo.Balance)
      if (not (balance < threshold))
      then
        return Ok None
      else
        let! custodyId = custodyClient.GetInfo() |> Async.AwaitTask
        match! checkRoute client custodyId.NodeInfo.NodeId None token with
          | HasActiveRoute -> return! executeRebalanceCore client custodyClient token
          | Pending -> return Ok None
          | NoRouteToThirdPartyNode -> match whenNoRoute with
                                       | Default -> return! defaultBehaviourWhenNoRoute client custodyClient
                                       | Custom func -> return! (func client custodyClient)
          | NoRouteToCustodyNode -> return Error ("Rebalancer could not reach to the custody node by traversing the channels! please check if custody node has openned the correct channel.")
    with
    | CustodyNotSupportedException msg -> return Error (sprintf "Failed to rebalance! %s" msg)
  }