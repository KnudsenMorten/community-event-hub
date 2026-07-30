using System.Text.Json;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §303 P1 — the INTEGRATION-AGNOSTIC field-mapping ENGINE (operator 2026-07-24: six
/// systems today, ~ten soon; "i need 100% bullet-proof generic concept that works for
/// any integration and any type of field"). This class owns the SEMANTICS — field
/// kinds, the one generic valid-vs-changed comparison, change-management capability,
/// fetch strategy, direction, and the derivation registry. It knows NOTHING about any
/// concrete system.
///
/// <para><b>Two-layer design (§302e/§303):</b> the engine (this file) is CODE —
/// behaviour, type-safe, unit-tested. The per-system MAP ROWS are DATA: each
/// integration ships <c>config/integrations/&lt;system&gt;.fieldmap.json</c> (entities +
/// endpoints + field rows + derivation KEYS referencing functions registered here),
/// loaded fail-soft at startup via <see cref="LoadMapFile"/>; the system's code rows
/// (e.g. <see cref="ZohoFieldMap"/>) are the fallback when the file is absent or
/// invalid. A new integration = one JSON file + any new derivation functions.</para>
/// </summary>
public static class IntegrationFieldMap
{
    /// <summary>
    /// §302c change-management capability — what happens when the SOURCE value changes
    /// after the record reached the target:
    /// CreateAndUpdate ⇒ pushed automatically (change-only);
    /// CreateOnly ⇒ rides the create, later changes produce the hash-deduped ACTION mail;
    /// GuiOnly ⇒ the create refuses the field — the ACTION mail is the only channel.
    /// </summary>
    public enum Capability { CreateOnly = 0, CreateAndUpdate = 1, GuiOnly = 2 }

    /// <summary>
    /// §302e field TYPE driving the generic compare rule (see <see cref="Compare"/>):
    /// String = trimmed ordinal; MultiLine = existence (rich-text targets reformat);
    /// Integer = numeric; Object = existence (source split across fields merging into
    /// one target object — "just validate it exist"); Array = set inclusion
    /// (every expected item present, case-insensitive, target extras OK).
    /// </summary>
    public enum FieldKind { String = 0, MultiLine = 1, Integer = 2, Object = 3, Array = 4 }

    /// <summary>
    /// §302e fetch strategy — WHERE the target exposes the field for reading:
    /// List = in the get-ALL response; DetailOnly = only the per-id GET (fetch detail
    /// before comparing); NotReadable = the target accepts the write but never echoes
    /// it ⇒ no live compare, the engine stamps a source-side hash of what it last
    /// pushed (§302d) and re-pushes only when the source value changes.
    /// </summary>
    public enum ReadSupport { List = 0, DetailOnly = 1, NotReadable = 2 }

    /// <summary>
    /// §303 — the field's sync DIRECTION. Ownership is one direction PER FIELD:
    /// Outbound = CEH is the master, CEH → target (the whole Zoho flow — stage 3 was
    /// dropped, CEH is always the master there); Inbound = the external system is the
    /// source and CEH mirrors it (e.g. webshop order pulls, evaluation results).
    /// </summary>
    public enum Direction { Outbound = 0, Inbound = 1 }

    /// <summary>The generic comparison verdict (operator rules a+b, §302e).</summary>
    public enum FieldVerdict
    {
        /// <summary>Source empty or values match per the kind's rule — do nothing.</summary>
        UpToDate = 0,
        /// <summary>Missing or different in the target — update (CreateAndUpdate) or
        /// mail the ACTION line (CreateOnly/GuiOnly).</summary>
        NeedsUpdate = 1,
        /// <summary>The field is <see cref="ReadSupport.NotReadable"/> — no live
        /// compare; use the source-side pushed-value stamp instead (§302d).</summary>
        Unverifiable = 2,
    }

    /// <summary>
    /// One mapped field. NAMING (§303c, operator: "i don't see names like source and
    /// target … is guiLabel in source or target?"): every property is prefixed with
    /// the SIDE it belongs to —
    /// <list type="bullet">
    /// <item>SOURCE side: <see cref="CehField"/> (the source field name; the first of
    /// <see cref="MergedFrom"/> when several source fields feed one target field),
    /// <see cref="SourceType"/> (free-text source shape, e.g. "MultiSelect checkboxes"),
    /// <see cref="Derivation"/> (the registered function deriving the target value
    /// FROM the source record).</item>
    /// <item>TARGET side: <see cref="ApiField"/> (the target API field written/read),
    /// <see cref="GuiLabel"/> (the label the TARGET's GUI shows — ops mails speak this
    /// name), <see cref="TargetType"/> (free-text target shape).</item>
    /// <item>RULES: <see cref="Kind"/> (the generic compare rule), <see cref="Capability"/>
    /// (create/update/gui-only), <see cref="Read"/> (fetch strategy),
    /// <see cref="Direction"/> (who is master), <see cref="Limitations"/> (free-text
    /// per-field constraints, e.g. "3 e-mail updates max", "full labels kept").</item>
    /// </list>
    /// </summary>
    public sealed record Field(
        string CehField, string ApiField, string GuiLabel,
        Capability Capability = Capability.CreateOnly,
        FieldKind Kind = FieldKind.String,
        ReadSupport Read = ReadSupport.List,
        string[]? MergedFrom = null,
        string? SourceType = null,
        Direction Direction = Direction.Outbound,
        string? Derivation = null,
        string? TargetType = null,
        string? Limitations = null);

    /// <summary>
    /// One TARGET ENTITY's API surface: list endpoint, optional per-id detail (when it
    /// returns MORE than the list), create, optional update (null = create-only) and
    /// the live-verified behavioural notes the engines must respect.
    /// </summary>
    public sealed record Entity(
        string Name, string ListEndpoint, string? DetailEndpoint,
        string CreateEndpoint, string? UpdateEndpoint, string Notes);

    /// <summary>
    /// §302e — THE one generic comparison (any integration, any field type). Values
    /// arrive as strings (arrays comma-joined); the kind decides the semantics
    /// documented on <see cref="FieldKind"/>.
    /// </summary>
    public static FieldVerdict Compare(Field field, string? source, string? target)
    {
        if (field.Read == ReadSupport.NotReadable) return FieldVerdict.Unverifiable;
        if (string.IsNullOrWhiteSpace(source)) return FieldVerdict.UpToDate;   // nothing to push
        switch (field.Kind)
        {
            case FieldKind.MultiLine:
            case FieldKind.Object:
                // Existence check: rich text reformats / merged objects — present is enough.
                return string.IsNullOrWhiteSpace(target) ? FieldVerdict.NeedsUpdate : FieldVerdict.UpToDate;
            case FieldKind.Integer:
                return int.TryParse(source.Trim(), out var si) && int.TryParse(target?.Trim(), out var ti) && si == ti
                    ? FieldVerdict.UpToDate : FieldVerdict.NeedsUpdate;
            case FieldKind.Array:
                var expected = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var have = (target ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return expected.All(e => have.Contains(e, StringComparer.OrdinalIgnoreCase))
                    ? FieldVerdict.UpToDate : FieldVerdict.NeedsUpdate;
            default:   // String
                return string.Equals(source.Trim(), target?.Trim(), StringComparison.Ordinal)
                    ? FieldVerdict.UpToDate : FieldVerdict.NeedsUpdate;
        }
    }

    // ------------------------- JSON map file (layer 2) -------------------------

    /// <summary>One loaded map file: entities + field rows per record, keyed by source field name.</summary>
    public sealed record MapFile(
        string? Integration, string? SourceSystem, string? TargetSystem,
        IReadOnlyDictionary<string, Entity> Entities,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, Field>> Fields);

    /// <summary>
    /// Load + validate a per-integration fieldmap JSON (SCHEMA v2, §303c — explicit
    /// <c>source</c>/<c>target</c> blocks so it is never ambiguous which side a
    /// property describes; see config/integrations/*.fieldmap.json). FAIL-SOFT:
    /// returns null (and reports via <paramref name="onError"/>) on a
    /// missing/unreadable/invalid file so the system's code-default rows stay in
    /// force — a bad config file must never take an engine down. A RELATIVE path is
    /// tried as-is (repo/dev), then against <see cref="AppContext.BaseDirectory"/>
    /// (deployed output), mirroring EmailTemplateProvider.
    /// </summary>
    public static MapFile? LoadMapFile(string path, Action<string>? onError = null)
    {
        try
        {
            var resolved = File.Exists(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path);
            if (!File.Exists(resolved)) { onError?.Invoke($"fieldmap not found: {path}"); return null; }

            using var doc = JsonDocument.Parse(File.ReadAllText(resolved));
            var root = doc.RootElement;

            var fileDirection = Enum<Direction>(root, "direction", onError, path) ?? Direction.Outbound;
            var sourceSystem = root.TryGetProperty("source", out var src) ? Str(src, "system") : null;
            var targetSystem = root.TryGetProperty("target", out var tgt) ? Str(tgt, "system") : null;

            var entities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            var fields = new Dictionary<string, IReadOnlyDictionary<string, Field>>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Object)
            {
                foreach (var rec in recs.EnumerateObject())
                {
                    // --- target block: record name, GUI panel, endpoints, limitations ---
                    if (rec.Value.TryGetProperty("target", out var rt) && rt.ValueKind == JsonValueKind.Object)
                    {
                        string list = string.Empty, create = string.Empty;
                        string? detail = null, update = null;
                        if (rt.TryGetProperty("endpoints", out var eps) && eps.ValueKind == JsonValueKind.Object)
                        {
                            list = Str(eps, "list") ?? string.Empty;
                            detail = Str(eps, "detail");
                            create = Str(eps, "create") ?? string.Empty;
                            update = Str(eps, "update");
                        }
                        var notes = string.Join(" | ", StrArray(rt, "limitations") ?? Array.Empty<string>());
                        entities[rec.Name] = new Entity(rec.Name, list, detail, create, update, notes);
                    }

                    // --- field rows: nested source{} + target{} + rule properties ---
                    if (!rec.Value.TryGetProperty("fields", out var fs)) continue;
                    if (fs.ValueKind != JsonValueKind.Array)
                    { onError?.Invoke($"fieldmap '{path}': records.{rec.Name}.fields must be an array"); return null; }

                    var rows = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in fs.EnumerateArray())
                    {
                        if (!f.TryGetProperty("source", out var fSrc) || fSrc.ValueKind != JsonValueKind.Object
                            || !f.TryGetProperty("target", out var fTgt) || fTgt.ValueKind != JsonValueKind.Object)
                        { onError?.Invoke($"fieldmap '{path}': records.{rec.Name} field row missing source{{}}/target{{}}"); return null; }

                        var mergedFrom = StrArray(fSrc, "fields");
                        var sourceField = Str(fSrc, "field") ?? mergedFrom?.FirstOrDefault();
                        var targetField = Str(fTgt, "field");
                        var gui = Str(fTgt, "gui");
                        if (string.IsNullOrWhiteSpace(sourceField) || string.IsNullOrWhiteSpace(targetField) || string.IsNullOrWhiteSpace(gui))
                        { onError?.Invoke($"fieldmap '{path}': records.{rec.Name} row missing source.field(s)/target.field/target.gui"); return null; }

                        rows[sourceField!] = new Field(
                            sourceField!, targetField!, gui!,
                            Enum<Capability>(f, "capability", onError, path) ?? Capability.CreateOnly,
                            Enum<FieldKind>(f, "compare", onError, path) ?? FieldKind.String,
                            Enum<ReadSupport>(f, "read", onError, path) ?? ReadSupport.List,
                            mergedFrom,
                            SourceType: Str(fSrc, "type"),
                            Direction: Enum<Direction>(f, "direction", onError, path) ?? fileDirection,
                            Derivation: Str(fSrc, "derivation"),
                            TargetType: Str(fTgt, "type"),
                            Limitations: Str(f, "limitations"));
                    }
                    fields[rec.Name] = rows;
                }
            }

            return new MapFile(Str(root, "integration"), sourceSystem, targetSystem, entities, fields);
        }
        catch (Exception ex)
        {
            onError?.Invoke($"fieldmap '{path}' failed to load: {ex.Message}");
            return null;
        }
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;

    private static string[]? StrArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!).ToArray();
        return list.Length > 0 ? list : null;
    }

    // A misspelled enum value is a REAL config error — report it (fail-soft to the
    // default would silently change compare semantics, the worst failure mode).
    private static T? Enum<T>(JsonElement el, string prop, Action<string>? onError, string path)
        where T : struct
    {
        var s = Str(el, prop);
        if (s is null) return null;
        if (System.Enum.TryParse<T>(s, ignoreCase: true, out var v)) return v;
        onError?.Invoke($"fieldmap '{path}': '{s}' is not a valid {typeof(T).Name} for '{prop}'");
        return null;
    }
}
