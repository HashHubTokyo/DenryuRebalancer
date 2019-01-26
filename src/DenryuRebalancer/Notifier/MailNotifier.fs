namespace DenryuRebalancer.Notifier
open Microsoft.Extensions.Configuration

// TODO: send mail actually
type MailNotifier(conf: IConfiguration) =
  interface INotifier with
    member this.Notify info = async {
      return false
    }