module DenryuRebalancer.Startup

open System
open System.IO
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.EntityFrameworkCore
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open DenryuRebalancer.HostedServices
open DenryuRebalancer.AppDbContext
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open DenryuRebalancer.LndLogRepository
open StackExchange.Redis
open StackExchange.Redis
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration

// ---------------------------------
// Models
// ---------------------------------

type Message =
    {
        Text : string
    }

// ---------------------------------
// Views
// ---------------------------------

module Views =
    open GiraffeViewEngine

    let layout (content: XmlNode list) =
        html [] [
            head [] [
                title []  [ encodedText "GiraffeBlog" ]
                link [ _rel  "stylesheet"
                       _type "text/css"
                       _href "/main.css" ]
            ]
            body [] content
        ]

    let partial () =
        h1 [] [ encodedText "GiraffeBlog" ]

    let index (model : Message) =
        [
            partial()
            p [] [ encodedText model.Text ]
        ] |> layout

// ---------------------------------
// Web app
// ---------------------------------

let indexHandler (name : string) =
    let greetings = sprintf "Hello %s, from Giraffe!" name
    let model     = { Text = greetings }
    let view      = Views.index model
    htmlView view

let webApp =
    choose [
        GET >=>
            choose [
                route "/" >=> indexHandler "world"
                routef "/hello/%s" indexHandler
            ]
        setStatusCode 404 >=> text "Not Found" ]
let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

let configureCors (builder : CorsPolicyBuilder) =
    builder.WithOrigins("http://localhost:8080")
           .AllowAnyMethod()
           .AllowAnyHeader()
           |> ignore

let configureApp (app : IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IHostingEnvironment>()
    (match env.IsDevelopment() with
    | true  -> app.UseDeveloperExceptionPage()
    | false -> app.UseGiraffeErrorHandler errorHandler)
        .UseHttpsRedirection()
        .UseCors(configureCors)
        .UseStaticFiles()
        .UseGiraffe(webApp)

let configureAppConfiguration (ctx: WebHostBuilderContext) (conf: IConfigurationBuilder) =
    conf
        .AddJsonFile("appsettings.json", false, true)
        .AddJsonFile(sprintf "appsettings.%s.json" ctx.HostingEnvironment.EnvironmentName, true)
        .AddEnvironmentVariables() |> ignore

let getRedisConnectionMultiplexer (conf: IConfiguration) =
    let redisConf = conf.GetSection("redis")
    let connString = redisConf.GetValue<string>("ConnectionString", "localhost")
    ConnectionMultiplexer.Connect(connString)

let configureServices (ctx: WebHostBuilderContext) (services : IServiceCollection) =
    services.AddDbContext<AppDbContext>(fun p o ->
            let conf = p.GetRequiredService<IConfiguration>()
            let connString = conf.GetSection("db").GetValue<string>("ConnectionString")
            o.UseSqlite connString |> ignore
        ) |> ignore
    services.AddCors()    |> ignore
    services.AddGiraffe() |> ignore
    let con = getRedisConnectionMultiplexer ctx.Configuration
    services.AddSingleton<IConnectionMultiplexer>(con) |> ignore
    services.AddSingleton<ILndLogRepository, LndLogRepository>() |> ignore
    services.AddSingleton<IHostedService, LNDWatcher>() |> ignore