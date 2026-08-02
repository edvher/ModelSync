using ModelSync.Core;

namespace ModelSync.Server.Services;

/// <summary>
/// Seeds a small collaboration scenario so a fresh server shows a meaningful
/// operation tree and a brewing conflict on the dashboard. Runs only when
/// explicitly requested (--seed-demo / MODELSYNC_SEED_DEMO=1) and only against
/// an empty history, and uses the regular workspace API exclusively, so the
/// seeded state is indistinguishable from real client activity.
/// </summary>
public static class DemoDataSeeder
{
    public static void Seed(ModelService service)
    {
        if (service.History(ModelService.PublicWorkspaceId).Count > 0)
        {
            return;
        }

        // Public history: a small component model, committed via workspace "Setup".
        service.Checkout("Setup");
        service.Apply("Setup", Op(OperationType.CreateElement, "component-a", type: "Component"));
        service.Apply("Setup", Set("component-a", "name", "Component A"));
        service.Apply("Setup", Set("component-a", "status", "Initialized"));
        var inPort = InsertItem("component-a", "ports", "input", after: null);
        service.Apply("Setup", inPort);
        service.Apply("Setup", InsertItem("component-a", "ports", "output", after: inPort.ItemId));
        service.Apply("Setup", MapPut("component-a", "metadata", "author", "demo"));
        service.Commit("Setup");

        // Feature-X diverges after the public head and edits the status...
        service.Checkout("Feature-X");
        service.Apply("Feature-X", Set("component-a", "status", "Feature X draft"));
        service.Apply("Feature-X", AddSet("component-a", "tags", "Telemetry"));
        service.Apply("Feature-X", Set("component-a", "status", "Feature X ready"));

        // ...while Exploration builds a second component.
        service.Checkout("Exploration");
        service.Apply("Exploration", Op(OperationType.CreateElement, "component-b", type: "Component"));
        service.Apply("Exploration", Set("component-b", "name", "Component B"));
        service.Apply("Exploration", Set("component-b", "status", "Prototype"));
        service.Apply("Exploration", AddSet("component-b", "tags", "Discovery"));

        // A concurrent public change that conflicts with Feature-X's status edit,
        // so the dashboard's awareness table has something real to show.
        service.Apply("Setup", Set("component-a", "status", "Connected"));
        service.Commit("Setup");
    }

    private static Operation Op(OperationType operationType, string elementId, string? type = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = operationType,
        ElementId = elementId,
        ElementTypeId = type
    };

    private static Operation Set(string elementId, string property, string value) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.SetProperty,
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    private static Operation AddSet(string elementId, string property, string value) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.AddSetItem,
        ElementId = elementId,
        PropertyName = property,
        Value = PropertyValue.String(value)
    };

    private static Operation MapPut(string elementId, string property, string key, string value) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.PutMapEntry,
        ElementId = elementId,
        PropertyName = property,
        MapKey = key,
        Value = PropertyValue.String(value)
    };

    private static Operation InsertItem(string elementId, string property, string value, string? after) => new()
    {
        Id = Guid.NewGuid(),
        Type = OperationType.InsertListItem,
        ElementId = elementId,
        PropertyName = property,
        ItemId = Guid.NewGuid().ToString("N"),
        AfterItemId = after,
        Value = PropertyValue.String(value)
    };
}
