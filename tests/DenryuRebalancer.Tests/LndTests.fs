module DenryuRebalancer.Tests.LndTests
open System
open DenryuRebalancer
open DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning
open Xunit
open Xunit.Abstractions
open FNBitcoin.TestFramework
open System.Threading
open NBitcoin


let checkResult (x: RebalancingStrategy.RebalanceResult) =
  match x with
  | Ok i -> printf "OK!"
  | Error e -> printf "Got Error from executeRebalance. \n %s \n" e; Assert.True(false)


type LndWatcherTestCase(output: ITestOutputHelper) =
    [<Fact>]
    let ``Should check route`` () =
      async {
        use builder = lnLauncher.createBuilder()
        builder.startNode()
        let! _ = builder.ConnectAll()
        let clients = builder.GetClients()

        // case1: before opening channel
        output.WriteLine("case 1")
        match! checkRoute clients.Rebalancer clients.Custody with
        | NoRouteToThirdPartyNode -> ()
        | other -> failwithf "%A" other

        let! _ = builder.PrepareFunds(Money.Satoshis(200000m))

        // case2: pending channel (rebalancer -> thirdParty)
        output.WriteLine("case 1")
        let! _ = builder.OpenChannel(clients.Rebalancer, clients.ThirdParty, Money.Satoshis(50000m))
        match! checkRoute clients.Rebalancer clients.Custody with
        | Pending -> ()
        | other -> failwithf "%A" other

        // case3: after confirmation (rebalancer -> thirdParty)
        output.WriteLine("case 3")
        let! _ = clients.Bitcoin.GenerateAsync(5) |> Async.AwaitTask
        match! checkRoute clients.Rebalancer clients.Custody with
        | NoRouteToCustodyNode -> ()
        | other -> failwithf "%A" other

        // case4: after opening pending channel (custody -> thirdParty)
        output.WriteLine("case 4")
        let! _ = builder.OpenChannel(clients.Custody, clients.ThirdParty, Money.Satoshis(50000m))
        match! checkRoute clients.Rebalancer clients.Custody with
        | Pending -> ()
        | other -> failwithf "%A" other

        // case5: after opening while channels
        output.WriteLine("case 5")
        let! _ = clients.Bitcoin.GenerateAsync(5) |> Async.AwaitTask
        match! checkRoute clients.Rebalancer clients.Custody with
        | HasActiveRoute-> ()
        | other -> failwithf "%A" other

      } |> Async.RunSynchronously


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