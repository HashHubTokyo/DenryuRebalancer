# FNBitcoin.TestFramework

utilities for testing bitcoin based DApps and Lapps

## Usage

```fsharp
open FNBitcoin.TestFramework

use builder = lnLauncher.createBuilder()
builder.startNode()
builder.ConnectAll()
let clients = builder.GetClients()

/// ... use clients to control the nodes
```
