using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroGcCreator.Models;

/// <summary>
/// A <c>.rgc</c> card definition, kept as a JSON tree: fields the creator edits are read and written
/// in place, so unknown fields and the key order survive a save.
/// </summary>
public sealed class CardDocument
{
    public const string RetroArch = "retroarch";
    public const string MisterFpga = "misterfpga";
    public const string DefaultRetroArchVersion = "1.22.2";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        // Keep accents, apostrophes and "&" readable, like the hand-written cards.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly JsonObject _root;

    private CardDocument(JsonObject root) => _root = root;

    /// <summary>A new card with the fields in the usual order.</summary>
    public static CardDocument CreateNew(string name) => new(new JsonObject
    {
        ["name"] = name,
        ["description"] = string.Empty,
        ["platform"] = string.Empty,
        ["year"] = string.Empty,
        ["image"] = string.Empty,
        ["developer"] = string.Empty,
        ["genre"] = string.Empty,
        ["players"] = string.Empty,
        [RetroArch] = new JsonObject
        {
            ["version"] = DefaultRetroArchVersion,
            ["system"] = string.Empty,
            ["core"] = string.Empty,
            ["roms"] = new JsonArray()
        }
    });

    public static CardDocument Load(string path)
    {
        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text, documentOptions: ReadOptions)
                   ?? throw new JsonException($"The card definition is empty: {path}");
        return node is JsonObject root
            ? new CardDocument(root)
            : throw new JsonException($"The card definition is not a JSON object: {path}");
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
        {
            _root.WriteTo(writer);
        }

        File.AppendAllText(temporary, Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
    }

    // ------------------------------------------------------------ access

    public string GetString(string key, string? section = null) =>
        Section(section)?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    /// <summary>Sets a string. An empty value is only written when the field already exists.</summary>
    public void SetString(string key, string value, string? section = null)
    {
        if (string.IsNullOrEmpty(value) && Section(section)?.ContainsKey(key) != true)
            return;

        var target = section is null ? _root : EnsureSection(section);
        target[key] = value;
    }

    public IReadOnlyList<string> GetList(string key, string section) =>
        Section(section)?[key] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty).ToList()
            : [];

    /// <summary>Sets a list. An empty list is only written when the field already exists.</summary>
    public void SetList(string key, string section, IEnumerable<string> values)
    {
        var items = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (items.Count == 0 && Section(section)?.ContainsKey(key) != true)
            return;

        // Unchanged lists keep their node (and formatting).
        if (GetList(key, section).SequenceEqual(items) && Section(section)?[key] is JsonArray)
            return;

        EnsureSection(section)[key] = new JsonArray(items.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    }

    public bool HasSection(string section) => _root[section] is JsonObject;

    public void RemoveSection(string section) => _root.Remove(section);

    private JsonObject? Section(string? section) => section is null ? _root : _root[section] as JsonObject;

    private JsonObject EnsureSection(string section)
    {
        if (_root[section] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        _root[section] = created;
        return created;
    }
}
