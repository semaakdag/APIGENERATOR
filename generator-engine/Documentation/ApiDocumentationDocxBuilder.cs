using ApiGenerator.Cli.Analyzers;
using ApiGenerator.Cli.Generators;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ApiGenerator.Cli.Documentation;

internal static class ApiDocumentationDocxBuilder
{
    public static byte[] Build(SolutionTemplateModel model)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
            WriteEntry(archive, "_rels/.rels", BuildRootRelationshipsXml());
            WriteEntry(archive, "docProps/core.xml", BuildCorePropertiesXml(model));
            WriteEntry(archive, "docProps/app.xml", BuildAppPropertiesXml());
            WriteEntry(archive, "word/document.xml", BuildDocumentXml(model));
            WriteEntry(archive, "word/styles.xml", BuildStylesXml());
            WriteEntry(archive, "word/_rels/document.xml.rels", BuildDocumentRelationshipsXml());
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        // Fixed timestamp keeps the package byte-identical across runs so regeneration does not report false conflicts.
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
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

    private static string BuildCorePropertiesXml(SolutionTemplateModel model)
    {
        const string createdUtc = "2026-03-11T00:00:00Z";
        return $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                           xmlns:dc="http://purl.org/dc/elements/1.1/"
                           xmlns:dcterms="http://purl.org/dc/terms/"
                           xmlns:dcmitype="http://purl.org/dc/dcmitype/"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <dc:title>{{Escape(model.SolutionName)}} API Documentation</dc:title>
          <dc:subject>Generated API Reference</dc:subject>
          <dc:creator>API Generator Platform</dc:creator>
          <cp:keywords>api,generator,documentation</cp:keywords>
          <dc:description>Generated API methods and endpoints.</dc:description>
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
                <w:sz w:val="22"/>
                <w:szCs w:val="22"/>
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
            <w:uiPriority w:val="10"/>
            <w:qFormat/>
            <w:pPr>
              <w:spacing w:before="120" w:after="180"/>
            </w:pPr>
            <w:rPr>
              <w:b/>
              <w:color w:val="1F4E78"/>
              <w:sz w:val="36"/>
              <w:szCs w:val="36"/>
            </w:rPr>
          </w:style>
          <w:style w:type="paragraph" w:styleId="Subtitle">
            <w:name w:val="Subtitle"/>
            <w:basedOn w:val="Normal"/>
            <w:qFormat/>
            <w:pPr>
              <w:spacing w:after="160"/>
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
            <w:next w:val="Normal"/>
            <w:uiPriority w:val="9"/>
            <w:qFormat/>
            <w:pPr>
              <w:spacing w:before="220" w:after="120"/>
            </w:pPr>
            <w:rPr>
              <w:b/>
              <w:color w:val="1F4E78"/>
              <w:sz w:val="28"/>
              <w:szCs w:val="28"/>
            </w:rPr>
          </w:style>
          <w:style w:type="paragraph" w:styleId="Heading2">
            <w:name w:val="heading 2"/>
            <w:basedOn w:val="Normal"/>
            <w:qFormat/>
            <w:pPr>
              <w:spacing w:before="120" w:after="80"/>
            </w:pPr>
            <w:rPr>
              <w:b/>
              <w:color w:val="244061"/>
              <w:sz w:val="24"/>
              <w:szCs w:val="24"/>
            </w:rPr>
          </w:style>
        </w:styles>
        """;

    private static string BuildDocumentXml(SolutionTemplateModel model)
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

        builder.Append(Paragraph($"{model.SolutionName}.Api", "Title"));
        builder.Append(Paragraph("Generated API reference with methods, routes, request and response types.", "Subtitle"));
        builder.Append(InfoTable(model));
        builder.Append(Paragraph("Endpoint Inventory", "Heading1"));

        foreach (var entity in model.Entities)
        {
            var baseRoute = ResolveBaseRoute(entity, model.Profile);
            var responseType = UsesContractModels(model.Profile) ? entity.ResponseName : entity.EntityTypeName;
            var createRequestType = UsesContractModels(model.Profile) ? entity.CreateRequestName : entity.EntityTypeName;
            var updateRequestType = UsesContractModels(model.Profile) ? entity.UpdateRequestName : entity.EntityTypeName;

            builder.Append(Paragraph(entity.EntityTypeName, "Heading1"));
            builder.Append(KeyValueParagraph("Artifact", UsesControllerArtifacts(model.Profile) ? entity.ControllerName : entity.EndpointModuleName));
            builder.Append(KeyValueParagraph("Base Route", baseRoute));
            builder.Append(MethodTable(
                ("GetAll", "GET", baseRoute, "None", $"IReadOnlyList<{responseType}>"),
                ("GetById", "GET", $"{baseRoute}/{entity.KeyRouteTemplate}", "None", responseType),
                ("Create", "POST", baseRoute, createRequestType, responseType),
                ("Update", "PUT", $"{baseRoute}/{entity.KeyRouteTemplate}", updateRequestType, responseType),
                ("Delete", "DELETE", $"{baseRoute}/{entity.KeyRouteTemplate}", "None", "204 No Content")));
            builder.Append(Paragraph("Code Methods", "Heading2"));
            builder.Append(BulletParagraph("GetAll"));
            builder.Append(BulletParagraph("GetById"));
            builder.Append(BulletParagraph("Create"));
            builder.Append(BulletParagraph("Update"));
            builder.Append(BulletParagraph("Delete"));
        }

        builder.Append(
            """
                <w:sectPr>
                  <w:pgSz w:w="11906" w:h="16838"/>
                  <w:pgMar w:top="1200" w:right="1000" w:bottom="1200" w:left="1000" w:header="720" w:footer="720" w:gutter="0"/>
                </w:sectPr>
              </w:body>
            </w:document>
            """);

        return builder.ToString();
    }

    private static string InfoTable(SolutionTemplateModel model)
    {
        var rows = new[]
        {
            ("Project", $"{model.SolutionName}.Api"),
            ("API Style", UsesControllerArtifacts(model.Profile) ? "Controller" : "Endpoint Module"),
            ("Entity Count", model.Entities.Count.ToString()),
            ("Windows Authentication", model.Profile.Framework.UseWindowsAuthentication.ToString()),
            ("Database Provider", model.Profile.Framework.DatabaseProvider)
        };

        return KeyValueTable(rows);
    }

    private static string KeyValueTable(IEnumerable<(string Key, string Value)> rows)
    {
        var builder = new StringBuilder();
        builder.Append(
            """
            <w:tbl>
              <w:tblPr>
                <w:tblW w:w="0" w:type="auto"/>
                <w:tblBorders>
                  <w:top w:val="single" w:sz="8" w:space="0" w:color="D9E2F3"/>
                  <w:left w:val="single" w:sz="8" w:space="0" w:color="D9E2F3"/>
                  <w:bottom w:val="single" w:sz="8" w:space="0" w:color="D9E2F3"/>
                  <w:right w:val="single" w:sz="8" w:space="0" w:color="D9E2F3"/>
                  <w:insideH w:val="single" w:sz="8" w:space="0" w:color="D9E2F3"/>
                  <w:insideV w:val="single" w:sz="8" w:space="0" w:color="D9E2F3"/>
                </w:tblBorders>
              </w:tblPr>
            """);

        foreach (var (key, value) in rows)
        {
            builder.Append(TableRow(
                TableCell(key, true, "D9EAF7"),
                TableCell(value, false, "FFFFFF")));
        }

        builder.Append("</w:tbl>");
        return builder.ToString();
    }

    private static string MethodTable(params (string Method, string Verb, string Route, string Request, string Response)[] rows)
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
            TableCell("Method", true, "1F4E78", true),
            TableCell("HTTP", true, "1F4E78", true),
            TableCell("Route", true, "1F4E78", true),
            TableCell("Request Body", true, "1F4E78", true),
            TableCell("Response", true, "1F4E78", true)));

        foreach (var row in rows)
        {
            builder.Append(TableRow(
                TableCell(row.Method, false, "F8FBFF"),
                TableCell(row.Verb, false, "F8FBFF"),
                TableCell(row.Route, false, "FFFFFF"),
                TableCell(row.Request, false, "FFFFFF"),
                TableCell(row.Response, false, "FFFFFF")));
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
              <w:rPr>{{boldXml}}{{color}}</w:rPr>
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

    private static string KeyValueParagraph(string key, string value) =>
        $$"""
        <w:p>
          <w:r>
            <w:rPr><w:b/></w:rPr>
            <w:t xml:space="preserve">{{Escape(key)}}: </w:t>
          </w:r>
          <w:r>
            <w:t xml:space="preserve">{{Escape(value)}}</w:t>
          </w:r>
        </w:p>
        """;

    private static string BulletParagraph(string text) =>
        $$"""
        <w:p>
          <w:pPr>
            <w:ind w:left="720" w:hanging="360"/>
          </w:pPr>
          <w:r>
            <w:t xml:space="preserve">- {{Escape(text)}}</w:t>
          </w:r>
        </w:p>
        """;

    private static bool UsesControllerArtifacts(StandardProfile profile) =>
        profile.Framework.UseControllers || profile.Framework.ApiStyle.Equals("controller", StringComparison.OrdinalIgnoreCase);

    private static bool UsesContractModels(StandardProfile profile) =>
        profile.Framework.UseServiceLayer || profile.Framework.UseContractModels;

    private static string ResolveBaseRoute(EntityTemplateModel entity, StandardProfile profile)
    {
        if (!UsesControllerArtifacts(profile))
        {
            return $"/api/{entity.EntityName}";
        }

        var controllerSegment = entity.ControllerName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? entity.ControllerName[..^"Controller".Length]
            : entity.ControllerName;
        var route = profile.Controller.RouteTemplate.Replace("[controller]", controllerSegment, StringComparison.OrdinalIgnoreCase);
        return route.StartsWith("/", StringComparison.Ordinal) ? route : $"/{route}";
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
