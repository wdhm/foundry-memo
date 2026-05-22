// Copyright (c) foundry-memo. All rights reserved.

using System.ComponentModel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FoundryMemo.Tools;

/// <summary>
/// Agent tool that generates a branded PDF memo using QuestPDF.
/// </summary>
public static class PdfGeneratorTool
{
    static PdfGeneratorTool()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Generates a PDF memo from the provided title and content.
    /// </summary>
    [Description("Generates a branded PDF memo document from a title and content text.")]
    public static Task<string> GenerateMemoPdf(
        [Description("The title of the memo")] string title,
        [Description("The memo content in plain text, organized with sections")] string content,
        [Description("Optional subtitle or date line")] string? subtitle = null)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "foundry-memo");
        Directory.CreateDirectory(outputDir);

        var fileName = $"Memo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";
        var filePath = Path.Combine(outputDir, fileName);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(50);
                page.MarginVertical(40);
                page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Segoe UI"));

                // Header
                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("MEMO")
                            .FontSize(28).Bold().FontColor(Colors.Blue.Darken3);
                    });

                    col.Item().PaddingTop(8).Text(title)
                        .FontSize(16).SemiBold();

                    if (!string.IsNullOrWhiteSpace(subtitle))
                    {
                        col.Item().PaddingTop(4).Text(subtitle)
                            .FontSize(10).FontColor(Colors.Grey.Darken1);
                    }

                    col.Item().PaddingTop(4).Text($"Generated: {DateTime.UtcNow:MMMM dd, yyyy HH:mm} UTC")
                        .FontSize(9).FontColor(Colors.Grey.Medium);

                    col.Item().PaddingTop(10)
                        .LineHorizontal(1).LineColor(Colors.Blue.Darken3);
                });

                // Content
                page.Content().PaddingTop(15).Text(content)
                    .FontSize(11).LineHeight(1.5f);

                // Footer
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        }).GeneratePdf(filePath);

        return Task.FromResult($"PDF memo generated successfully at: {filePath}");
    }
}
