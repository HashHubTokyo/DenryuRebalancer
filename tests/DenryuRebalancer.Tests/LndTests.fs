module DenryuRebalancer.Tests.LndTests
open System
open DenryuRebalancer
open BTCPayServer.Lightning
open Xunit
open DenryuRebalancer.LightningClient
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks
open BTCPayServer.Lightning.LND
open BTCPayServer.Lightning
open NBitcoin.RPC
open Confluent.Kafka


let getCustodyClients (fac: ILightningClientFactory) : ILightningClient array =
  let clientsConfigs: LightningClientConfig seq = seq {
    yield { name = "custody1"; ConnectionString = "type=lnd-rest;server=https://lnd:lnd@127.0.0.1:42802;allowinsecure=true" }
  }
  clientsConfigs |> Seq.toArray  |>  Array.map(fun s -> fac.Create(s.ConnectionString))

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
    request.ChannelAmount <- NBitcoin.Money.Coins(0.01m)
    request.FeeRate <- new NBitcoin.FeeRate(0.0004m)
    let! _ = client.OpenChannel(request)
    return ()
  }
let prepareNodes
  (bitcoinClient: RPCClient)
  (rebalancerClient: LndClient)
  (custodyClients: ILightningClient seq)
  (thirdPartyClient: ILightningClient) =
  task {
    // prepare funds
    let! _ = generateOneBlockForClient bitcoinClient (rebalancerClient :> ILightningClient)
    let! _ = custodyClients |> Seq.map(generateOneBlockForClient bitcoinClient) |> Task.WhenAll
    let! _ = bitcoinClient.GenerateAsync(100)

    // connect and broadcast funding tx
    let! info = thirdPartyClient.GetInfo()
    let! _ = connect info.NodeInfo (rebalancerClient :> ILightningClient)
    let! _ = custodyClients |> Seq.map(connect info.NodeInfo) |> Task.WhenAll

    // confirm funding tx
    let! _ = bitcoinClient.GenerateAsync(3)
    return ()
  }

let checkResult = function
  | Ok i -> printf "OK!"
  | Error e -> Assert.True(false, "Got Error from executeRebalance")

[<Fact>]
let ``Should perform rebalancing properly`` () =
  let (btcClient, rebalancerClient, custodyClients, thirdParty) = getClients()

  // prepare channels for the first time.
  let nf = Nullable<bool>(false)
  let tf = Nullable<bool>(true)
  let channel = rebalancerClient.SwaggerClient.ListChannelsAsync(tf, nf, nf, nf) |> Async.AwaitTask |> Async.RunSynchronously
  if channel.Channels = null then
    prepareNodes btcClient rebalancerClient custodyClients thirdParty |> Async.AwaitTask |> Async.RunSynchronously
    printf "preparing nodes ... "
  let channel2 = rebalancerClient.SwaggerClient.ListChannelsAsync(tf, nf, nf, nf) |> Async.AwaitTask |> Async.RunSynchronously
  Assert.NotNull(channel2.Channels)
  Assert.NotEmpty(channel2.Channels)

  let results = custodyClients
                |> Seq.map(fun c -> RebalancingStrategy.extecuteRebalance rebalancerClient c)
                |> Task.WhenAll
                |> Async.AwaitTask
                |> Async.RunSynchronously
  results |> Array.map(checkResult)
