using System.Text.Json;

namespace LANFax.Core;

public record Job(
    string Id, string Direction, string State, string? Filename, string? ToNumber, string? FromNumber,
    string? Subject, string? Origin, int? Pages, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? FinishedAt,
    string? Detail = null)
{
    /// <summary>faxd TERMINAL_STATES: sent, failed, received, cancelled.</summary>
    public bool IsTerminal => State is "sent" or "failed" or "received" or "cancelled";
}

/// <summary>One row of a job's <c>job_events</c>: a state transition or progress note.</summary>
public record JobEvent(long Id, DateTimeOffset Ts, string? State, string? Detail);

/// <summary>GET /jobs/{id}: the job plus its event history (the confirmation's transmission log).</summary>
public record JobDetail(Job Job, IReadOnlyList<JobEvent> Events);

/// <summary>POST /analyze: the pre-send check behind the blank-page prompt.</summary>
public record FaxCheck(int? Pages, IReadOnlyList<int> BlankPages);

public record ArmState(bool Armed, DateTimeOffset? Until, int RemainingMin, string Ext);

public record ServerStatus(
    bool AmiConnected,
    IReadOnlyList<(string Name, string Status)> Registrations,
    ArmState Arm,
    IReadOnlyDictionary<string, int> Queue,
    bool Receiving)
{
    public static readonly ServerStatus Empty = new(
        false, Array.Empty<(string, string)>(), new ArmState(false, null, 0, ""),
        new Dictionary<string, int>(), false);

    public static ServerStatus FromJson(JsonElement el) => Empty.Merge(el);

    /// <summary>
    /// Merges a full or partial faxd status object into this one. Only keys present in
    /// <paramref name="el"/> replace values (the AMI "receiving" event is partial).
    /// </summary>
    public ServerStatus Merge(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return this;
        var s = this;
        if (el.TryGetProperty("daemon", out var d) && d.ValueKind == JsonValueKind.Object &&
            d.TryGetProperty("ami_connected", out var ami) && ami.ValueKind is JsonValueKind.True or JsonValueKind.False)
            s = s with { AmiConnected = ami.GetBoolean() };
        if (el.TryGetProperty("registrations", out var regs) && regs.ValueKind == JsonValueKind.Array)
        {
            var list = new List<(string, string)>();
            foreach (var r in regs.EnumerateArray())
                list.Add((Str(r, "name"), Str(r, "status")));
            s = s with { Registrations = list };
        }
        if (el.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, int>();
            foreach (var p in q.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) dict[p.Name] = p.Value.GetInt32();
            s = s with { Queue = dict };
        }
        if (el.TryGetProperty("arm", out var arm) && arm.ValueKind == JsonValueKind.Object)
            s = s with { Arm = arm.Deserialize<ArmState>(Json.Options) ?? s.Arm };
        if (el.TryGetProperty("receiving", out var rc) && rc.ValueKind is JsonValueKind.True or JsonValueKind.False)
            s = s with { Receiving = rc.GetBoolean() };
        return s;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

public static class Json
{
    /// <summary>snake_case keys, unknown fields ignored, case-insensitive reads.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}
