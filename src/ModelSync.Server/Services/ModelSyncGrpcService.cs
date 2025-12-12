using Grpc.Core;
using ModelSync.Core;
using ProtoOperationType = ModelSync.Server.OperationType;
using ProtoValueKind = ModelSync.Server.ValueKind;

namespace ModelSync.Server.Services;

public class ModelSyncGrpcService : ModelSyncApi.ModelSyncApiBase
{
    private readonly ModelManager _manager;
    private readonly OperationHub _hub;

    public ModelSyncGrpcService(ModelManager manager, OperationHub hub)
    {
        _manager = manager;
        _hub = hub;
    }

    public override Task<CheckoutResponse> Checkout(CheckoutRequest request, ServerCallContext context)
    {
        _ = _manager.Checkout(request.ModelName);
        var operations = _manager.Tree.GetAllOperations(request.ModelName)
            .Select(ToMessage);

        var response = new CheckoutResponse();
        response.Operations.AddRange(operations);
        return Task.FromResult(response);
    }

    public override Task<OperationAck> ApplyOperation(ApplyOperationRequest request, ServerCallContext context)
    {
        var op = FromMessage(request.Operation, request.ModelName);
        _manager.ApplyOperation(request.ModelName, op);
        _hub.Publish(request.ModelName, op);
        return Task.FromResult(new OperationAck { OperationId = op.Id.ToString() });
    }

    public override async Task SubscribeOperations(SubscribeRequest request, IServerStreamWriter<OperationEvent> responseStream, ServerCallContext context)
    {
        _ = _manager.Checkout(request.ModelName);
        var existing = _manager.Tree.GetAllOperations(request.ModelName);
        foreach (var op in existing)
        {
            await responseStream.WriteAsync(new OperationEvent { Operation = ToMessage(op) });
        }

        await foreach (var op in _hub.Subscribe(request.ModelName, context.CancellationToken))
        {
            await responseStream.WriteAsync(new OperationEvent { Operation = ToMessage(op) });
        }
    }

    private static OperationMessage ToMessage(Operation op)
    {
        return new OperationMessage
        {
            Id = op.Id.ToString(),
            ModelName = op.ModelName,
            ElementId = op.ElementId,
            ElementType = op.ElementType ?? string.Empty,
            PropertyName = op.PropertyName ?? string.Empty,
            AfterItemId = op.AfterItemId ?? string.Empty,
            ItemId = op.ItemId ?? string.Empty,
            MapKey = op.MapKey ?? string.Empty,
            Value = op.NewValue?.Content ?? string.Empty,
            ValueKind = MapValueKind(op.NewValue?.Kind ?? Core.ValueKind.String),
            Type = MapOperationType(op.Type),
            TimestampUnixMs = op.Timestamp.ToUnixTimeMilliseconds()
        };
    }

    private static Operation FromMessage(OperationMessage message, string modelName)
    {
        return new Operation(Guid.TryParse(message.Id, out var parsed) ? parsed : Guid.NewGuid(), MapOperationType(message.Type))
        {
            ModelName = modelName,
            ElementId = message.ElementId,
            ElementType = string.IsNullOrWhiteSpace(message.ElementType) ? null : message.ElementType,
            PropertyName = string.IsNullOrWhiteSpace(message.PropertyName) ? null : message.PropertyName,
            NewValue = new PropertyValue(MapValueKind(message.ValueKind), string.IsNullOrEmpty(message.Value) ? null : message.Value),
            AfterItemId = string.IsNullOrWhiteSpace(message.AfterItemId) ? null : message.AfterItemId,
            ItemId = string.IsNullOrWhiteSpace(message.ItemId) ? null : message.ItemId,
            MapKey = string.IsNullOrWhiteSpace(message.MapKey) ? null : message.MapKey,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : message.TimestampUnixMs)
        };
    }

    private static ProtoOperationType MapOperationType(OperationType type) => type switch
    {
        OperationType.CreateElement => ProtoOperationType.OperationTypeCreateElement,
        OperationType.DeleteElement => ProtoOperationType.OperationTypeDeleteElement,
        OperationType.SetProperty => ProtoOperationType.OperationTypeSetProperty,
        OperationType.AddToListProperty => ProtoOperationType.OperationTypeAddToListProperty,
        OperationType.RemoveFromListProperty => ProtoOperationType.OperationTypeRemoveFromListProperty,
        OperationType.AddToSetProperty => ProtoOperationType.OperationTypeAddToSetProperty,
        OperationType.RemoveFromSetProperty => ProtoOperationType.OperationTypeRemoveFromSetProperty,
        OperationType.UpdateMapEntry => ProtoOperationType.OperationTypeUpdateMapEntry,
        OperationType.RemoveMapEntry => ProtoOperationType.OperationTypeRemoveMapEntry,
        _ => ProtoOperationType.OperationTypeNone
    };

    private static OperationType MapOperationType(ProtoOperationType type) => type switch
    {
        ProtoOperationType.OperationTypeCreateElement => OperationType.CreateElement,
        ProtoOperationType.OperationTypeDeleteElement => OperationType.DeleteElement,
        ProtoOperationType.OperationTypeSetProperty => OperationType.SetProperty,
        ProtoOperationType.OperationTypeAddToListProperty => OperationType.AddToListProperty,
        ProtoOperationType.OperationTypeRemoveFromListProperty => OperationType.RemoveFromListProperty,
        ProtoOperationType.OperationTypeAddToSetProperty => OperationType.AddToSetProperty,
        ProtoOperationType.OperationTypeRemoveFromSetProperty => OperationType.RemoveFromSetProperty,
        ProtoOperationType.OperationTypeUpdateMapEntry => OperationType.UpdateMapEntry,
        ProtoOperationType.OperationTypeRemoveMapEntry => OperationType.RemoveMapEntry,
        _ => OperationType.None
    };

    private static ProtoValueKind MapValueKind(ValueKind kind) => kind switch
    {
        ValueKind.Integer => ProtoValueKind.ValueKindInteger,
        ValueKind.Double => ProtoValueKind.ValueKindDouble,
        ValueKind.Boolean => ProtoValueKind.ValueKindBoolean,
        ValueKind.Reference => ProtoValueKind.ValueKindReference,
        ValueKind.Json => ProtoValueKind.ValueKindJson,
        _ => ProtoValueKind.ValueKindString
    };

    private static ValueKind MapValueKind(ProtoValueKind kind) => kind switch
    {
        ProtoValueKind.ValueKindInteger => ValueKind.Integer,
        ProtoValueKind.ValueKindDouble => ValueKind.Double,
        ProtoValueKind.ValueKindBoolean => ValueKind.Boolean,
        ProtoValueKind.ValueKindReference => ValueKind.Reference,
        ProtoValueKind.ValueKindJson => ValueKind.Json,
        _ => ValueKind.String
    };
}
