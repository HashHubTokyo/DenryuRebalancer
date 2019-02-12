module DenryuRebalancer.HostedServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks.V2.ContextInsensitive
open Microsoft.Extensions.Logging
open BTCPayServer.Lightning
open Microsoft.Extensions.Configuration
open DenryuRebalancer.LndLogRepository
open DenryuRebalancer.RebalancingStrategy
open BTCPayServer.Lightning.LND
open DenryuRebalancer.Notifier

type LNDWatcher(logger: ILogger<LNDWatcher>, conf: IConfiguration, logRepo: ILndLogRepository, notifier: INotifier) =
  let getCustodyClients (fac: ILightningClientFactory) (conf: IConfigurationSection): ILightningClient array =
    let sections = conf.AsEnumerable()
    sections |> Seq.toArray |> Array.filter(fun s -> s.Key = "ConnectionString") |>  Array.map(fun s -> fac.Create(s.Value))

  let maybeLog = function
    | Some i -> sprintf "Rebalance performed in %s with amount %.2f satoshi" i.NodeName (i.Amount.ToDecimal(LightMoneyUnit.Satoshi)) |> logger.LogInformation
    | None -> logger.LogInformation "Rebalance not performed since channel has enough amount"

  let postRebalanceExecution = function
    | Ok r -> maybeLog r
    | Error e -> logger.LogError e

  let mutable notified = false

  let notify (info: LnrpcWalletBalanceResponse) =
    notified <- true
    async {
      try
        do! notifier.Notify({ Subject = "Please send more funds to your DenryuRebalancer!"; Content = info.ToJson()})
        logger.LogWarning "Sent email to the admin mail address"
      with
        | ex -> logger.LogError (sprintf "Failed to notify with email! Please check your settings \n %s" ex.Message)
    }
  // not using task since it does not support tail call optimization
  let rec loop (client: LndClient)
               (custodyClients: ILightningClient seq)
               (rebalanceThreshold: LightMoney)
               (notificationThreshold: LightMoney)
               (token: CancellationToken) = 
    async {
      if token.IsCancellationRequested then return ()
      logger.LogInformation "Performing loop ..."
      do! Task.Delay(5000, token) |> Async.AwaitTask

      // propagate information to the repository
      let! info = (client :> ILightningClient).GetInfo token |> Async.AwaitTask
      logRepo.setInfo info |> ignore
      let! balance = client.SwaggerClient.WalletBalanceAsync(token) |> Async.AwaitTask
      logRepo.setBalance balance |> ignore
      if not notified && (snd (LightMoney.TryParse(balance.Total_balance)) < notificationThreshold) then notify balance |> Async.Start

      // check rebalancer 
      let! prepareChannelResults = custodyClients |> Seq.map(fun c -> c.GetInfo()) |> Seq.toArray |> Task.WhenAll |> Async.AwaitTask

      // execute rebalance
      let! rebalanceResult =  custodyClients |> Seq.toArray |> Array.map(fun c -> executeRebalance client c rebalanceThreshold token Default) |> Task.WhenAll |> Async.AwaitTask
      rebalanceResult |> Array.map(fun r -> postRebalanceExecution r) |> ignore
      return! loop client custodyClients notificationThreshold rebalanceThreshold token
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
        let rebalanceThreshold = LightMoney.Satoshis(lndconf.GetValue<decimal>("RebalanceThreshold", 10000m))
        logger.LogInformation(sprintf "Connecting to LND by %s" connectionString)
        let client = lnClientFactory.Create(connectionString)
        let notificationThreshold = LightMoney.Satoshis(lndconf.GetValue<decimal>("WalletBalanceNotificationThreshold", 100000m))
        let listener = client.Listen token

        // clients for custody
        let custodyConf = conf.GetSection("Custodies")
        let custodyClients = getCustodyClients lnClientFactory custodyConf

        // launch loop and return
        do loop (client :?> LndClient) custodyClients rebalanceThreshold notificationThreshold token |> Async.StartAsTask |> ignore
        return Task.CompletedTask
      } :> Task

    member __.StopAsync token =
      task {
        token.ThrowIfCancellationRequested()
      } :> Task
