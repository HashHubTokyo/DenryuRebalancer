namespace LNTestFramework


// Since some methods (namely QueryRoutesAsync) in BTCPayServer.Lightning.LND.SwaggerClient does not work properly and always returns 400 error,
// here is alternative.
[<AutoOpen>]
module LNDSwaggerProvider =
    open System
    open System.Globalization
    open System.Threading
    open System.Threading.Tasks
    open FSharp.Control.Tasks.V2.ContextInsensitive
    open NBitcoin
    open SwaggerProvider
    open BTCPayServer.Lightning

    let [<Literal>] schema = "https://github.com/lightningnetwork/lnd/raw/master/lnrpc/rpc.swagger.json"
    type LNDSwaggerProvider = SwaggerProvider<schema>

    let bitString(hash: byte[]) =
        BitConverter.ToString(hash).Replace("-", "").ToLower(CultureInfo.InvariantCulture)

    let decimalToString (value: decimal) =
        Convert.ToString(value, CultureInfo.InvariantCulture)

    let int64ToString (value: int64) =
        Convert.ToString(value, CultureInfo.InvariantCulture)

    let doubleToString (value: double) =
        Convert.ToString(value, CultureInfo.InvariantCulture)

    let stringToInt64 (value: string) =
        Convert.ToInt64(value, CultureInfo.InvariantCulture)


    let convertLndInvoice (resp: LNDSwaggerProvider.lnrpcInvoice): LightningInvoice =
        let result = new LightningInvoice()
        result.Id <- bitString(resp.RHash)
        result.Amount <- LightMoney.Satoshis(stringToInt64 resp.Value)
        result.BOLT11 <- resp.PaymentRequest
        result.Status <- LightningInvoiceStatus.Unpaid
        let fromNow = stringToInt64 resp.CreationDate - stringToInt64 resp.Expiry
        result.ExpiresAt <- DateTimeOffset.FromUnixTimeSeconds(fromNow)
        if resp.SettleDate <> null then
            result.PaidAt <- DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(resp.CreationDate, CultureInfo.InvariantCulture)) |> Nullable
            result.Status <- LightningInvoiceStatus.Paid
            result
        else if result.ExpiresAt < DateTimeOffset.UtcNow then
            result.Status <- LightningInvoiceStatus.Expired
            result
        else
            result
           

    type LNDSwaggerProvider with

        member this.AsILightningClient =
            {
                new ILightningClient with
                member __.GetInvoice (id: string, token: CancellationToken) = 
                    task {
                        return this.LookupInvoice(id) |> convertLndInvoice
                        }
                member __.CreateInvoice(amount: LightMoney, desc: string, expiry: TimeSpan, token) =
                    task {
                        let amountStr = decimalToString(amount.ToUnit(LightMoneyUnit.Satoshi))
                        let expiryStr = doubleToString(Math.Round(expiry.TotalSeconds, 0))
                        let arg = new LNDSwaggerProvider.lnrpcInvoice()
                        arg.Value <- amountStr
                        arg.Memo <- desc
                        arg.Expiry <- expiryStr
                        let resp = this.AddInvoice(arg)
                        let result = new LightningInvoice()
                        result.Id <- bitString(resp.RHash)
                        result.Amount <- amount
                        result.BOLT11 <- resp.PaymentRequest
                        result.Status <- LightningInvoiceStatus.Unpaid
                        result.ExpiresAt <- DateTimeOffset.UtcNow + expiry
                        return result
                        }
                member __.Listen (token) =
                    task {
                        return { new ILightningInvoiceListener with
                                member ___.Dispose() = ()
                                member ___.WaitInvoice(token) = task { return LightningInvoice() }
                            }
                        }
                member __.GetInfo (token) =
                    task {
                        return new LightningNodeInformation()
                    }
                member __.Pay(bolt11: string, token) =
                    task {
                        return new PayResponse(PayResult.Ok)
                    }
                member __.OpenChannel(request, token) =
                    task {
                        return new OpenChannelResponse(OpenChannelResult.AlreadyExists)
                    }
                member __.GetDepositAddress() =
                    task {
                        let addr = this.NewAddress()
                        let info = this.GetInfo()
                        return BitcoinAddress.Create(addr.Address)
                    }
                 member __.ConnectTo(info: NodeInfo) =
                     task {
                         return ()
                     } :> Task
            } 