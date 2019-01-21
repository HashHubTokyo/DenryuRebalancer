module DenryuRebalancer.Tests.LndTests
open DenryuRebalancer
open BTCPayServer.Lightning
open Xunit
open DenryuRebalancer.LightningClient
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks

let getCustodyClients (fac: ILightningClientFactory) : ILightningClient array =
  let clientsConfigs: LightningClientConfig seq = seq {
    yield { name = "custody1"; ConnectionString = "type=lnd-rest;server=https://127.0.0.1:42802;allowinsecure=true" }
  }
  clientsConfigs |> Seq.toArray  |>  Array.map(fun s -> fac.Create(s.ConnectionString))

let getClients() =
  let network = NBitcoin.Network.GetNetwork("regtest")
  let lnClientFactory = new LightningClientFactory(network)
  let connectionString = "type=lnd-rest;server=https://127.0.0.1:32736;allowinsecure=true"
  let client = lnClientFactory.Create(connectionString)
  (client, getCustodyClients lnClientFactory)


[<Fact>]
let ``Should perform rebalancing properly`` =
  let (rebalancer, custodies) = getClients()
  let info = rebalancer.GetInfo() |> Async.AwaitTask |> Async.RunSynchronously
  let task = custodies |> Array.map(fun c -> c.GetInfo()) |> Task.WhenAll
  let custodyInfos = task.GetAwaiter().GetResult()
  let result = RebalancingStrategy.extecute rebalancer info custodyInfos
  ()