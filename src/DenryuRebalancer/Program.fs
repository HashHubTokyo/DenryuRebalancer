module GiraffeBlog.App

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Giraffe
open FSharp.Control.Tasks.V2.ContextInsensitive
open DenryuRebalancer.Startup


let lndListenerHandler =
    fun (next : HttpFunc) (ctx: HttpContext) ->
        task {
            let hoge = ""
            return! text hoge next ctx
        }

let configureLogging (ctx: WebHostBuilderContext)  (builder : ILoggingBuilder) =
    let conf = ctx.Configuration.GetSection("Logging")
    builder.AddFilter(fun l -> l.Equals LogLevel.Error)
           .AddConsole()
           .AddConfiguration(conf)
           .AddDebug() |> ignore

[<EntryPoint>]
let main _ =
    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot     = Path.Combine(contentRoot, "WebRoot")
    WebHostBuilder()
        .UseKestrel()
        .UseContentRoot(contentRoot)
        .UseIISIntegration()
        .UseWebRoot(webRoot)
        .ConfigureAppConfiguration(configureAppConfiguration)
        .ConfigureLogging(configureLogging)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .Build()
        .Run()
    0
