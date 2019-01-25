module DenryuRebalancer.HostedServices
open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks.V2.ContextInsensitive
open Microsoft.Extensions.Logging
open BTCPayServer.Lightning
open Microsoft.Extensions.Configuration
open DenryuRebalancer.LndLogRepository
open BTCPayServer.Lightning
open BTCPayServer.Lightning
open DenryuRebalancer.LightningClient
open DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning.LND
open BTCPayServer.Lightning.LND
open BTCPayServer.Lightning

type LNDWatcher(logger: ILogger<LNDWatcher>, conf: IConfiguration, logRepo: ILndLogRepository) =
  let getCustodyClients (fac: ILightningClientFactory) (conf: IConfigurationSection): ILightningClient array =
    let sections = conf.AsEnumerable()
    sections |> Seq.toArray |> Array.filter(fun s -> s.Key = "ConnectionString") |>  Array.map(fun s -> fac.Create(s.Value))

  let postRebalanceExecution = function
    | Ok i -> sprintf "Rebalance performed in %s with amount %d satoshi" i.NodeName i.Amount |> logger.LogInformation
    | Error e -> logger.LogError e

  // not using task since it does not support tail call optimization
  let rec loop (client: LndClient) (custodyClients: ILightningClient seq) (token: CancellationToken) =  async {
      if token.IsCancellationRequested then return ()
      logger.LogInformation "Performing loop ..."
      do! Task.Delay(3000, token) |> Async.AwaitTask
      let! info = (client :> ILightningClient).GetInfo token |> Async.AwaitTask
      let! custodyInfos = custodyClients |> Seq.map(fun c -> c.GetInfo()) |> Seq.toArray |> Task.WhenAll |> Async.AwaitTask
      logRepo.setInfo info |> ignore
      let! rebalanceResult =  custodyClients |> Seq.toArray |> Array.map(fun c -> extecuteRebalance client c) |> Task.WhenAll |> Async.AwaitTask
      rebalanceResult |> Array.map(fun r -> postRebalanceExecution r) |> ignore
      return! loop client custodyClients token
    }

  interface IHostedService with

    member __.StartAsync token =
      task {
        logger.LogInformation "task started by logger" |> ignore
        let network = NBitcoin.Network.GetNetwork(conf.GetValue<string>("network", "testnet"))
        let lnClientFactory = new LightningClientFactory(network)

        // Setup client for rebalancer LND
        let lndconf = conf.GetSection("RebalanerLND")
        let connectionString = lndconf.GetValue<string>("ConnectionString", "type=lnd-rest;server=https://127.0.0.1:32736;allowinsecure=true")
        logger.LogInformation(sprintf "Connecting to LND by %s" connectionString)
        let client = lnClientFactory.Create(connectionString)
        let listener = client.Listen token

        // clients for custody
        let custodyConf = conf.GetSection("Custodies")
        let custodyClients = getCustodyClients lnClientFactory custodyConf

        // launch loop and return
        do loop (client :?> LndClient) custodyClients token |> Async.StartAsTask |> ignore
        return Task.CompletedTask
      } :> Task

    member __.StopAsync token =
      task {
        token.ThrowIfCancellationRequested()
      } :> Task
