using ModelSync.Core;
using ModelSync.Protocol;
using CoreConflict = ModelSync.Core.Conflict;
using CoreOperation = ModelSync.Core.Operation;

namespace ModelSync.Server.Services;

/// <summary>Mapping between the wire protocol and the core model.</summary>
public static class ProtoMapper
{
    public static OperationMessage ToMessage(CoreOperation op)
    {
        var message = new OperationMessage
        {
            Id = op.Id.ToString(),
            Type = (Protocol.OperationType)(int)op.Type,
            WorkspaceId = op.WorkspaceId,
            ElementId = op.ElementId,
            ValueKind = op.Value is null ? Protocol.ValueKind.String : (Protocol.ValueKind)(int)op.Value.Kind,
            IsResolution = op.IsResolution,
            TimestampUnixMs = op.Timestamp.ToUnixTimeMilliseconds()
        };

        if (op.ElementTypeId is not null)
        {
            message.ElementTypeId = op.ElementTypeId;
        }

        if (op.PropertyName is not null)
        {
            message.PropertyName = op.PropertyName;
        }

        if (op.Value is not null)
        {
            message.Value = op.Value.Content;
        }

        if (op.ItemId is not null)
        {
            message.ItemId = op.ItemId;
        }

        if (op.AfterItemId is not null)
        {
            message.AfterItemId = op.AfterItemId;
        }

        if (op.MapKey is not null)
        {
            message.MapKey = op.MapKey;
        }

        return message;
    }

    public static CoreOperation ToOperation(OperationMessage message)
    {
        return new CoreOperation
        {
            Id = Guid.TryParse(message.Id, out var id) && id != Guid.Empty ? id : Guid.NewGuid(),
            Type = (Core.OperationType)(int)message.Type,
            WorkspaceId = message.WorkspaceId,
            ElementId = message.ElementId,
            ElementTypeId = message.HasElementTypeId ? message.ElementTypeId : null,
            PropertyName = message.HasPropertyName ? message.PropertyName : null,
            Value = message.HasValue ? new PropertyValue((Core.ValueKind)(int)message.ValueKind, message.Value) : null,
            ItemId = message.HasItemId ? message.ItemId : null,
            AfterItemId = message.HasAfterItemId ? message.AfterItemId : null,
            MapKey = message.HasMapKey ? message.MapKey : null,
            IsResolution = message.IsResolution,
            Timestamp = message.TimestampUnixMs == 0
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampUnixMs)
        };
    }

    public static ConflictMessage ToMessage(CoreConflict conflict)
    {
        var message = new ConflictMessage
        {
            Category = (Protocol.ConflictCategory)(int)conflict.Category,
            MergeType = (Protocol.MergeConflictType)(int)conflict.MergeType,
            Severity = (Protocol.ConflictSeverity)(int)conflict.Severity,
            Policy = (Protocol.ResolutionPolicy)(int)conflict.Policy,
            RequiresResolution = conflict.RequiresResolution,
            ConflictKey = conflict.ConflictKey,
            ParentOperation = ToMessage(conflict.ParentOperation),
            ChildOperation = ToMessage(conflict.ChildOperation)
        };

        if (conflict.Resolution is not null)
        {
            message.Resolution = ToMessage(conflict.Resolution);
        }

        return message;
    }
}
