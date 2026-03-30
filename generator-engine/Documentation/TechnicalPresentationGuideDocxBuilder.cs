using System.IO.Compression;
using System.Security;
using System.Text;

namespace ApiGenerator.Cli.Documentation;

internal static class TechnicalPresentationGuideDocxBuilder
{
    private static readonly PresentationRow[] Rows =
    [
        new(
            "Built-in Default",
            "Generator'in baz naming, folder ve controller kurallarini kullanir. Ekstra sirket overlay'i yok.",
            "Minimal preset ile sade kalmak veya sadece genel generator davranisini kullanmak istendiginde secilir.",
            "En guvenli secenektir. Target project standardini bozmadan onun uzerine calisir.",
            "Kuruma ozel template veya ozel kod kalibi dayatmaz."),
        new(
            "GeneratedApiPolicy Standard - Rules Only",
            "GeneratedApiPolicy'den ogrenilen naming, folder, route ve genel yazim standardini uygular. Ozel kod govdesi bindirmez.",
            "Kurumsal standard korunsun ama kod govdesi generic generator template'leriyle kalsin istendiginde secilir.",
            "Target project standardinin uzerine ek kurallar bindirir.",
            "Minimal veya sade projeleri daha katmanli hale itebilir."),
        new(
            "GeneratedApiPolicy Standard - Exact Templates",
            "GeneratedApiPolicy kurallariyla birlikte controller, service, repository ve Program icin ozel template override'lari uygular.",
            "Cikti referans projeye olabildigince benzesin istendiginde secilir.",
            "Target project uzerine en baskin overlay olarak davranir.",
            "En opinionated secenektir. Minimal yapilari zorlayabilir."),
        new(
            "Workspace Default Standard",
            "Repo icindeki clean-architecture agirlikli klasor ve naming standardini uygular.",
            "Sirket template'i istemeyip daha duzenli ve katmanli bir yapi istendiginde secilir.",
            "Target project uzerine clean-architecture egilimli bir overlay bindirir.",
            "Minimal veya duz yapilarda folder beklentisini degistirebilir.")
    ];

    public static byte[] Build()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
            WriteEntry(archive, "_rels/.rels", BuildRootRelationshipsXml());
            WriteEntry(archive, "docProps/core.xml", BuildCorePropertiesXml());
            WriteEntry(archive, "docProps/app.xml", BuildAppPropertiesXml());
            WriteEntry(archive, "word/document.xml", BuildDocumentXml());
            WriteEntry(archive, "word/styles.xml", BuildStylesXml());
            WriteEntry(archive, "word/_rels/document.xml.rels", BuildDocumentRelationshipsXml());
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string BuildContentTypesXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
          <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
          <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
          <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
        </Types>
        """;

    private static string BuildRootRelationshipsXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
        </Relationships>
        """;

    private static string BuildDocumentRelationshipsXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private static string BuildCorePropertiesXml()
    {
        const string createdUtc = "2026-03-12T00:00:00Z";
        return $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                           xmlns:dc="http://purl.org/dc/elements/1.1/"
                           xmlns:dcterms="http://purl.org/dc/terms/"
                           xmlns:dcmitype="http://purl.org/dc/dcmitype/"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <dc:title>API Generator Technical Presentation Guide</dc:title>
          <dc:subject>Standard Profile Selection Table</dc:subject>
          <dc:creator>API Generator Platform</dc:creator>
          <cp:keywords>presentation,standard-profile,documentation</cp:keywords>
          <dc:description>Presentation-oriented comparison of standard profile options.</dc:description>
          <cp:lastModifiedBy>API Generator Platform</cp:lastModifiedBy>
          <dcterms:created xsi:type="dcterms:W3CDTF">{{createdUtc}}</dcterms:created>
          <dcterms:modified xsi:type="dcterms:W3CDTF">{{createdUtc}}</dcterms:modified>
        </cp:coreProperties>
        """;
    }

    private static string BuildAppPropertiesXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"
                    xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
          <Application>API Generator Platform</Application>
          <DocSecurity>0</DocSecurity>
          <ScaleCrop>false</ScaleCrop>
          <Company>Local</Company>
          <LinksUpToDate>false</LinksUpToDate>
          <SharedDoc>false</SharedDoc>
          <HyperlinksChanged>false</HyperlinksChanged>
          <AppVersion>1.0</AppVersion>
        </Properties>
        """;

    private static string BuildStylesXml() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault>
              <w:rPr>
                <w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/>
                <w:sz w:val="20"/>
                <w:szCs w:val="20"/>
              </w:rPr>
            </w:rPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
            <w:name w:val="Normal"/>
            <w:qFormat/>
          </w:style>
          <w:style w:type="paragraph" w:styleId="Title">
            <w:name w:val="Title"/>
            <w:basedOn w:val="Normal"/>
            <w:qFormat/>
            <w:pPr>
              <w:spacing w:before="120" w:after="180"/>
            </w:pPr>
            <w:rPr>
              <w:b/>
              <w:color w:val="1F4E78"/>
              <w:sz w:val="34"/>
              <w:szCs w:val="34"/>
            </w:rPr>
          </w:style>
          <w:style w:type="paragraph" w:styleId="Subtitle">
            <w:name w:val="Subtitle"/>
            <w:basedOn w:val="Normal"/>
            <w:qFormat/>
            <w:pPr>
              <w:spacing w:after="140"/>
            </w:pPr>
            <w:rPr>
              <w:color w:val="5B6570"/>
              <w:sz w:val="22"/>
              <w:szCs w:val="22"/>
            </w:rPr>
          </w:style>
          <w:style w:type="paragraph" w:styleId="Heading1">
            <w:name w:val="heading 1"/>
            <w:basedOn w:val="Normal"/>
            <w:qFormat/>
            <w:pPr>
              <w:spacing w:before="180" w:after="100"/>
            </w:pPr>
            <w:rPr>
              <w:b/>
              <w:color w:val="1F4E78"/>
              <w:sz w:val="26"/>
              <w:szCs w:val="26"/>
            </w:rPr>
          </w:style>
        </w:styles>
        """;

    private static string BuildDocumentXml()
    {
        var builder = new StringBuilder();
        builder.Append(
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:wpc="http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas"
                        xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
                        xmlns:o="urn:schemas-microsoft-com:office:office"
                        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                        xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"
                        xmlns:v="urn:schemas-microsoft-com:vml"
                        xmlns:wp14="http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing"
                        xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                        xmlns:w10="urn:schemas-microsoft-com:office:word"
                        xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                        xmlns:w14="http://schemas.microsoft.com/office/word/2010/wordml"
                        xmlns:wpg="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"
                        xmlns:wpi="http://schemas.microsoft.com/office/word/2010/wordprocessingInk"
                        xmlns:wne="http://schemas.microsoft.com/office/word/2006/wordml"
                        xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"
                        mc:Ignorable="w14 wp14">
              <w:body>
            """);

        builder.Append(Paragraph("API Generator Technical Presentation Guide", "Title"));
        builder.Append(Paragraph("Standard Profile secim tablosu ve hangi senaryoda hangisinin secilecegi.", "Subtitle"));
        builder.Append(Paragraph("Bu tablo, standard profile seciminde hizli karar vermek icin hazirlandi. Ozellikle Default Framework modunda hangi secenegin overlay uyguladigini kisa ve savunulabilir bir dille ozetler.", "Normal"));
        builder.Append(Paragraph("Standard Profile Secim Tablosu", "Heading1"));
        builder.Append(ProfileComparisonTable());
        builder.Append(Paragraph("En kisa cevap: sadece secili projenin kendi standardi korunsun istiyorsan Built-in Default, kurumsal kurallar eklensin ama kod govdesi generic kalsin istiyorsan Rules Only, referans projeye en yakin ciktiyi istiyorsan Exact Templates, daha clean architecture agirlikli klasor duzeni istiyorsan Workspace Default Standard secilir.", "Normal"));

