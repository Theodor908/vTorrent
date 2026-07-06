using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace vTorrent.Server.Services;

public class SettingsRedactor
{
    private static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "jwtSecret", "localPasswordHash", "oidcClientSecret", "httpsCertPassword", "password"
    };

    private const string RedactedValue = "***";

    public void Redact(JsonObject obj)
    {
        foreach (var (key, value) in obj.ToArray())
        {
            if (value is JsonObject nested)
            {
                Redact(nested);
            }
            else if (value is JsonValue && IsSensitive(key))
            {
                obj[key] = RedactedValue;
            }
        }
    }

    public void StripRedactedFields(JsonObject obj)
    {
        foreach (var (key, value) in obj.ToArray())
        {
            if (value is JsonObject nested)
            {
                StripRedactedFields(nested);
            }
            else if (value is JsonValue jv && jv.TryGetValue<string>(out var str) && str == RedactedValue)
            {
                obj.Remove(key);
            }
        }
    }

    private static bool IsSensitive(string fieldName)
    {
        if (SensitiveFieldNames.Contains(fieldName))
            return true;

        return fieldName.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("password", StringComparison.OrdinalIgnoreCase)
            || fieldName.Contains("passwordhash", StringComparison.OrdinalIgnoreCase);
    }
}
