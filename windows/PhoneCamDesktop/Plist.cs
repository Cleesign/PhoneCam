using System.Collections;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhoneCamDesktop;

/// <summary>Minimal XML property-list reader/writer — just enough for usbmux messages.</summary>
internal static class Plist
{
    public static byte[] Serialize(IDictionary<string, object> dict)
    {
        var root = new XElement("plist", new XAttribute("version", "1.0"), ToElement(dict));
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            root.ToString(SaveOptions.DisableFormatting) + "\n";
        return Encoding.UTF8.GetBytes(xml);
    }

    public static object? Parse(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data).TrimEnd('\0');
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        var doc = XDocument.Load(reader);
        var first = doc.Root?.Elements().FirstOrDefault();
        return first is null ? null : FromElement(first);
    }

    private static XElement ToElement(object value) => value switch
    {
        string s => new XElement("string", s),
        bool b => new XElement(b ? "true" : "false"),
        int i => new XElement("integer", i.ToString(CultureInfo.InvariantCulture)),
        long l => new XElement("integer", l.ToString(CultureInfo.InvariantCulture)),
        uint u => new XElement("integer", u.ToString(CultureInfo.InvariantCulture)),
        double d => new XElement("real", d.ToString("R", CultureInfo.InvariantCulture)),
        byte[] bytes => new XElement("data", Convert.ToBase64String(bytes)),
        IDictionary<string, object> dict => new XElement("dict",
            dict.SelectMany(kv => new[] { new XElement("key", kv.Key), ToElement(kv.Value) })),
        IEnumerable list => new XElement("array", list.Cast<object>().Select(ToElement)),
        _ => throw new NotSupportedException($"Plist type not supported: {value.GetType()}"),
    };

    private static object FromElement(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
            {
                var dict = new Dictionary<string, object>();
                var children = e.Elements().ToList();
                for (int i = 0; i + 1 < children.Count; i += 2)
                {
                    if (children[i].Name.LocalName == "key")
                        dict[children[i].Value] = FromElement(children[i + 1]);
                }
                return dict;
            }
            case "array":
                return e.Elements().Select(FromElement).ToList();
            case "integer":
                return long.Parse(e.Value.Trim(), CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture);
            case "true":
                return true;
            case "false":
                return false;
            case "data":
                return Convert.FromBase64String(new string(e.Value.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            default: // string, date, anything else
                return e.Value;
        }
    }
}
