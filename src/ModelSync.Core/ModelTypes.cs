namespace ModelSync.Core;

public enum PropertyKind
{
    Primitive,
    Reference
}

public enum CollectionKind
{
    Scalar,
    List,
    Set,
    Map
}

public enum OperationType
{
    None,
    CreateElement,
    DeleteElement,
    SetProperty,
    AddToListProperty,
    RemoveFromListProperty,
    AddToSetProperty,
    RemoveFromSetProperty,
    UpdateMapEntry,
    RemoveMapEntry
}

public enum ConflictType
{
    None,
    PropertyWrite,
    ElementDelete
}

public enum ValueKind
{
    String,
    Integer,
    Double,
    Boolean,
    Reference,
    Json
}
