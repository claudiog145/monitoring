using Kusto.Language.Symbols;

namespace FakeAppInsights.Rewrite;

/// <summary>A column of one of the App Insights tables, typed with the Kusto scalar type name.</summary>
public sealed record SchemaColumn(string Name, string Type);

/// <summary>
/// The six Application Insights tables the demo serves (contract v5 §4.1). <c>Portal/demo/kusto/schema.kql</c>
/// creates exactly these columns; a test keeps the two in step.
/// </summary>
public static class AppInsightsSchema
{
    public static readonly IReadOnlyList<string> TableNames =
        ["requests", "exceptions", "dependencies", "traces", "pageViews", "customEvents"];

    private static readonly string[] ClientColumns =
    [
        "client_Type", "client_Model", "client_OS", "client_IP", "client_City",
        "client_StateOrProvince", "client_CountryOrRegion", "client_Browser",
    ];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<SchemaColumn>> Tables =
        new Dictionary<string, IReadOnlyList<SchemaColumn>>(StringComparer.Ordinal)
        {
            ["requests"] = Build(
                "timestamp:datetime", "id", "name", "url", "resultCode", "duration:real", "success:bool",
                "performanceBucket", "itemType", "customDimensions:dynamic", "customMeasurements:dynamic",
                "operation_Name", "operation_Id", "operation_ParentId", "session_Id", "user_Id", "user_AuthenticatedId",
                "*client", "cloud_RoleName", "cloud_RoleInstance", "appId", "appName", "iKey", "sdkVersion", "itemId",
                "itemCount:int", "source"),
            ["exceptions"] = Build(
                "timestamp:datetime", "problemId", "handledAt", "type", "message", "assembly", "method", "outerType",
                "outerMessage", "outerAssembly", "outerMethod", "innermostType", "innermostMessage", "innermostAssembly",
                "innermostMethod", "severityLevel:int", "details:dynamic", "customDimensions:dynamic",
                "customMeasurements:dynamic", "operation_Name", "operation_Id", "operation_ParentId", "session_Id",
                "user_Id", "*client", "cloud_RoleName", "cloud_RoleInstance", "appId", "appName", "itemId", "itemCount:int"),
            ["dependencies"] = Build(
                "timestamp:datetime", "id", "target", "type", "name", "data", "success:bool", "resultCode",
                "duration:real", "performanceBucket", "customDimensions:dynamic", "customMeasurements:dynamic",
                "operation_Name", "operation_Id", "operation_ParentId", "session_Id", "user_Id", "*client",
                "cloud_RoleName", "cloud_RoleInstance", "appId", "appName", "itemId", "itemCount:int"),
            ["traces"] = Build(
                "timestamp:datetime", "message", "severityLevel:int", "customDimensions:dynamic",
                "customMeasurements:dynamic", "operation_Name", "operation_Id", "operation_ParentId", "session_Id",
                "user_Id", "*client", "cloud_RoleName", "cloud_RoleInstance", "appId", "appName", "itemId", "itemCount:int"),
            ["pageViews"] = Build(
                "timestamp:datetime", "id", "name", "url", "duration:real", "performanceBucket",
                "customDimensions:dynamic", "customMeasurements:dynamic", "operation_Name", "operation_Id",
                "session_Id", "user_Id", "*client", "cloud_RoleName", "appId", "appName", "itemId", "itemCount:int"),
            ["customEvents"] = Build(
                "timestamp:datetime", "name", "customDimensions:dynamic", "customMeasurements:dynamic",
                "operation_Name", "operation_Id", "session_Id", "user_Id", "*client", "cloud_RoleName", "appId",
                "appName", "itemId", "itemCount:int"),
        };

    public static bool IsTable(string name) => Tables.ContainsKey(name);

    /// <summary>The six tables as Kusto.Language symbols, for semantic analysis of a query.</summary>
    public static IReadOnlyList<TableSymbol> ToTableSymbols() =>
        TableNames.Select(t => new TableSymbol(t, Tables[t].Select(c => new ColumnSymbol(c.Name, ScalarTypes.GetSymbol(c.Type))), null)).ToList();

    private static IReadOnlyList<SchemaColumn> Build(params string[] specs)
    {
        var columns = new List<SchemaColumn>();
        foreach (var spec in specs)
        {
            if (spec == "*client")
            {
                columns.AddRange(ClientColumns.Select(c => new SchemaColumn(c, "string")));
                continue;
            }
            var parts = spec.Split(':');
            columns.Add(new SchemaColumn(parts[0], parts.Length > 1 ? parts[1] : "string"));
        }
        return columns;
    }
}
