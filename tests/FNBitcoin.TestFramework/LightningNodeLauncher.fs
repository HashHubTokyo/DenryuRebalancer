namespace FNBitcoin.TestFramework

open FSharp.Control.Tasks.V2.ContextInsensitive
open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Diagnostics
open System.Threading.Tasks
open System.Runtime.InteropServices
open System.Runtime.CompilerServices
open Microsoft.FSharp.Reflection
open BTCPayServer.Lightning.LND
open BTCPayServer.Lightning
open NBitcoin
open NBitcoin.RPC

type LauncherSettings = {
  NETWORK: string
  BITCOIND_RPCPORT: int
  BITCOIND_PORT: int
  DATADIR: string
  BALANCER_RESTPORT: int
  CUSTODY_RESTPORT: int
  THIRDPARTY_RESTPORT: int
}

type Clients = {
  Bitcoin: NBitcoin.RPC.RPCClient
  Rebalancer: LndClient
  Custody: ILightningClient
  ThirdParty: ILightningClient
}

[<AutoOpen>]
module LightningNodeLauncher =
    // Wrapper for the specific process of docker-compose
    type LightningNodeBuilder(name: string, network: Network, composeFilePath: string) =
        let checkConnection portN =
            let l = new TcpListener(IPAddress.Loopback, portN)
            l.Start()
            l.Stop()

        let findEmptyPort (number: int) =
            let mutable i = 0
            let mutable result = Array.create number 0
            while (i < number) do
                let mutable port = RandomUtils.GetInt32() % 4000
                port <- port + 10000
                if not (result |> Array.contains port) then
                    try
                        checkConnection port
                        result.[i] <- port
                        i <- i + 1
                    with
                    | :? SocketException -> ()          
            result

        let ports = findEmptyPort 5
        let settings = {
            NETWORK = network.ToString().ToLower()
            BITCOIND_PORT = ports.[0]
            BITCOIND_RPCPORT = ports.[1]
            DATADIR = name
            BALANCER_RESTPORT = ports.[2]
            CUSTODY_RESTPORT = ports.[3]
            THIRDPARTY_RESTPORT = ports.[4]
        }

        let mutable maybeRunningProcess: Process option = None

        let convertSettingsToEnvInStartInfo (settings: LauncherSettings): ProcessStartInfo =
            let mutable startInfo = new ProcessStartInfo()
            let allFields = FSharpType.GetRecordFields (settings.GetType())
            for k in allFields do
                startInfo.EnvironmentVariables.["LNLAUNCHER_" + k.Name] <- k.GetValue(settings).ToString()
            startInfo

        let rec checkConnected clients =
            Task.Delay(2000) |> Async.AwaitTask |> Async.RunSynchronously
            try
                Console.WriteLine("checking connection ...")
                let _ = clients.Rebalancer.SwaggerClient.GetInfoAsync() |> Async.AwaitTask |> Async.RunSynchronously
                ()
            with
            | :? SocketException -> checkConnected clients
            | :? AggregateException -> checkConnected clients

        let runDockerComposeDown() =
            let startInfo = convertSettingsToEnvInStartInfo settings
            startInfo.EnvironmentVariables.["COMPOSE_PROJECT_NAME"] <- name
            startInfo.FileName <- "docker-compose"
            startInfo.Arguments <- " -f " + composeFilePath + " down"
            let p = Process.Start(startInfo)
            p.WaitForExit()
            ()

        interface IDisposable with
            member this.Dispose() =
                match maybeRunningProcess with
                | None -> ()
                | Some p ->
                    runDockerComposeDown()
                    p.Dispose()
                    maybeRunningProcess <- None
                    printf "disposed Builder %s " name

        member this.startNode() =
            let startInfo = convertSettingsToEnvInStartInfo settings
            startInfo.EnvironmentVariables.["COMPOSE_PROJECT_NAME"] <- name
            startInfo.FileName <- "docker-compose"
            startInfo.Arguments <- " -f " + composeFilePath + " up"
            // startInfo.ArgumentList.Add("-d")
            startInfo.ErrorDialog <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardOutput <- true
            let p = Process.Start(startInfo)
            maybeRunningProcess <- Some p
            // printf "%s" (p.StandardError.ReadToEnd())
            let c = this.GetClients()
            c |> checkConnected
            // lnd requires at least one block in the chain. (otherwise it will return 500 when tried to connect)
            c.Bitcoin.Generate(1) |> ignore
            // end we must wait until lnd can scan that block.
            Async.Sleep 1000 |> Async.RunSynchronously 
            ()


        member this.GetClients(): Clients =
            let fac = new LightningClientFactory(network)

            let con1 = sprintf "type=lnd-rest;server=https://lnd:lnd@127.0.0.1:%d;allowinsecure=true" settings.BALANCER_RESTPORT
            Console.WriteLine(con1)
            {
                Bitcoin = new RPCClient(RPCCredentialString.Parse("0I5rfLbJEXsg:yJt7h7D8JpQy"),
                                        new Uri(sprintf "http://localhost:%d" settings.BITCOIND_RPCPORT),
                                        network)
                Rebalancer = (fac.Create(con1) :?> LndClient)
                Custody = fac.Create(sprintf "type=lnd-rest;server=https://lnd:lnd@127.0.0.1:%d;allowinsecure=true" settings.CUSTODY_RESTPORT)
                ThirdParty = fac.Create(sprintf "type=lnd-rest;server=https://lnd:lnd@127.0.0.1:%d;allowinsecure=true" settings.THIRDPARTY_RESTPORT)
            }

        member this.ConnectAsync(from: ILightningClient, dest: ILightningClient) =
            task {
                let! info = dest.GetInfo()
                let! _ = from.ConnectTo(info.NodeInfo)
                return ()
            }

         member this.Connect(from: ILightningClient, dest: ILightningClient) =
             this.ConnectAsync(from, dest).GetAwaiter().GetResult()

         member this.ConnectAllAsync() =
             let clients = this.GetClients()
             [|
                 this.ConnectAsync(clients.Rebalancer, clients.ThirdParty)
                 this.ConnectAsync(clients.Rebalancer, clients.Custody)
                 this.ConnectAsync(clients.Custody, clients.ThirdParty)
             |] |> Task.WhenAll

         member this.ConnectAll() =
             this.ConnectAllAsync().GetAwaiter().GetResult()


        member this.OpenChannelAsync(from: ILightningClient, dest: ILightningClient, amount: Money) =
            task {
                let! info = dest.GetInfo()
                let request = new OpenChannelRequest()
                request.NodeInfo <- info.NodeInfo
                request.ChannelAmount <- amount
                request.FeeRate <- new NBitcoin.FeeRate(0.0004m)
                let! _ = from.OpenChannel(request)
                return ()
            }

        member this.OpenChannel(from: ILightningClient, dest: ILightningClient, amount: Money) =
            this.OpenChannelAsync(from, dest, amount).GetAwaiter().GetResult()

        member private this.PrepareFundsAsyncPrivate(amount: Money, confirmation: int option, onlyThisClient: ILightningClient option) =
            let clients = this.GetClients()
            let conf = defaultArg confirmation 3
            task {
                let! _ = clients.Bitcoin.GenerateAsync(101)
                match onlyThisClient with
                | Some c ->
                    let! addr = c.GetDepositAddress()
                    let! _ = clients.Bitcoin.SendToAddressAsync(addr, amount)
                    return! clients.Bitcoin.GenerateAsync(conf)
                | None ->
                    let! addr1 = clients.Custody.GetDepositAddress()
                    let! tmp = clients.Rebalancer.SwaggerClient.NewWitnessAddressAsync()
                    let addr2 = NBitcoin.BitcoinAddress.Create(tmp.Address, network)
                    let! addr3 = clients.ThirdParty.GetDepositAddress()
                    let! _ = [addr1; addr2; addr3] |> List.map(fun a -> clients.Bitcoin.SendToAddressAsync(a, amount)) |> Task.WhenAll
                    return! clients.Bitcoin.GenerateAsync(conf)
            }

        member this.PrepareFundsAsync(amount: Money, [<Optional>] ?confirmation: int, [<Optional>] ?onlyThisClient: ILightningClient) =
            this.PrepareFundsAsyncPrivate(amount, confirmation, onlyThisClient)

        member this.PrepareFunds(amount: Money, [<Optional>] ?confirmation: int, [<Optional>] ?onlyThisClient: ILightningClient) =
            this.PrepareFundsAsyncPrivate(amount, confirmation, onlyThisClient)


    type LightningNodeLauncher() =
      let composeFilePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../../../FNBitcoin.TestFramework/docker-compose.yml"))
      member this.createBuilder ([<CallerMemberName>] ?caller: string, [<Optional>] ?network: Network) =
         if not (File.Exists(composeFilePath)) then failwith "Could not compose file"
         let name = match caller with
                    | None -> failwith "caller member name not spplyed!"
                    | Some i -> Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), i))
         let net = match network with
                   | Some n -> n
                   | None -> Network.RegTest
         if not (Directory.Exists(name)) then
            Directory.CreateDirectory(name) |> ignore
         else 
            Directory.Delete(name, true)
            Directory.CreateDirectory(name) |> ignore
         new LightningNodeBuilder(name, net, composeFilePath)

    let lnLauncher = new LightningNodeLauncher()