using System;
using System.Collections;
using System.Collections.Generic;

namespace FitServer.Services;

public static class EcgDataSource
{
    public const string Fitbit = "fitbit";
    public const string EcgId = "ecg-id";

    public static string Resolve(IReadOnlyDictionary<string, object> payload)
    {
        if (payload.TryGetValue("dataSource", out var direct) && direct is not null)
        {
            var normalized = Normalize(direct.ToString());
            if (!string.IsNullOrEmpty(normalized))
                return normalized;
        }

        if (payload.TryGetValue("tags", out var tagsObj) && tagsObj is not null)
        {
            if (TagsContain(tagsObj, EcgId))
                return EcgId;
        }

        if (payload.TryGetValue("metadata", out var metadataObj) && metadataObj is not null)
        {
            if (MetadataIndicatesEcgId(metadataObj))
                return EcgId;
        }

        return Fitbit;
    }

    public static bool MatchesScope(EcgDatasetScope scope, string dataSource)
    {
        var normalized = Normalize(dataSource);
        return scope switch
        {
            EcgDatasetScope.All => true,
            EcgDatasetScope.FitbitOnly => !string.Equals(normalized, EcgId, StringComparison.OrdinalIgnoreCase),
            EcgDatasetScope.EcgIdOnly => string.Equals(normalized, EcgId, StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static bool TagsContain(object candidate, string target)
    {
        if (candidate is string tag)
            return string.Equals(Normalize(tag), target, StringComparison.OrdinalIgnoreCase);

        if (candidate is IEnumerable enumerable)
        {
            foreach (var entry in enumerable)
            {
                if (entry is null)
                    continue;
                if (string.Equals(Normalize(entry.ToString()), target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool MetadataIndicatesEcgId(object metadataObj)
    {
        if (metadataObj is IReadOnlyDictionary<string, object> readOnlyDict)
            return MetadataMatches(readOnlyDict);
        if (metadataObj is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is not string key)
                    continue;
                if (MetadataEntryMatches(key, entry.Value))
                    return true;
            }
            return false;
        }

        return false;
    }

    private static bool MetadataMatches(IReadOnlyDictionary<string, object> dict)
    {
        foreach (var kvp in dict)
        {
            if (MetadataEntryMatches(kvp.Key, kvp.Value))
                return true;
        }
        return false;
    }

    private static bool MetadataEntryMatches(string? key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key) || value is null)
            return false;

        var normalizedKey = key.Trim().ToLowerInvariant();
        if (normalizedKey is not ("activitylabel" or "devicemodel"))
            return false;

        var normalizedValue = Normalize(value.ToString());
        if (string.IsNullOrEmpty(normalizedValue))
            return false;

        return normalizedValue.Contains("ecg-id", StringComparison.OrdinalIgnoreCase);
    }
}
