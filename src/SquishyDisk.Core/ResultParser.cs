using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SquishyDisk.Core;

public static class ResultParser
{
    public static PassResult Parse(string xml, TestCase test, int pass, string command)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var root = XDocument.Load(reader).Root ?? throw new FormatException("DiskSpd returned empty XML.");
        if (root.Name != "Results") throw new FormatException("DiskSpd did not return benchmark results.");
        var spans = root.Elements("TimeSpan").ToList();
        if (spans.Count != 1) throw new FormatException("Expected one measured time span.");
        var span = spans[0];
        var seconds = Number(span.Element("TestTimeSeconds"));
        var targets = span.Elements("Thread").SelectMany(t => t.Elements("Target")).ToList();
        var prefix = test.Direction == TestDirection.Read ? "Read" : "Write";
        long bytes = 0, operations = 0;
        foreach (var target in targets)
        {
            bytes = checked(bytes + Integer(target.Element(prefix + "Bytes")));
            operations = checked(operations + Integer(target.Element(prefix + "Count")));
        }
        var latency = Number(span.Element("Latency")?.Element("Average" + prefix + "Milliseconds"));
        if (seconds <= 0 || bytes <= 0 || operations <= 0 || latency < 0)
            throw new FormatException("DiskSpd returned no valid completed I/O measurements.");
        return new(test.Row, test.Direction, pass, seconds, bytes, operations, latency, command, xml);
    }
    private static double Number(XElement? node) => node != null &&
        double.TryParse(node.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("A required DiskSpd measurement is missing or invalid.");
    private static long Integer(XElement? node) => node != null &&
        long.TryParse(node.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : throw new FormatException("A required DiskSpd counter is missing or invalid.");
}
