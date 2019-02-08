module DenryuRebalancer.Tests.LndTests
open System
open DenryuRebalancer
open DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open FSharp.Control.Tasks.V2.ContextInsensitive
open Xunit
open Xunit.Abstractions
open FNBitcoin.TestFramework
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
        let! _ = builder.OpenChannelAsync(clients.Rebalancer, clients.ThirdParty, Money.Satoshis(80_000m))
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | Pending -> ()
        | other -> failwithf "%A" other

        // case3: after confirmation (rebalancer -> thirdParty)
        output.WriteLine("case 3")
        clients.Bitcoin.Generate(6) |> ignore
        do! Task.Delay(500) // Unfortunately we must wait lnd to sync...
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | NoRouteToCustodyNode -> ()
        | other -> failwithf "%A" other

        // case4: after opening pending channel (custody -> thirdParty)
        output.WriteLine("case 4")
        let! _ = builder.OpenChannelAsync(clients.Custody, clients.ThirdParty, Money.Satoshis(80_000m))
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | NoRouteToCustodyNode -> ()
        | other -> failwithf "%A" other

        // case5: after opening whole channels (but custody can not receive yet)
        output.WriteLine("case 5")
        clients.Bitcoin.Generate(5) |> ignore
        do! Async.Sleep(500)
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | NoRouteToCustodyNode -> ()
        | other -> failwithf "%A" other

        // case6: Success case
        output.WriteLine("case 6")
        let! invoice = clients.ThirdParty.CreateInvoice(LightMoney.op_Implicit(REBALANCE_UNIT_AMOUNT + 1000), "RouteCheckTest", TimeSpan.FromMinutes(5.0), new CancellationToken()) |> Async.AwaitTask
        use! listener = clients.ThirdParty.Listen()
        let waitTask = listener.WaitInvoice(new CancellationToken())
        let! _ = clients.Custody.Pay(invoice.BOLT11)
        let! paidInvoice = waitTask
        Assert.True(paidInvoice.PaidAt.HasValue)
        output.WriteLine("checking rout")
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | HasActiveRoute -> ()
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