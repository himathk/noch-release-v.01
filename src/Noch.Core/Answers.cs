using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noch.Core;

/// The exact JSON Claude Code expects back.
public static class Answers
{
    static readonly JsonSerializerOptions Opt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Permission(JsonObject decision) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision }
    }.ToJsonString(Opt);

    public static string Allow() => Permission(new JsonObject { ["behavior"] = "allow" });
    public static string AllowAlways(JsonArray suggestions) => Permission(new JsonObject { ["behavior"] = "allow", ["updatedPermissions"] = suggestions.DeepClone() });
    public static string Deny() => Permission(new JsonObject { ["behavior"] = "deny", ["message"] = "The user denied this from the notch." });

    public static string Question(JsonObject input, IReadOnlyDictionary<string, string> answers)
    {
        var updated = (JsonObject)input.DeepClone();
        var map = new JsonObject();
        foreach (var (q, a) in answers) map[q] = a;
        updated["answers"] = map;
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "allow", ["updatedInput"] = updated }
        }.ToJsonString(Opt);
    }
}
