// Copyright (c) foundry-memo. All rights reserved.

using System.ComponentModel;
using System.Text.RegularExpressions;
using FoundryMemo.Services;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace FoundryMemo.Tools;

/// <summary>
/// Agent tool that generates a branded PDF memo using PdfSharp
/// and uploads it to SharePoint.
/// </summary>
public class PdfGeneratorTool
{
    private readonly SharePointUploadService? _uploadService;
    private readonly ILogger<PdfGeneratorTool>? _logger;

    // Guard against extremely large PDFs that could exhaust memory or exceed tool output limits
    private const int MaxContentLength = 50_000;

    public PdfGeneratorTool(SharePointUploadService? uploadService, ILogger<PdfGeneratorTool>? logger = null)
    {
        CrossPlatformFontResolver.Register();
        _uploadService = uploadService;
        _logger = logger;
    }

    /// <summary>
    /// Generates a PDF memo and uploads it to the same SharePoint folder.
    /// </summary>
    [Description("Generates a branded PDF memo and uploads it to the source SharePoint folder. Returns the SharePoint URL of the uploaded PDF.")]
    public async Task<string> GenerateMemoPdf(
        [Description("The title of the memo")] string title,
        [Description("The memo content in plain text with sections marked by ## headings and - bullet points")] string content,
        [Description("The SharePoint site URL where the source documents came from")] string sharePointUrl,
        [Description("Optional subtitle or date line")] string? subtitle = null)
    {
        try
        {
            // Content size guard
            if (content.Length > MaxContentLength)
            {
                _logger?.LogWarning("Content truncated from {Original} to {Max} chars for PDF generation",
                    content.Length, MaxContentLength);
                content = content[..MaxContentLength] + "\n\n[... content truncated — original was too large for a single memo]";
            }

            _logger?.LogInformation("Generating PDF memo: title={Title}, content={ContentLen} chars, url={Url}",
                title, content.Length, sharePointUrl);

            var pdfBytes = RenderPdf(title, content, subtitle);
            var fileName = $"Memo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf";

            _logger?.LogInformation("PDF rendered: {Size} bytes, {FileName}", pdfBytes.Length, fileName);

            if (_uploadService is not null && !string.IsNullOrEmpty(sharePointUrl))
            {
                var webUrl = await _uploadService.UploadAsync(
                    sharePointUrl, "Shared Documents", fileName, pdfBytes);
                return $"PDF memo uploaded to SharePoint: {webUrl}";
            }

            // Fallback: save locally if no upload service
            var outputDir = Path.Combine(Path.GetTempPath(), "foundry-memo");
            Directory.CreateDirectory(outputDir);
            var filePath = Path.Combine(outputDir, fileName);
            await File.WriteAllBytesAsync(filePath, pdfBytes);
            return $"PDF memo saved locally at: {filePath} (SharePoint upload not configured)";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PDF generation failed for title={Title}", title);
            return $"PDF generation failed: {ex.Message}";
        }
    }

    private static byte[] RenderPdf(string title, string content, string? subtitle)
    {
        var document = new PdfDocument();
        document.Info.Title = title;
        document.Info.Author = "foundry-memo";

        var sections = ParseSections(content);

        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        var gfx = XGraphics.FromPdfPage(page);

        // Fonts
        var headerFont = new XFont("Arial", 24, XFontStyleEx.Bold);
        var titleFont = new XFont("Arial", 14, XFontStyleEx.Bold);
        var subtitleFont = new XFont("Arial", 9, XFontStyleEx.Italic);
        var dateFont = new XFont("Arial", 8);
        var sectionFont = new XFont("Arial", 11, XFontStyleEx.Bold);
        var bodyFont = new XFont("Arial", 9.5);
        var bulletFont = new XFont("Arial", 9.5);
        var subBulletFont = new XFont("Arial", 8.5, XFontStyleEx.Italic);
        var footerFont = new XFont("Arial", 7);

        var accentColor = XColor.FromArgb(0, 51, 102); // Dark blue
        var lightGrey = XColor.FromArgb(140, 140, 140);

        double marginLeft = 50;
        double marginRight = 50;
        double pageWidth = page.Width.Point;
        double contentWidth = pageWidth - marginLeft - marginRight;
        double y = 40;
        double pageHeight = page.Height.Point;
        double bottomMargin = 50;

        // --- Header ---
        gfx.DrawString("MEMO", headerFont, new XSolidBrush(accentColor), marginLeft, y + 24);
        y += 36;

        gfx.DrawString(title, titleFont, XBrushes.Black, marginLeft, y + 14);
        y += 22;

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            gfx.DrawString(subtitle, subtitleFont, new XSolidBrush(lightGrey), marginLeft, y + 9);
            y += 14;
        }

        gfx.DrawString($"Generated: {DateTime.UtcNow:MMMM dd, yyyy}", dateFont, new XSolidBrush(lightGrey), marginLeft, y + 8);
        y += 16;

        // Accent line
        gfx.DrawLine(new XPen(accentColor, 1.5), marginLeft, y, marginLeft + contentWidth, y);
        y += 12;

