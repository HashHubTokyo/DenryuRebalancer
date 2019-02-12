module DenryuRebalancer.RebalancingStrategy
open FSharp.Control.Tasks.V2.ContextInsensitive
open BTCPayServer.Lightning.LND
open System.Threading
open System.Threading.Tasks
open BTCPayServer.Lightning
open System
open System.Globalization
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


type RouteStatus = 
| HasActiveRoute of ICollection<LnrpcRoute>
| Pending
| NoRouteToThirdPartyNode
| NoRouteToCustodyNode

let nullOrEmpty (x: ICollection<_>) =
    x = null || x.Count = 0

let checkRoute (client: LndClient) (custodyId: PubKey) (thirdPartyId: PubKey option) (token: CancellationToken): Task<RouteStatus> =
  task { 

    let! pendingC = client.SwaggerClient.PendingChannelsAsync()
    if pendingC.Pending_open_channels |> nullOrEmpty |> not then
        return Pending
    else 
        try
            let! route = client.SwaggerClient.QueryRoutesAsync(custodyId.ToHex(),
                                                               Convert.ToString(REBALANCE_UNIT_AMOUNT, CultureInfo.InvariantCulture),
                                                               Nullable<int>(10))

            return HasActiveRoute (route.Routes :> ICollection<LnrpcRoute>)
        with
        | :? SwaggerException ->
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
                    let! _ = client.SwaggerClient.QueryRoutesAsync(i.ToHex(), Convert.ToString(REBALANCE_UNIT_AMOUNT, CultureInfo.InvariantCulture), Nullable<int>()) |> Async.AwaitTask
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
    let! invoice = custodyClient.CreateInvoice(LightMoney.op_Implicit(REBALANCE_UNIT_AMOUNT * 1000), invoiceIdStr, TimeSpan.FromMinutes(5.0), token) |> Async.AwaitTask
    let! result = (client :> ILightningClient).Pay(invoice.BOLT11, token) |> Async.AwaitTask
    match result.Result with
      | PayResult.Ok -> return Ok(Some {NodeName = "node1"; Amount = LightMoney.op_Implicit(REBALANCE_UNIT_AMOUNT * 1000)})
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
                      (whenNoRoute: WhenNoRouteBehaviour): Task<RebalanceResult> =
  task {
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
          | HasActiveRoute route -> return! executeRebalanceCore client custodyClient token
          | Pending -> return Ok None
          | NoRouteToThirdPartyNode -> match whenNoRoute with
                                       | Default -> return! defaultBehaviourWhenNoRoute client custodyClient
                                       | Custom func -> return! (func client custodyClient)
          | NoRouteToCustodyNode -> return Error ("Rebalancer could not reach to the custody node by traversing the channels! please check if custody node has openned the correct channel.")
    with
    | CustodyNotSupportedException msg -> return Error (sprintf "Failed to rebalance! %s" msg)
  }