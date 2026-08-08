using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

[Flags]
public enum SessionListRequestFields
{
    None = 0,
    Limit = 1 << 0,
    Offset = 1 << 1,
    Search = 1 << 2,
    ConfiguredAgentsOnly = 1 << 3,
    Archived = 1 << 4,
}

/// <summary>
/// Capability-negotiated <c>sessions.list</c> request contract.
/// Keep this DTO aligned with the pinned protocol snapshot and released Core schemas.
/// </summary>
public sealed class SessionListRequest
{
    [JsonPropertyName("agentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentId { get; init; }

    [JsonPropertyName("limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; init; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Offset { get; init; }

    [JsonPropertyName("search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Search { get; init; }

    [JsonPropertyName("configuredAgentsOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConfiguredAgentsOnly { get; init; }

    [JsonPropertyName("archived")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Archived { get; init; }

    [JsonIgnore]
    internal SessionListRequestFields RequestedOptionalFields
    {
        get
        {
            var fields = SessionListRequestFields.None;
            if (Limit.HasValue) fields |= SessionListRequestFields.Limit;
            if (Offset.HasValue) fields |= SessionListRequestFields.Offset;
            if (!string.IsNullOrWhiteSpace(Search)) fields |= SessionListRequestFields.Search;
            if (ConfiguredAgentsOnly.HasValue) fields |= SessionListRequestFields.ConfiguredAgentsOnly;
            if (Archived.HasValue) fields |= SessionListRequestFields.Archived;
            return fields;
        }
    }

    internal Dictionary<string, object?> ToParameters(
        SessionListRequestFields unsupportedFields = SessionListRequestFields.None)
    {
        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(AgentId)) parameters["agentId"] = AgentId;
        if (Limit.HasValue && !unsupportedFields.HasFlag(SessionListRequestFields.Limit))
            parameters["limit"] = Limit.Value;
        if (Offset.HasValue && !unsupportedFields.HasFlag(SessionListRequestFields.Offset))
            parameters["offset"] = Offset.Value;
        if (!string.IsNullOrWhiteSpace(Search) &&
            !unsupportedFields.HasFlag(SessionListRequestFields.Search))
        {
            parameters["search"] = Search;
        }
        if (ConfiguredAgentsOnly.HasValue &&
            !unsupportedFields.HasFlag(SessionListRequestFields.ConfiguredAgentsOnly))
        {
            parameters["configuredAgentsOnly"] = ConfiguredAgentsOnly.Value;
        }
        if (Archived.HasValue && !unsupportedFields.HasFlag(SessionListRequestFields.Archived))
            parameters["archived"] = Archived.Value;
        return parameters;
    }
}

/// <summary>
/// Stable <c>sessions.list</c> response contract.
/// Nullable metadata deliberately tolerates older and additive gateway shapes.
/// </summary>
public sealed class SessionListResult
{
    [JsonPropertyName("sessions")]
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();

    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("totalCount")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("limitApplied")]
    public int? LimitApplied { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    [JsonPropertyName("nextOffset")]
    public int? NextOffset { get; init; }

    [JsonPropertyName("hasMore")]
    public bool? HasMore { get; init; }

    [JsonIgnore]
    public SessionListRequestFields UnsupportedRequestFields { get; init; }

    [JsonIgnore]
    public bool UsedCompatibilityFallback =>
        UnsupportedRequestFields != SessionListRequestFields.None;
}

/// <summary>UI-neutral query accepted by the session discovery boundary.</summary>
public sealed class SessionQuery
{
    public string? AgentId { get; init; }
    public string? Search { get; init; }
    public bool ConfiguredAgentsOnly { get; init; }
    public bool? Archived { get; init; }
    public bool IncludeBackground { get; init; }
    public IReadOnlyList<SessionInfo> PinnedSessions { get; init; } = Array.Empty<SessionInfo>();
}

public enum SessionSearchExecutionMode
{
    None,
    Server,
    LegacyLocal,
}

/// <summary>One coherent, bounded session discovery snapshot.</summary>
public sealed class SessionQuerySnapshot
{
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
    public string? Search { get; init; }
    public int ConnectionGeneration { get; init; }
    public int PagesRead { get; init; }
    public SessionListRequestFields UnsupportedRequestFields { get; init; }
    public SessionSearchExecutionMode SearchExecutionMode { get; init; }

    public bool UsedCompatibilityFallback =>
        UnsupportedRequestFields != SessionListRequestFields.None;

    internal IReadOnlyList<SessionInfo> MaterializedSessions { get; init; } = Array.Empty<SessionInfo>();
    internal long RequestIdentity { get; init; }
}
