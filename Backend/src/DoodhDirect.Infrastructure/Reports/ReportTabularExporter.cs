using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;
using DoodhDirect.Application.Reports;

namespace DoodhDirect.Infrastructure.Reports;

internal static class ReportTabularExporter
{
    private static readonly IReadOnlyDictionary<ReportModule, Type> RowTypes =
        new Dictionary<ReportModule, Type>
        {
            [ReportModule.Customers] = typeof(CustomerReportRow),
            [ReportModule.Employees] = typeof(EmployeeReportRow),
            [ReportModule.Orders] = typeof(OrderReportRow),
            [ReportModule.Subscriptions] = typeof(SubscriptionReportRow),
            [ReportModule.Payments] = typeof(PaymentReportRow),
            [ReportModule.Wallets] = typeof(WalletReportRow),
            [ReportModule.Deliveries] = typeof(DeliveryReportRow),
            [ReportModule.Dairy] = typeof(DairyReportRow),
            [ReportModule.MilkTests] = typeof(MilkTestReportRow),
            [ReportModule.Cameras] = typeof(CameraReportRow),
            [ReportModule.Notifications] = typeof(NotificationReportRow),
            [ReportModule.Audit] = typeof(AuditReportRow)
        };

    public static byte[] Csv(ReportModule module, IReadOnlyCollection<object> rows)
    {
        var properties = Properties(module);
        var builder = new StringBuilder()
            .AppendLine(string.Join(',', properties.Select(x => EscapeCsv(x.Name))));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(
                ',',
                properties.Select(property => EscapeCsv(
                    FormatText(property.GetValue(row)),
                    protectFormula: true))));
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return encoding.GetPreamble().Concat(encoding.GetBytes(builder.ToString())).ToArray();
    }

    public static byte[] Xlsx(ReportModule module, IReadOnlyCollection<object> rows)
    {
        var properties = Properties(module);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "[Content_Types].xml", ContentTypes);
            WriteTextEntry(archive, "_rels/.rels", PackageRelationships);
            WriteTextEntry(archive, "xl/workbook.xml", Workbook);
            WriteTextEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
            WriteWorksheet(archive, properties, rows);
        }

        return output.ToArray();
    }

    private static PropertyInfo[] Properties(ReportModule module) =>
        RowTypes[module].GetProperties(BindingFlags.Instance | BindingFlags.Public);

    private static string FormatText(object? value) => value switch
    {
        null => string.Empty,
        DateTime { Kind: DateTimeKind.Unspecified } indiaLocal =>
            indiaLocal.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTime dateTime => throw new InvalidOperationException(
            $"Report timestamps must be India-local wall-clock values; received {dateTime.Kind}."),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "true" : "false",
        string text => text,
        IEnumerable values => string.Join("; ", values.Cast<object?>().Select(FormatText)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string EscapeCsv(string value, bool protectFormula = false)
    {
        if (protectFormula && IsFormula(value))
        {
            value = $"'{value}";
        }

        return value.Contains(',', StringComparison.Ordinal) ||
               value.Contains('"') ||
               value.Contains('\r') ||
               value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static bool IsFormula(string value)
    {
        var first = value.AsSpan().TrimStart();
        return !first.IsEmpty && first[0] is '=' or '+' or '-' or '@';
    }

    private static void WriteWorksheet(
        ZipArchive archive,
        IReadOnlyList<PropertyInfo> properties,
        IReadOnlyCollection<object> rows)
    {
        var entry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            CloseOutput = false
        });

        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", SpreadsheetNamespace);
        writer.WriteStartElement("sheetData", SpreadsheetNamespace);
        WriteRow(writer, 1, properties.Select(x => (object?)x.Name));

        var rowNumber = 2;
        foreach (var row in rows)
        {
            WriteRow(writer, rowNumber++, properties.Select(x => x.GetValue(row)));
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteRow(XmlWriter writer, int rowNumber, IEnumerable<object?> values)
    {
        writer.WriteStartElement("row", SpreadsheetNamespace);
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture));
        var column = 1;
        foreach (var value in values)
        {
            WriteCell(writer, CellReference(column++, rowNumber), value);
        }

        writer.WriteEndElement();
    }

    private static void WriteCell(XmlWriter writer, string reference, object? value)
    {
        writer.WriteStartElement("c", SpreadsheetNamespace);
        writer.WriteAttributeString("r", reference);

        switch (value)
        {
            case null:
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is", SpreadsheetNamespace);
                writer.WriteElementString("t", SpreadsheetNamespace, string.Empty);
                writer.WriteEndElement();
                break;
            case bool boolean:
                writer.WriteAttributeString("t", "b");
                writer.WriteElementString("v", SpreadsheetNamespace, boolean ? "1" : "0");
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                writer.WriteElementString("v", SpreadsheetNamespace, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is", SpreadsheetNamespace);
                writer.WriteStartElement("t", SpreadsheetNamespace);
                writer.WriteAttributeString("xml", "space", XmlNamespace, "preserve");
                writer.WriteString(FormatText(value));
                writer.WriteEndElement();
                writer.WriteEndElement();
                break;
        }

        writer.WriteEndElement();
    }

    private static string CellReference(int column, int row)
    {
        var name = string.Empty;
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }

        return name + row.ToString(CultureInfo.InvariantCulture);
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private const string SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private const string PackageRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string Workbook = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="Report" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;

    private const string WorkbookRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;
}
