# ModelSync
ModelSync is a lightweight experimental platform for collaborative model editing. It provides basic metamodeling, model instantiation, operation tracking, incremental conflict detection, and simple merge support for evaluating synchronization scenarios in model-driven engineering.

## Projects

- **ModelSync.Core** – in-memory model store, operation tree, CRUD helpers, and conflict detection logic.
- **ModelSync.Server** – standalone gRPC server that exposes checkout/update/commit primitives and streams operations to clients.
- **ModelSync.Sdk** – small client wrapper that connects to the gRPC server, replays history, and listens for new operations.

## Running the server

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/) (preview at the time of writing).
2. Start the gRPC host:

   ```bash
   dotnet run --project src/ModelSync.Server/ModelSync.Server.csproj
   ```

The server listens on the default ASP.NET Core gRPC port (`https://localhost:5001`) and exposes the following RPCs defined in `src/ModelSync.Server/Protos/modelsync.proto`:

- `Checkout(model_name)` → returns the complete operation history for the branch, allowing the SDK to reconstruct the model state.
- `ApplyOperation(model_name, operation)` → appends a new operation to the tree and broadcasts it to listeners.
- `SubscribeOperations(model_name)` → server-side stream that first replays existing operations and then pushes new ones in real time.

## SDK usage

```csharp
await using var client = new ModelSync.Sdk.ModelSyncClient("https://localhost:5001");
var history = await client.CheckoutAsync("A"); // reconstruct branch A

// apply a change
var opId = await client.ApplyAsync("A", new OperationMessage {
    Type = OperationType.OperationTypeSetProperty,
    ElementId = "42",
    PropertyName = "name",
    Value = "heartbeat",
    ValueKind = ValueKind.ValueKindString
});

// stream new operations
await foreach (var op in client.SubscribeAsync("A", cancellationToken))
{
    // replay op locally
}
```

The server maintains a shared public branch (`P`) and any number of private branches. `Checkout` seeds a branch from the current public head, `Update` (server-side) replays public changes onto the private model while resolving conflicts, and `Commit` fast-forwards the public head when the private branch is up to date.
