module DenryuRebalancer.LndLogRepository

open Microsoft.Extensions.Configuration
open Confluent.Kafka
open BTCPayServer.Lightning
open Microsoft.Extensions.Logging
open DenryuRebalancer.AppDbContext
open DenryuRebalancer.Serializers
open FSharp.Control.Tasks.V2.ContextInsensitive
open DenryuRebalancer
open System.Threading.Tasks
open Confluent.Kafka
open BTCPayServer.Lightning
open System.Collections.Generic
open BTCPayServer.Lightning
open System.Diagnostics
open StackExchange.Redis
open System.Collections.Generic
open StackExchange.Redis
open BTCPayServer.Lightning.LND

type ILndLogRepository =
  abstract member setInfo : LightningNodeInformation -> Task
  abstract member setBalance : LnrpcWalletBalanceResponse -> Task

type RedisKeyType =
  | LndLog
  | Else

let toString = function
  | LndLog -> "lndlog"
  | Else -> "else"

type RedisLndLogRepository (redis: IConnectionMultiplexer, logger: ILogger<RedisLndLogRepository>) =
  let db = redis.GetDatabase()
  interface ILndLogRepository with
    member this.setInfo info =
      logger.LogDebug(info.ToString())
      task {
        let kvPair = new KeyValuePair<RedisKey, RedisValue>(RedisKey.op_Implicit(toString LndLog), RedisValue.op_Implicit(info.ToString())) |> Array.singleton
        let success = db.StringSet(kvPair)
        if not success then
          failwith "failed to set lndlog to Repository"
        return Task.FromResult "TODO: broadcast actual object to redis"
      } :> Task
    member this.setBalance balance =
      logger.LogDebug(balance.ToString())
      task {
        let kvPair = new KeyValuePair<RedisKey, RedisValue>(RedisKey.op_Implicit(toString LndLog), RedisValue.op_Implicit(balance.ToString())) |> Array.singleton
        let success = db.StringSet(kvPair)
        if not success then
          failwith "failed to set lndlog to Repository"
        return Task.FromResult "TODO: broadcast actual object to redis"
      } :> Task
