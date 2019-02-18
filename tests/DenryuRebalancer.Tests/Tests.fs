module Tests
open System
open DenryuRebalancer
open DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open FSharp.Control.Tasks.V2.ContextInsensitive
open Xunit
open Xunit.Abstractions
open LNTestFramework.LightningNodeLauncher
open System.Threading
open System.Threading.Tasks
open NBitcoin


let checkResult (x: RebalancingStrategy.RebalanceResult) =
  match x with
  | Ok i -> printf "OK!"
  | Error e -> printf "Got Error from executeRebalance. \n %s \n" e; Assert.True(false)


type LndWatcherTestCase(output: ITestOutputHelper) =
    [<Fact>]
    let ``Should check route`` () =
      task {
        use builder = lnLauncher.createBuilder()
        builder.startNode()
        builder.ConnectAll() |> ignore
        let clients = builder.GetClients()

        let! custodyInfo = clients.Custody.GetInfo()
        let custodyId = custodyInfo.NodeInfo.NodeId

        // case1: before opening channel
        output.WriteLine("case 1")
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | NoRouteToThirdPartyNode -> ()
        | other -> failwithf "%A" other

        let! _ = builder.PrepareFunds(Money.Satoshis(200_000m))

        // case2: pending channel (rebalancer -> thirdParty)
        output.WriteLine("case 2")
        let! thirdPartyInfo = clients.ThirdParty.GetInfo()
        let request = new OpenChannelRequest()
        request.NodeInfo <- thirdPartyInfo.NodeInfo
        request.ChannelAmount <- Money.Satoshis(80_000m)
        request.FeeRate <- new NBitcoin.FeeRate(0.0004m)
        let! _ = (clients.Rebalancer :> ILightningClient).OpenChannel(request)
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | Pending -> ()
        | other -> failwithf "%A" other

        // case3: after confirmation (rebalancer -> thirdParty)
        output.WriteLine("case 3")
        clients.Bitcoin.Generate(6) |> ignore
        do! Task.Delay(1000) // Unfortunately we must wait lnd to sync...
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | NoRouteToCustodyNode -> ()
        | other -> failwithf "%A" other

        // case4: after opening whole channels (but custody can not receive yet)
        output.WriteLine("case 4")
        do! builder.OpenChannelAsync(clients.Bitcoin, clients.Custody, clients.ThirdParty, Money.Satoshis(80_000m))
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        // This should fail, but since current queryroutes rpc does not consider the bias for balance in the channel,
        // it will always returns non-empty routes
        | HasActiveRoute r -> Assert.NotEmpty(r)
        | other -> failwithf "%A" other

        // case5: Success case
        output.WriteLine("case 5")
        let! invoice = clients.ThirdParty.CreateInvoice(LightMoney.op_Implicit((1000 + 50000) * 1000), "RouteCheckTest", TimeSpan.FromMinutes(5.0), new CancellationToken())
        use! listener = clients.ThirdParty.Listen()
        let waitTask = listener.WaitInvoice(new CancellationToken())
        let! _ = clients.Custody.Pay(invoice.BOLT11)
        let! paidInvoice = waitTask
        Assert.True(paidInvoice.PaidAt.HasValue)
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | HasActiveRoute r -> Assert.NotEmpty(r)
        | other -> failwithf "%A" other

      }

    (*
    [<Fact>]
    let ``Should perform rebalancing properly`` () =
      async {
        use builder = lnLauncher.createBuilder()
        builder.startNode()
        let! _ = builder.PrepareFunds(Money.Satoshis(200000m))
        let threshold = LightMoney.Satoshis(200000m + 1m)
        let clients = builder.GetClients()
        let! result = executeRebalance clients.Rebalancer clients.Custody threshold CancellationToken.None RebalancingStrategy.Default
        checkResult result

        let! postRebalanceAmount = clients.Rebalancer.SwaggerClient.ChannelBalanceAsync()
        let a = snd (Decimal.TryParse(postRebalanceAmount.Balance))
        Assert.True(a < CHANNEL_AMOUNT_SATOSHI, "Rebalance performed but the amount in rebalancer has not reduced!")
        ()
      } |> Async.AwaitTask |> Async.RunSynchronously

      *)