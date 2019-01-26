namespace DenryuRebalancer.Notifier

type INotifier =
  abstract member Notify : string -> Async<bool>