using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace Melodex.Core.Subsonic;

/// <summary>
/// An answer to a Subsonic player (#7), written as XML or JSON as it asks. It's built as JSON, the shape OpenSubsonic
/// documents, and turned into XML by Subsonic's rules: a value is an attribute, an object a child element, a list one
/// element for each item, and a value called <c>value</c> the element's text.
/// </summary>
public sealed class SubsonicResponse
{
    /// <summary>The Subsonic API version Melodex answers as.</summary>
    public const string ApiVersion = "1.16.1";
    public const string Namespace = "http://subsonic.org/restapi";

    /// <summary>Melodex's own version, without the commit.</summary>
    public static readonly string ServerVersion =
        typeof(SubsonicResponse).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    public JsonObject Body { get; } = new();
    public bool Failed { get; private init; }

    public static SubsonicResponse Ok() => new();

    /// <summary>A Subsonic error: still a 200, as players expect, with its code and message.</summary>
    public static SubsonicResponse Error(int code, string message)
    {
        var response = new SubsonicResponse { Failed = true };
        response.Body["error"] = new JsonObject { ["code"] = code, ["message"] = message };
        return response;
    }

    public SubsonicResponse With(string name, JsonNode? value)
    {
        Body[name] = value;
        return this;
    }

    /// <summary>The whole answer, with the status and who's answering.</summary>
    private JsonObject Envelope()
    {
        var envelope = new JsonObject
        {
            ["status"] = Failed ? "failed" : "ok",
            ["version"] = ApiVersion,
            ["type"] = "melodex",
            ["serverVersion"] = ServerVersion,
            ["openSubsonic"] = true,
        };
        foreach (var (name, value) in Body)
            envelope[name] = value?.DeepClone();
        return envelope;
    }

    public string ToJson() =>
        new JsonObject { ["subsonic-response"] = Envelope() }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    public string ToXml()
    {
        var builder = new StringBuilder();
        using (var xml = XmlWriter.Create(builder, new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = false }))
        {
            xml.WriteStartDocument();
            Write(xml, "subsonic-response", Envelope(), Namespace);
            xml.WriteEndDocument();
        }
        // The writer says UTF-16, as a string is; it's sent as UTF-8.
        return builder.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"");
    }

    private static void Write(XmlWriter xml, string name, JsonNode? node, string? ns = null)
    {
        if (ns is null)
            xml.WriteStartElement(name);
        else
            xml.WriteStartElement(name, ns);
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                    if (value is JsonValue v && key != "value")
                        xml.WriteAttributeString(key, Text(v));
                foreach (var (key, value) in obj)
                    switch (value)
                    {
                        case JsonObject child:
                            Write(xml, key, child);
                            break;
                        case JsonArray items:
                            foreach (var item in items)
                                Write(xml, key, item);
                            break;
                    }
                if (obj["value"] is JsonValue text)
                    xml.WriteString(Text(text));
                break;
            case JsonValue value:
                xml.WriteString(Text(value));
                break;
        }
        xml.WriteEndElement();
    }

    private static string Text(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => value.GetValue<string>(),
        _ => value.ToJsonString(),
    };
}
