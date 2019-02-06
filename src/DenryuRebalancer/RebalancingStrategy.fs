module DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open BTCPayServer.Lightning.LND
open System.Threading
open System

type RebalanceOperationReturnValue =
  {
    NodeName: string
    Amount: LightMoney
  }
type MaybeReblanceOperationReturn = RebalanceOperationReturnValue option

type RebalanceResult = Result<MaybeReblanceOperationReturn, string>
exception CustodyNotSupportedException of string

let REBALANCE_UNIT_AMOUNT = LightMoney.Satoshis(50000m)


type RouteStatus = HasActiveRoute | Pending | NoRouteToThirdPartyNode | NoRouteToCustodyNode


let checkRoute (client: LndClient) (custodyClient: ILightningClient): Async<RouteStatus> =
  async { 
  // prepare channels for the first time.
    let nf = Nullable<bool>(false)
    let tf = Nullable<bool>(true)
    let! activeChannels = client.SwaggerClient.ListChannelsAsync(tf, nf, nf, nf) |> Async.AwaitTask
    return HasActiveRoute
  }

let executeRebalanceCore (client: LndClient)
                          (custodyClient: ILightningClient)
                          (token: CancellationToken): Async<RebalanceResult> =
  async {
    let invoiceid = new Guid()
    let invoiceIdStr = "rebalance-" + invoiceid.ToString()
    let! invoice = custodyClient.CreateInvoice(REBALANCE_UNIT_AMOUNT, invoiceIdStr, TimeSpan.FromMinutes(5.0), token) |> Async.AwaitTask
    let! result = (client :> ILightningClient).Pay(invoice.BOLT11, token) |> Async.AwaitTask
    match result.Result with
      | PayResult.Ok -> return Ok(Some {NodeName = "node1"; Amount = REBALANCE_UNIT_AMOUNT})
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
        match! checkRoute client custodyClient with
          | HasActiveRoute -> return! executeRebalanceCore client custodyClient token
          | Pending -> return Ok None
          | NoRouteToThirdPartyNode -> match whenNoRoute with
                                       | Default -> return! defaultBehaviourWhenNoRoute client custodyClient
                                       | Custom func -> return! (func client custodyClient)
          | NoRouteToCustodyNode -> return Error ("Rebalancer could not reach to the custody node by traversing the channels! please check if custody node has openned the correct channel.")
    with
    | CustodyNotSupportedException msg -> return Error (sprintf "Failed to rebalance! %s" msg)
  }