        builder.Append(
            """
                <w:sectPr>
                  <w:pgSz w:w="16838" w:h="11906" w:orient="landscape"/>
                  <w:pgMar w:top="900" w:right="700" w:bottom="900" w:left="700" w:header="720" w:footer="720" w:gutter="0"/>
                </w:sectPr>
              </w:body>
            </w:document>
            """);

        return builder.ToString();
    }

    private static string ProfileComparisonTable()
    {
        var builder = new StringBuilder();
        builder.Append(
            """
            <w:tbl>
              <w:tblPr>
                <w:tblStyle w:val="TableGrid"/>
                <w:tblW w:w="0" w:type="auto"/>
                <w:tblBorders>
                  <w:top w:val="single" w:sz="8" w:space="0" w:color="B4C6E7"/>
                  <w:left w:val="single" w:sz="8" w:space="0" w:color="B4C6E7"/>
                  <w:bottom w:val="single" w:sz="8" w:space="0" w:color="B4C6E7"/>
                  <w:right w:val="single" w:sz="8" w:space="0" w:color="B4C6E7"/>
                  <w:insideH w:val="single" w:sz="8" w:space="0" w:color="B4C6E7"/>
                  <w:insideV w:val="single" w:sz="8" w:space="0" w:color="B4C6E7"/>
                </w:tblBorders>
              </w:tblPr>
            """);

        builder.Append(TableRow(
            TableCell("Secenek", true, "1F4E78", true),
            TableCell("Ne Uygular", true, "1F4E78", true),
            TableCell("Ne Zaman Secilir", true, "1F4E78", true),
            TableCell("Default Framework ile Iliskisi", true, "1F4E78", true),
            TableCell("Dikkat Edilecek Nokta", true, "1F4E78", true)));

        foreach (var row in Rows)
        {
            builder.Append(TableRow(
                TableCell(row.Option, true, "F8FBFF"),
                TableCell(row.WhatItApplies, false, "FFFFFF"),
                TableCell(row.WhenToChoose, false, "FFFFFF"),
                TableCell(row.DefaultFrameworkRelation, false, "FFFFFF"),
                TableCell(row.WatchOut, false, "FFFFFF")));
        }

        builder.Append("</w:tbl>");
        return builder.ToString();
    }

    private static string TableRow(params string[] cells) =>
        $"<w:tr>{string.Concat(cells)}</w:tr>";

    private static string TableCell(string text, bool bold, string fill, bool whiteText = false)
    {
        var color = whiteText ? "<w:color w:val=\"FFFFFF\"/>" : string.Empty;
        var boldXml = bold ? "<w:b/>" : string.Empty;
        return $$"""
        <w:tc>
          <w:tcPr>
            <w:shd w:val="clear" w:color="auto" w:fill="{{fill}}"/>
            <w:tcW w:w="0" w:type="auto"/>
          </w:tcPr>
          <w:p>
            <w:r>
              <w:rPr>{{boldXml}}{{color}}<w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr>
              <w:t xml:space="preserve">{{Escape(text)}}</w:t>
            </w:r>
          </w:p>
        </w:tc>
        """;
    }

    private static string Paragraph(string text, string style) =>
        $$"""
        <w:p>
          <w:pPr>
            <w:pStyle w:val="{{style}}"/>
          </w:pPr>
          <w:r>
            <w:t xml:space="preserve">{{Escape(text)}}</w:t>
          </w:r>
        </w:p>
        """;

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private readonly record struct PresentationRow(
        string Option,
        string WhatItApplies,
        string WhenToChoose,
        string DefaultFrameworkRelation,
        string WatchOut);
}
