module DenryuRebalancer.Tests.LndTests
open System
open DenryuRebalancer
open BTCPayServer.Lightning
open Xunit
open DenryuRebalancer.LightningClient
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks
open BTCPayServer.Lightning.LND
open NBitcoin.RPC
open System.Threading


let getCustodyClients (fac: ILightningClientFactory) : ILightningClient array =
  let clientsConfigs: LightningClientConfig seq = seq {
    yield { name = "custody1"; ConnectionString = "type=lnd-rest;server=https://lnd:lnd@127.0.0.1:42802;allowinsecure=true" }
  }
  clientsConfigs |> Seq.toArray  |>  Array.map(fun s -> fac.Create(s.ConnectionString))

let CHANNEL_AMOUNT_SATOSHI = 100000m

let getClients() =
  let network = NBitcoin.Network.GetNetwork("regtest")

  // for bitcoind
  let credential = RPCCredentialString.Parse("0I5rfLbJEXsg:yJt7h7D8JpQy") // username and password
  let uri = new Uri("http://localhost:43782")
  let btcClient = new NBitcoin.RPC.RPCClient(credential, uri, network)

  // for rebalancer
  let lnClientFactory = new LightningClientFactory(network)
  let connectionString = "type=lnd-rest;server=https://lnd:lnd@127.0.0.1:32736;allowinsecure=true"
  let rebalancerClient = lnClientFactory.Create(connectionString)

  // for virtual 3rd-party node
  let thirdPartyClient = lnClientFactory.Create("type=lnd-rest;server=https://lnd:lnd@127.0.0.1:42804;allowinsecure=true")
  (btcClient, rebalancerClient :?> LndClient, getCustodyClients lnClientFactory, thirdPartyClient) // get clients for custodies and return

let generateOneBlockForClient (btc: RPCClient) (client: ILightningClient) =
  task {
    let! a =  client.GetDepositAddress()
    let _ = btc.GenerateToAddress(1, a)
    return ()
  }

let connect (thirdParty: NodeInfo) (client: ILightningClient) =
  task {
    let! _ = client.ConnectTo(thirdParty)
    let request = new OpenChannelRequest()
    request.NodeInfo <- thirdParty
    request.ChannelAmount <- NBitcoin.Money.Satoshis(CHANNEL_AMOUNT_SATOSHI)
    request.FeeRate <- new NBitcoin.FeeRate(0.0004m)
    let! _ = client.OpenChannel(request)
    return ()
  }

/// 1. generate one block per to the address for each LN node.
/// 2. genereate 100 (for coinbase maturity)
/// 3. connect "custody or rebalaner" -> "third party node" with 100000 satoshi
let prepareNodes
  (bitcoinClient: RPCClient)
  (rebalancerClient: LndClient)
  (custodyClients: ILightningClient seq)
  (thirdPartyClient: ILightningClient) =
  task {
    // prepare funds
    let generator = generateOneBlockForClient bitcoinClient
    let allClientsSeq = seq {
        yield (rebalancerClient :> ILightningClient);
        yield thirdPartyClient;
        for c in custodyClients do yield c
      }
    let! _ = allClientsSeq |> Seq.map(generator) |> Task.WhenAll
    let! _ = bitcoinClient.GenerateAsync(100)

    // connect and broadcast funding tx
    let! info = thirdPartyClient.GetInfo()
    printf "NodeInfo is %s" (info.NodeInfo.ToString())
    let! _ = connect info.NodeInfo (rebalancerClient :> ILightningClient)
    let! _ = custodyClients |> Seq.map(connect info.NodeInfo) |> Task.WhenAll

    // confirm funding tx
    let! _ = bitcoinClient.GenerateAsync(3)
    return ()
  }

let checkResult (x: RebalancingStrategy.RebalanceResult) =
  match x with
  | Ok i -> printf "OK!"
  | Error e -> printf "Got Error from executeRebalance. \n %s \n" e; Assert.True(false)

let prepareNodesIfNecessary () =
  let (btcClient, rebalancerClient, custodyClients, thirdParty) = getClients()
  // prepare channels for the first time.
  let nf = Nullable<bool>(false)
  let tf = Nullable<bool>(true)
  task {
    let! channel = rebalancerClient.SwaggerClient.ListChannelsAsync(tf, nf, nf, nf)
    if channel.Channels = null then
      do! prepareNodes btcClient rebalancerClient custodyClients thirdParty
      printf "preparing nodes ... "
    let! channelFromRebalancer = rebalancerClient.SwaggerClient.ListChannelsAsync(tf, nf, nf, nf)
    Assert.NotNull(channelFromRebalancer.Channels)
    Assert.NotEmpty(channelFromRebalancer.Channels)
  }

(*
[<Fact>]
let ``Should check route`` () =
  prepareNodesIfNecessary()
  RebalancingStrategy.checkRoute

[<Fact>]
let ``Should perform rebalancing properly`` () =
  prepareNodesIfNecessary()
  let (btcClient, rebalancerClient, custodyClients, thirdParty) = getClients()


  let threshold = LightMoney.Satoshis(CHANNEL_AMOUNT_SATOSHI + 1m)

  let results = custodyClients
                |> Seq.map(fun c -> RebalancingStrategy.extecuteRebalance rebalancerClient c threshold CancellationToken.None RebalancingStrategy.Default)
                |> Async.Parallel
                |> Async.RunSynchronously
  results |> Array.map(checkResult) |> ignore
  let postRebalanceAmount = rebalancerClient.SwaggerClient.ChannelBalanceAsync() |> Async.AwaitTask |> Async.RunSynchronously
  let a = snd (Decimal.TryParse(postRebalanceAmount.Balance))
  Assert.True(a < CHANNEL_AMOUNT_SATOSHI, "Rebalance performed but the amount in rebalancer has not reduced!")
  ()

*)