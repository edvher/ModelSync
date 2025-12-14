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
            ValueKind = MapToProtoValueKind(op.NewValue?.Kind ?? Core.ValueKind.String),
            Type = MapToProtoOperationType(op.Type),
            TimestampUnixMs = op.Timestamp.ToUnixTimeMilliseconds()
        };
    }

    private static Operation FromMessage(OperationMessage message, string modelName)
    {
        return new Operation(Guid.TryParse(message.Id, out var parsed) ? parsed : Guid.NewGuid(), MapToOperationType(message.Type))
        {
            ModelName = modelName,
            ElementId = message.ElementId,
            ElementType = string.IsNullOrWhiteSpace(message.ElementType) ? null : message.ElementType,
            PropertyName = string.IsNullOrWhiteSpace(message.PropertyName) ? null : message.PropertyName,
            NewValue = new PropertyValue(MapToValueKind(message.ValueKind), string.IsNullOrEmpty(message.Value) ? null : message.Value),
            AfterItemId = string.IsNullOrWhiteSpace(message.AfterItemId) ? null : message.AfterItemId,
            ItemId = string.IsNullOrWhiteSpace(message.ItemId) ? null : message.ItemId,
            MapKey = string.IsNullOrWhiteSpace(message.MapKey) ? null : message.MapKey,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : message.TimestampUnixMs)
        };
    }

    private static ProtoOperationType MapToProtoOperationType(ModelSync.Core.OperationType type) => type switch
    {
        ModelSync.Core.OperationType.CreateElement => ProtoOperationType.CreateElement,
        ModelSync.Core.OperationType.DeleteElement => ProtoOperationType.DeleteElement,
        ModelSync.Core.OperationType.SetProperty => ProtoOperationType.SetProperty,
        ModelSync.Core.OperationType.AddToListProperty => ProtoOperationType.AddToListProperty,
        ModelSync.Core.OperationType.RemoveFromListProperty => ProtoOperationType.RemoveFromListProperty,
        ModelSync.Core.OperationType.AddToSetProperty => ProtoOperationType.AddToSetProperty,
        ModelSync.Core.OperationType.RemoveFromSetProperty => ProtoOperationType.RemoveFromSetProperty,
        ModelSync.Core.OperationType.UpdateMapEntry => ProtoOperationType.UpdateMapEntry,
        ModelSync.Core.OperationType.RemoveMapEntry => ProtoOperationType.RemoveMapEntry,
        _ => ProtoOperationType.None
    };

    private static ModelSync.Core.OperationType MapToOperationType(ProtoOperationType type) => type switch
    {
        ProtoOperationType.CreateElement => ModelSync.Core.OperationType.CreateElement,
        ProtoOperationType.DeleteElement => ModelSync.Core.OperationType.DeleteElement,
        ProtoOperationType.SetProperty => ModelSync.Core.OperationType.SetProperty,
        ProtoOperationType.AddToListProperty => ModelSync.Core.OperationType.AddToListProperty,
        ProtoOperationType.RemoveFromListProperty => ModelSync.Core.OperationType.RemoveFromListProperty,
        ProtoOperationType.AddToSetProperty => ModelSync.Core.OperationType.AddToSetProperty,
        ProtoOperationType.RemoveFromSetProperty => ModelSync.Core.OperationType.RemoveFromSetProperty,
        ProtoOperationType.UpdateMapEntry => ModelSync.Core.OperationType.UpdateMapEntry,
        ProtoOperationType.RemoveMapEntry => ModelSync.Core.OperationType.RemoveMapEntry,
        _ => ModelSync.Core.OperationType.None
    };

    private static ProtoValueKind MapToProtoValueKind(ModelSync.Core.ValueKind kind) => kind switch
    {
        ModelSync.Core.ValueKind.Integer => ProtoValueKind.Integer,
        ModelSync.Core.ValueKind.Double => ProtoValueKind.Double,
        ModelSync.Core.ValueKind.Boolean => ProtoValueKind.Boolean,
        ModelSync.Core.ValueKind.Reference => ProtoValueKind.Reference,
        ModelSync.Core.ValueKind.Json => ProtoValueKind.Json,
        _ => ProtoValueKind.String
    };

    private static ModelSync.Core.ValueKind MapToValueKind(ProtoValueKind kind) => kind switch
    {
        ProtoValueKind.Integer => ModelSync.Core.ValueKind.Integer,
        ProtoValueKind.Double => ModelSync.Core.ValueKind.Double,
        ProtoValueKind.Boolean => ModelSync.Core.ValueKind.Boolean,
        ProtoValueKind.Reference => ModelSync.Core.ValueKind.Reference,
        ProtoValueKind.Json => ModelSync.Core.ValueKind.Json,
        _ => ModelSync.Core.ValueKind.String
    };
}
