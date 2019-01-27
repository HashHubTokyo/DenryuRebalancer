namespace DenryuRebalancer.Notifier
open Microsoft.Extensions.Configuration
open MailKit.Net.Smtp
open MimeKit
open MimeKit.Text


[<CLIMutable>]
type EmailConfigurationRecord = {
    AdminAddress: string
    SmtpServer: string
    SmtpPort: int
    SmtpUsername: string
    SmtpPassword: string
  }

type MailNotifier(conf: IConfiguration) =
  let conf = conf.GetSection("Email").Get<EmailConfigurationRecord>()
  let message = new MimeMessage()
  do message.To.Add(new MailboxAddress(conf.AdminAddress))
  do message.From.Add(new MailboxAddress(conf.AdminAddress))

  let constructMessage info =
    let textpart = new TextPart(TextFormat.Plain)
    textpart.Text <- info.Content
    message.Subject <- info.Subject
    message.Body <- textpart 
    message

  interface INotifier with
    member this.Notify info = async {
      let message = constructMessage info
      use client = new SmtpClient()
      client.Connect(conf.SmtpServer, conf.SmtpPort, true)
      client.Authenticate(conf.SmtpUsername, conf.SmtpPassword)
      client.Send(message)
      client.Disconnect(true)
      return ()
    }