        // --- Content ---
        foreach (var section in sections)
        {
            // Check if we need a new page
            double neededHeight = section.IsHeading ? 24 : section.IsSubBullet ? 12 : 14;
            if (y + neededHeight > pageHeight - bottomMargin)
            {
                // Footer on current page
                DrawFooter(gfx, page, document, footerFont, lightGrey);

                page = document.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                gfx = XGraphics.FromPdfPage(page);
                y = 40;
            }

            if (section.IsHeading)
            {
                y += 8;
                gfx.DrawString(section.Text, sectionFont, new XSolidBrush(accentColor), marginLeft, y + 11);
                y += 15;
                gfx.DrawLine(new XPen(XColors.LightGray, 0.5), marginLeft, y, marginLeft + contentWidth, y);
                y += 6;
            }
            else if (section.IsBullet)
            {
                double bulletX = marginLeft + 8;
                gfx.DrawString("\u2022", bulletFont, new XSolidBrush(accentColor), bulletX, y + 9.5);

                double textX = bulletX + 12;
                double textWidth = contentWidth - 20;

                // Word-wrap long bullets
                var lines = WrapText(gfx, section.Text, bulletFont, textWidth);
                foreach (var line in lines)
                {
                    if (y + 13 > pageHeight - bottomMargin)
                    {
                        DrawFooter(gfx, page, document, footerFont, lightGrey);
                        page = document.AddPage();
                        page.Size = PdfSharp.PageSize.A4;
                        gfx = XGraphics.FromPdfPage(page);
                        y = 40;
                    }
                    gfx.DrawString(line, bulletFont, XBrushes.Black, textX, y + 9.5);
                    y += 13;
                }
                y += 1;
            }
            else if (section.IsSubBullet)
            {
                double bulletX = marginLeft + 24;
                gfx.DrawString("\u25E6", subBulletFont, new XSolidBrush(lightGrey), bulletX, y + 8.5);

                double textX = bulletX + 10;
                double textWidth = contentWidth - 34;

                var lines = WrapText(gfx, section.Text, subBulletFont, textWidth);
                foreach (var line in lines)
                {
                    if (y + 12 > pageHeight - bottomMargin)
                    {
                        DrawFooter(gfx, page, document, footerFont, lightGrey);
                        page = document.AddPage();
                        page.Size = PdfSharp.PageSize.A4;
                        gfx = XGraphics.FromPdfPage(page);
                        y = 40;
                    }
                    gfx.DrawString(line, subBulletFont, new XSolidBrush(XColor.FromArgb(80, 80, 80)), textX, y + 8.5);
                    y += 11;
                }
                y += 1;
            }
            else if (!string.IsNullOrWhiteSpace(section.Text))
            {
                double textWidth = contentWidth;
                var lines = WrapText(gfx, section.Text, bodyFont, textWidth);
                foreach (var line in lines)
                {
                    if (y + 13 > pageHeight - bottomMargin)
                    {
                        DrawFooter(gfx, page, document, footerFont, lightGrey);
                        page = document.AddPage();
                        page.Size = PdfSharp.PageSize.A4;
                        gfx = XGraphics.FromPdfPage(page);
                        y = 40;
                    }
                    gfx.DrawString(line, bodyFont, XBrushes.Black, marginLeft, y + 9.5);
                    y += 13;
                }
                y += 3;
            }
        }

        // Footer on last page
        DrawFooter(gfx, page, document, footerFont, lightGrey);

        using var ms = new MemoryStream();
        document.Save(ms, false);
        return ms.ToArray();
    }

    private static void DrawFooter(XGraphics gfx, PdfPage page, PdfDocument doc, XFont font, XColor color)
    {
        var pageNum = doc.PageCount;
        var text = $"Page {pageNum}  |  foundry-memo";
        var size = gfx.MeasureString(text, font);
        gfx.DrawString(text, font, new XSolidBrush(color),
            (page.Width.Point - size.Width) / 2, page.Height.Point - 25);
    }

    private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ');
        var current = "";

        foreach (var word in words)
        {
            var test = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
            if (gfx.MeasureString(test, font).Width > maxWidth && !string.IsNullOrEmpty(current))
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = test;
            }
        }

        if (!string.IsNullOrEmpty(current))
            lines.Add(current);

        return lines;
    }

    private static List<ContentBlock> ParseSections(string content)
    {
        var blocks = new List<ContentBlock>();
        var lines = content.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (Regex.IsMatch(line, @"^#{1,3}\s+"))
            {
                var text = Regex.Replace(line, @"^#{1,3}\s+", "").Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    blocks.Add(new ContentBlock(text, IsHeading: true));
            }
            else if (Regex.IsMatch(line, @"^\s{2,}-\s+"))
            {
                var text = Regex.Replace(line, @"^\s+-\s+", "").Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    blocks.Add(new ContentBlock(text, IsSubBullet: true));
            }
            else if (Regex.IsMatch(line, @"^-\s+"))
            {
                var text = Regex.Replace(line, @"^-\s+", "").Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    blocks.Add(new ContentBlock(text, IsBullet: true));
            }
            else
            {
                var text = line.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    blocks.Add(new ContentBlock(text));
            }
        }

        return blocks;
    }

    private record ContentBlock(
        string Text,
        bool IsHeading = false,
        bool IsBullet = false,
        bool IsSubBullet = false);
}
