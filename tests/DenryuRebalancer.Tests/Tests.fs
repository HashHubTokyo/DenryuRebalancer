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
  | Ok i -> printfn "OK! %A" i
  | Error e -> printfn "Got Error from executeRebalance. \n %s \n" e; Assert.True(false)


let pay (from: ILightningClient, dest: ILightningClient, amountMilliSatoshi: LightMoney) =
    task {
        let! invoice = dest.CreateInvoice(amountMilliSatoshi, "RouteCheckTest", TimeSpan.FromMinutes(5.0), new CancellationToken())
        use! listener = dest.Listen()
        let waitTask = listener.WaitInvoice(new CancellationToken())
        let! _ = from.Pay(invoice.BOLT11)
        let! paidInvoice = waitTask
        Assert.True(paidInvoice.PaidAt.HasValue)
        return ()
    }

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

        do! builder.PrepareBTCFundsAsync()
        let! _ = builder.PrepareLNFundsAsync(Money.Satoshis(1_000_000m))

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
        do! pay(clients.Custody, clients.ThirdParty, LightMoney.op_Implicit((1000+ 50000) * 1000))
        match! checkRoute clients.Rebalancer custodyId None CancellationToken.None with
        | HasActiveRoute r -> Assert.NotEmpty(r)
        | other -> failwithf "%A" other

      }

    let getBalanceInChannel (client: ILightningClient) =
        async {
            let! channels = client.ListChannels() |> Async.AwaitTask
            return channels |> Array.map(fun c -> c.LocalBalance) |> Array.reduce(+)
        }

    [<Fact>]
    let ``Should perform rebalancing properly`` () =
      async {
        use builder = lnLauncher.createBuilder()
        builder.startNode()
        // builder.ConnectAll() |> ignore
        do! builder.PrepareBTCFundsAsync() |> Async.AwaitTask
        let clients = builder.GetClients()
        let channelFunds = 200_000m
        let! _ =  builder.PrepareLNFundsAsync(Money.Satoshis(1_000_000m)) |> Async.AwaitTask
        builder.OpenChannel(clients.Bitcoin, clients.Rebalancer, clients.ThirdParty, Money.Satoshis(channelFunds))
        builder.OpenChannel(clients.Bitcoin, clients.ThirdParty, clients.Custody, Money.Satoshis(channelFunds))

        do! builder.Pay(clients.ThirdParty, clients.Custody, LightMoney.op_Implicit((50000) * 1000))

        let rebalancer = (clients.Rebalancer :> ILightningClient)
        let! preRebalanceAmount = rebalancer |> getBalanceInChannel
        let! preRebalanceAmountCustody = clients.Custody |> getBalanceInChannel

        // 1. perform rebalance
        let! result = executeRebalanceCore clients.Rebalancer clients.Custody CancellationToken.None
        checkResult result

        let! postRebalanceAmount1 = rebalancer |> getBalanceInChannel
        let! postRebalanceAmountCustody1 = clients.Custody |> getBalanceInChannel

        Assert.True(postRebalanceAmount1 < preRebalanceAmount,
                    sprintf "Rebalance performed but the amount in rebalancer has not reduced! %s" (postRebalanceAmount1.ToString()))
        Assert.True(preRebalanceAmountCustody < postRebalanceAmountCustody1,
                    sprintf "Rebalance performed but the amount in custody has not increased! %s" (postRebalanceAmountCustody1.ToString()))

        // 2. check rebalance threshold and perform rebalance
        let threshold = LightMoney.Satoshis(2_000_000m)
        let! result = executeRebalance clients.Rebalancer clients.Custody threshold CancellationToken.None WhenNoRouteBehaviour.Default
        checkResult result

        let! postRebalanceAmount2 = rebalancer |> getBalanceInChannel

        Assert.True(postRebalanceAmount2 < postRebalanceAmount1,
                    sprintf "Rebalance performed but the amount in rebalancer has not reduced! %s" (postRebalanceAmount2.ToString()))
        ()
      }
