namespace DenryuRebalancer.Notifier

type MailContent = { Subject: string; Content: string}
type INotifier =
  abstract member Notify : MailContent -> Async<unit>