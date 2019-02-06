namespace FNBitcoin.TestFramework

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
            Task.Delay(1000) |> Async.AwaitTask |> Async.RunSynchronously
            try
                Console.WriteLine("checking connection ...")
                let _ = clients.Rebalancer.SwaggerClient.GetInfoAsync() |> Async.AwaitTask |> Async.RunSynchronously
                ()
            with
            | :? SocketException -> checkConnected clients
            | :? AggregateException -> checkConnected clients

        interface IDisposable with
            member this.Dispose() =
                match maybeRunningProcess with
                | None -> ()
                | Some p ->
                    p.Kill()
                    p.Dispose()

        member this.startNode() =
            let startInfo = convertSettingsToEnvInStartInfo settings
            startInfo.EnvironmentVariables.["COMPOSE_PROJECT_NAME"] <- name
            startInfo.FileName <- "docker-compose"
            startInfo.ArgumentList.Add("-f")
            startInfo.ArgumentList.Add(composeFilePath)
            startInfo.ArgumentList.Add("up")
            startInfo.ErrorDialog <- true
            startInfo.RedirectStandardError <- true
            startInfo.RedirectStandardOutput <- false
            let p = Process.Start(startInfo)
            maybeRunningProcess <- Some p
            // printf "%s" (p.StandardError.ReadToEnd())
            this.GetClients() |> checkConnected

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

        member this.Connect(from: ILightningClient, dest: ILightningClient, [<Optional>] ?bothSide: bool) =
            async {
                let! info = dest.GetInfo() |> Async.AwaitTask
                let! _ = from.ConnectTo(info.NodeInfo) |> Async.AwaitTask
                match bothSide with
                | None -> ()
                | Some b -> if b then return! this.Connect(dest, from, false) else ()
            }

        member this.OpenChannel(from: ILightningClient, dest: ILightningClient, amount: Money) =
            async {
                let! info = dest.GetInfo() |> Async.AwaitTask
                let request = new OpenChannelRequest()
                request.NodeInfo <- info.NodeInfo
                request.ChannelAmount <- amount
                request.FeeRate <- new NBitcoin.FeeRate(0.0004m)
                let! _ = from.OpenChannel(request) |> Async.AwaitTask
                return ()
            }

        member this.PrepareFunds(amount: Money, [<Optional>] ?confirmation: int, [<Optional>] ?onlyThisClient: ILightningClient) =
            let clients = this.GetClients()
            let conf = defaultArg confirmation 5
            async {
                let! _ = clients.Bitcoin.GenerateAsync(101) |> Async.AwaitTask
                match onlyThisClient with
                | Some c ->
                    let! addr = c.GetDepositAddress() |> Async.AwaitTask
                    let! _ = clients.Bitcoin.SendToAddressAsync(addr, amount) |> Async.AwaitTask
                    return! clients.Bitcoin.GenerateAsync(conf) |> Async.AwaitTask
                | None ->
                    let! addr1 = clients.Custody.GetDepositAddress() |> Async.AwaitTask
                    let! tmp = clients.Rebalancer.SwaggerClient.NewWitnessAddressAsync() |> Async.AwaitTask
                    let addr2 = NBitcoin.BitcoinAddress.Create(tmp.Address, network)
                    let! addr3 = clients.ThirdParty.GetDepositAddress() |> Async.AwaitTask
                    let! _ = [addr1; addr2; addr3] |> List.map(fun a -> clients.Bitcoin.SendToAddressAsync(a, amount)) |> Task.WhenAll |> Async.AwaitTask
                    return! clients.Bitcoin.GenerateAsync(conf) |> Async.AwaitTask
            }

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
         new LightningNodeBuilder(name, net, composeFilePath)

    let lnLauncher = new LightningNodeLauncher()