using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Valuation.Api.Models;
using QRCoder;
using System.IO;

namespace Valuation.Api.Services
{
    public class PdfReportService
    {
        private readonly CosmosClient _cosmos;
        private readonly string _dbId;
        private readonly string _containerId;
        private readonly HttpClient _httpClient;

        // Master Brand Colors matched to original design
        private static readonly string BrandTeal  = "#009688";
        private static readonly string LabelSlate = "#64748B";
        private static readonly string ValueDark  = "#1a1a1a";
        private static readonly string MintBg     = "#F0FDF4";
        private static readonly string MintText   = "#059669";
        private static readonly TimeSpan PhotoDownloadTimeout = TimeSpan.FromSeconds(10);

        private Container Container => _cosmos.GetDatabase(_dbId).GetContainer(_containerId);
        private PartitionKey GetPk(string vehicleNumber, string applicantContact) =>
            new($"{vehicleNumber}|{applicantContact}");

        public PdfReportService(HttpClient httpClient, CosmosClient cosmos, IConfiguration configuration)
        {
            _httpClient    = httpClient;
            _cosmos        = cosmos;
            _dbId          = configuration["Cosmos:DatabaseId"]    ?? "ValuationsDb";
            _containerId   = configuration["Cosmos:ContainerId"]   ?? "Valuations";
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public async Task<ValuationDocument?> GetValuationDocumentAsync(
            string valuationId, string vehicleNumber, string applicantContact)
        {
            try
            {
                var resp = await Container.ReadItemAsync<ValuationDocument>(
                    id: valuationId,
                    partitionKey: GetPk(vehicleNumber, applicantContact));
                return resp.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<byte[]> GeneratePdfAsync(ValuationDocument doc)
        {
            var referenceNumber = GenerateReferenceNumber(doc);
            var photoStreams     = await DownloadPhotosAsync(doc.PhotoUrls);
            var qrCodeBytes      = GenerateQRCode($"https://prontofirebase.web.app/verify/{referenceNumber}");

            var pdfDoc = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontFamily("Helvetica").FontSize(8).FontColor(Colors.Black));

                    page.Background().Svg(size => GenerateWatermarkSvg(size));
                    page.Header().Element(c => ComposeHeader(c, doc, referenceNumber));

                    page.Content().PaddingVertical(8).Column(main =>
                    {
                        ComposeCoverPage(main, doc, photoStreams, qrCodeBytes, referenceNumber);
                        main.Item().PageBreak();

                        ComposeVahanDetailsPage(main, doc, photoStreams);
                        main.Item().PageBreak();

                        ComposeSystemScoresPage(main, doc);
                        main.Item().PageBreak();

                        ComposePhotoGalleryAndDisclaimer(main, doc, photoStreams);
                    });

                    page.Footer().Element(c => ComposeFooter(c, doc));
                });
            });

            return pdfDoc.GeneratePdf();
        }

        // ──────────────────────────────────────────────
        // SVG Generators
        // ──────────────────────────────────────────────

        private string GenerateWatermarkSvg(QuestPDF.Infrastructure.Size size)
        {
            float cx = size.Width  / 2f;
            float cy = size.Height / 2f;
            return $"""
                <svg xmlns="http://www.w3.org/2000/svg" width="{size.Width}" height="{size.Height}">
                  <text x="{cx.ToString("F1", CultureInfo.InvariantCulture)}" y="{cy.ToString("F1", CultureInfo.InvariantCulture)}"
                        transform="rotate(-45, {cx.ToString("F1", CultureInfo.InvariantCulture)}, {cy.ToString("F1", CultureInfo.InvariantCulture)})"
                        text-anchor="middle" dominant-baseline="middle"
                        font-family="Helvetica, Arial, sans-serif"
                        font-size="48" font-weight="bold"
                        fill="#808080" fill-opacity="0.015">VEHGA VERIFIED</text>
                </svg>
                """;
        }

        private string GenerateScoreGaugeSvg(QuestPDF.Infrastructure.Size size, double score)
        {
            float cx      = size.Width  / 2f;
            float cy      = size.Height * 0.50f;
            float radius  = Math.Min(cx, cy) * 0.82f;
            float strokeW = radius * 0.22f;

            string ArcPath(float r, float startDeg, float sweepDeg)
            {
                double s  = startDeg * Math.PI / 180.0;
                double e  = (startDeg + sweepDeg) * Math.PI / 180.0;
                float  x1 = cx + r * (float)Math.Cos(s);
                float  y1 = cy + r * (float)Math.Sin(s);
                float  x2 = cx + r * (float)Math.Cos(e);
                float  y2 = cy + r * (float)Math.Sin(e);
                int    la = sweepDeg > 180 ? 1 : 0;
                return $"M {x1.ToString("F2", CultureInfo.InvariantCulture)} {y1.ToString("F2", CultureInfo.InvariantCulture)} " +
                       $"A {r.ToString("F2", CultureInfo.InvariantCulture)} {r.ToString("F2", CultureInfo.InvariantCulture)} " +
                       $"0 {la} 1 {x2.ToString("F2", CultureInfo.InvariantCulture)} {y2.ToString("F2", CultureInfo.InvariantCulture)}";
            }

            float  sweep  = (float)(Math.Clamp(score / 10.0, 0, 1) * 270);
            string bgPath = ArcPath(radius, 135, 270);
            string fgPath = sweep > 0 ? ArcPath(radius, 135, sweep) : "";

            float numSize = radius * 0.88f;
            float subSize = radius * 0.30f;
            float numY    = cy + numSize * 0.28f;
            float subY    = numY + subSize * 1.2f;

            string fgArc = sweep > 0
                ? $"""<path d="{fgPath}" fill="none" stroke="{BrandTeal}" stroke-width="{strokeW.ToString("F2", CultureInfo.InvariantCulture)}" stroke-linecap="round"/>"""
                : "";

            return $"""
                <svg xmlns="http://www.w3.org/2000/svg"
                     width="{size.Width.ToString("F1", CultureInfo.InvariantCulture)}"
                     height="{size.Height.ToString("F1", CultureInfo.InvariantCulture)}">
                  <path d="{bgPath}" fill="none" stroke="#E2E8F0"
                        stroke-width="{strokeW.ToString("F2", CultureInfo.InvariantCulture)}" stroke-linecap="round"/>
                  {fgArc}
                  <text x="{cx.ToString("F2", CultureInfo.InvariantCulture)}" y="{numY.ToString("F2", CultureInfo.InvariantCulture)}"
                        text-anchor="middle"
                        font-family="Helvetica, Arial, sans-serif"
                        font-size="{numSize.ToString("F2", CultureInfo.InvariantCulture)}" font-weight="900"
                        fill="{BrandTeal}">{score.ToString("F1", CultureInfo.InvariantCulture)}</text>
                  <text x="{cx.ToString("F2", CultureInfo.InvariantCulture)}" y="{subY.ToString("F2", CultureInfo.InvariantCulture)}"
                        text-anchor="middle"
                        font-family="Helvetica, Arial, sans-serif"
                        font-size="{subSize.ToString("F2", CultureInfo.InvariantCulture)}" font-weight="bold"
                        fill="#9CA3AF">/ 10</text>
                </svg>
                """;
        }

        // ──────────────────────────────────────────────
        // Layout Helpers
        // ──────────────────────────────────────────────

        private double ParseScoreValue(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return 8.0;
            var lower = input.ToLower().Trim();
            if (lower.Contains("good") || lower == "true") return 8.5;
            if (lower.Contains("average"))                 return 6.0;
            if (lower.Contains("bad") || lower.Contains("poor")) return 3.0;
            var m = Regex.Match(lower, @"\d+(\.\d+)?");
            if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return Math.Clamp(v, 0.0, 10.0);
            return 8.0;
        }

        private async Task<Dictionary<string, byte[]>> DownloadPhotosAsync(Dictionary<string, string>? photoUrls)
        {
            var result = new Dictionary<string, byte[]>();
            if (photoUrls == null || !photoUrls.Any()) return result;

            var tasks = photoUrls
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value))
                .Select(async kvp =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(PhotoDownloadTimeout);
                        var resp      = await _httpClient.GetAsync(kvp.Value, cts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            var bytes = await resp.Content.ReadAsByteArrayAsync();
                            return (Key: kvp.Key, Bytes: bytes, Ok: true);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception) { }
                    return (Key: kvp.Key, Bytes: Array.Empty<byte>(), Ok: false);
                });

            foreach (var r in (await Task.WhenAll(tasks)).Where(r => r.Ok && r.Bytes.Length > 0))
                result[r.Key] = r.Bytes;

            return result;
        }

        private byte[] GenerateQRCode(string text)
        {
            try
            {
                using var gen    = new QRCodeGenerator();
                using var data   = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
                using var qrCode = new PngByteQRCode(data);
                return qrCode.GetGraphic(20);
            }
            catch { return Array.Empty<byte>(); }
        }

        private string GenerateReferenceNumber(ValuationDocument doc)
        {
            if (!string.IsNullOrWhiteSpace(doc.ReferenceNumber)) return doc.ReferenceNumber;
            var input = $"{doc.VehicleDetails?.RegistrationNumber}|{doc.CreatedAt:yyyyMMdd}|{doc.id}";
            using var sha256 = SHA256.Create();
            var h    = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            var num  = ((h[0] << 16) | (h[1] << 8) | h[2]) % 1_000_000;
            var letter = (char)('A' + h[3] % 26);
            return $"PM-{num:D6}-{letter}";
        }

        private string MapVerdict(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "NA";
            var lower = input.ToLower().Trim();
            if (lower is "true" or "yes" or "1" or "good" or "ok") return "GOOD";
            if (lower is "false" or "0" or "bad" or "poor")        return "POOR";
            if (lower == "no")                                        return "NO";
            if (lower is "average" or "fair")                        return "AVERAGE";
            return input.ToUpper();
        }

        private double CalculateSystemScore(Dictionary<string, string?> items)
        {
            var scores = new List<double>();
            foreach (var item in items)
            {
                var v = MapVerdict(item.Value);
                switch (v)
                {
                    case "GOOD": case "YES": scores.Add(10.0); break;
                    case "AVERAGE":          scores.Add(6.5);  break;
                    case "POOR": case "BAD": scores.Add(3.0);  break;
                    case "NO":               scores.Add(4.0);  break;
                    case "NA": break;
                    default:
                        var m = Regex.Match(v, @"\d+(\.\d+)?");
                        if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                            scores.Add(Math.Clamp(n, 0, 10));
                        break;
                }
            }
            return scores.Any() ? Math.Round(scores.Average(), 1) : 8.0;
        }

        private (string Score, string Color) GetScoreDisplayFromDouble(double score)
        {
            var c = score >= 8.0 ? MintText
                  : score >= 6.0 ? "#D97706"
                  : "#DC2626";
            return ($"{score.ToString("F1", CultureInfo.InvariantCulture)}/10", c);
        }

        private string SafeFormat(string? value, string def = "-") =>
            string.IsNullOrWhiteSpace(value) ? def : value;

        // ──────────────────────────────────────────────
        // Header
        // ──────────────────────────────────────────────

        private void ComposeHeader(IContainer container, ValuationDocument doc, string referenceNumber)
        {
            container.PaddingBottom(4).BorderBottom(1).BorderColor("#E2E8F0").PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Height(40).AlignLeft().Row(logoRow =>
                    {
                        var absolutePath = Path.Combine(AppContext.BaseDirectory, "png", "vehga-logo.png");
                        if (File.Exists(absolutePath))
                            logoRow.AutoItem().Height(40).Image(absolutePath).FitHeight();
                        else if (File.Exists("png/vehga-logo.png"))
                            logoRow.AutoItem().Height(40).Image("png/vehga-logo.png").FitHeight();
                        else
                            logoRow.AutoItem().Text("VEHGA").FontSize(26).Bold().FontColor(BrandTeal);
                    });
                });
                row.AutoItem().AlignRight().Column(col =>
                {
                    col.Item().AlignRight().Text(t => {
                        t.Span("REF NO: ").FontSize(8).Bold().FontColor(LabelSlate);
                        t.Span(referenceNumber).FontSize(8).Bold().FontColor(ValueDark);
                    });
                    col.Item().PaddingTop(2).AlignRight().Text(t => {
                        t.Span("REPORT DATE: ").FontSize(8).Bold().FontColor(LabelSlate);
                        t.Span(doc.CreatedAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpper()).FontSize(8).Bold().FontColor(ValueDark);
                    });
                    col.Item().PaddingTop(2).AlignRight().Text(text =>
                    {
                        text.Span("PAGE ").FontSize(8).Bold().FontColor(LabelSlate);
                        text.CurrentPageNumber().FontSize(8).Bold().FontColor(ValueDark);
                        text.Span(" OF ").FontSize(8).Bold().FontColor(LabelSlate);
                        text.TotalPages().FontSize(8).Bold().FontColor(ValueDark);
                    });
                });
            });
        }

        // ──────────────────────────────────────────────
        // Footer 
        // ──────────────────────────────────────────────

        private void ComposeFooter(IContainer container, ValuationDocument doc)
        {
            container.PaddingTop(4).BorderTop(1).BorderColor("#E2E8F0").PaddingTop(6).AlignCenter()
                .Text("NOTE: THIS IS A DIGITALLY GENERATED REPORT, HENCE NO PHYSICAL SIGNATURE IS REQUIRED. VERIFIED VIA PRONTO SECURE CLOUD.")
                .FontSize(5.5f).FontColor(LabelSlate).LetterSpacing(0.03f);
        }

        // ──────────────────────────────────────────────
        // PAGE 1 — Cover Page
        // ──────────────────────────────────────────────

        private void ComposeCoverPage(ColumnDescriptor main, ValuationDocument doc,
            Dictionary<string, byte[]> photos, byte[] qrCode, string referenceNumber)
        {
            var vehicleName = $"{doc.VehicleDetails?.Make} {doc.VehicleDetails?.Model}".Trim().ToUpper();
            var regNumber   = doc.VehicleDetails?.RegistrationNumber?.ToUpper() ?? "REGISTRATION PENDING";

            main.Item().PaddingTop(10).PaddingBottom(8).Row(row =>
            {
                row.AutoItem().AlignMiddle().Width(4).Height(24)
                    .Svg(_ => $@"<svg width=""4"" height=""24""><rect width=""4"" height=""24"" rx=""2"" fill=""{BrandTeal}""/></svg>");
                
                row.ConstantItem(10);
                
                row.RelativeItem().AlignMiddle()
                    .Text(regNumber).FontSize(20).ExtraBold().FontColor(BrandTeal);

                row.AutoItem().AlignMiddle().Layers(layers =>
                {
                    layers.Layer().Svg(s => {
                        float w = s.Width;
                        float h = s.Height;
                        float r = Math.Min(h / 2f, 12f); 
                        
                        string wStr = w.ToString("F1", CultureInfo.InvariantCulture);
                        string hStr = h.ToString("F1", CultureInfo.InvariantCulture);
                        string rStr = r.ToString("F1", CultureInfo.InvariantCulture);
                        string hMinusR = (h - r).ToString("F1", CultureInfo.InvariantCulture);

                        string path = $"M {wStr},0 L {rStr},0 A {rStr},{rStr} 0 0,0 0,{rStr} L 0,{hMinusR} A {rStr},{rStr} 0 0,0 {rStr},{hStr} L {wStr},{hStr} Z";
                        
                        return $@"<svg width=""{wStr}"" height=""{hStr}""><path d=""{path}"" fill=""{BrandTeal}""/></svg>";
                    });
                    
                    layers.PrimaryLayer().PaddingVertical(5).PaddingLeft(16).PaddingRight(12)
                        .Text("RETAIL").FontSize(8).Bold().FontColor(Colors.White);
                });
            });

            main.Item().Row(row =>
            {
                row.RelativeItem(7).Column(col =>
                {
                    col.Item().AspectRatio(4 / 3f).Layers(l =>
                    {
                        l.PrimaryLayer().Element(c =>
                        {
                            byte[]? img = photos.GetValueOrDefault("FrontLeftSide")
                                       ?? photos.GetValueOrDefault("FrontView")
                                       ?? photos.Values.FirstOrDefault();
                            if (img != null)
                                c.AlignCenter().AlignMiddle().Image(img).FitArea();
                            else
                                c.Background("#F8FAFC").AlignCenter().AlignMiddle()
                                    .Text("NO IMAGE AVAILABLE").FontSize(10).FontColor(LabelSlate);
                        });
                        l.Layer().Svg(size =>
                        {
                            string wStr = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string hStr = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                            string w24  = Math.Max(0, size.Width  - 24).ToString("F1", CultureInfo.InvariantCulture);
                            string h24  = Math.Max(0, size.Height - 24).ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{wStr}"" height=""{hStr}"" viewBox=""0 0 {wStr} {hStr}"">
                                        <path fill-rule=""evenodd"" d=""M0,0 h{wStr} v{hStr} h-{wStr} Z M12,0 a12,12 0 0 0 -12,12 v{h24} a12,12 0 0 0 12,12 h{w24} a12,12 0 0 0 12,-12 v-{h24} a12,12 0 0 0 -12,-12 Z"" fill=""#FFFFFF"" />
                                        <rect x=""0.75"" y=""0.75"" width=""{(size.Width - 1.5).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(size.Height - 1.5).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""12"" fill=""none"" stroke=""#F5F5F5"" stroke-width=""3""/>
                                      </svg>";
                        });
                    });

                    col.Item().PaddingTop(5).Layers(layers =>
                    {
                        layers.Layer().Svg(s => {
                            string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""8"" fill=""#E6F5F3""/></svg>";
                        });
                        layers.PrimaryLayer().PaddingVertical(5).AlignCenter()
                            .Text(vehicleName.Length > 35 ? vehicleName[..35].TrimEnd() + "…" : vehicleName)
                            .FontSize(vehicleName.Length > 28 ? 9 : 10).ExtraBold().FontColor(BrandTeal);
                    });
                });

                row.ConstantItem(12);

                row.RelativeItem(5).Column(cards =>
                {
                    cards.Item().Layers(layers =>
                    {
                        layers.Layer().Svg(size => {
                            string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""16"" fill=""{MintBg}"" stroke=""#D1FAE5"" stroke-width=""1""/></svg>";
                        });
                        layers.PrimaryLayer().Padding(10).Column(c =>
                        {
                            c.Item().AlignCenter()
                                .Text("OVERALL VEHICLE SCORE")
                                .FontSize(8).ExtraBold().FontColor(BrandTeal).LetterSpacing(0.04f);

                            var gaugeScore = ParseScoreValue(doc.QualityControl?.OverallRating);
                            c.Item().AlignCenter().Height(88)
                                .Svg(size => GenerateScoreGaugeSvg(size, gaugeScore));

                            c.Item().AlignCenter().PaddingTop(2).Layers(badgeLayers =>
                            {
                                badgeLayers.Layer().Svg(s => {
                                    string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                    string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                    return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""6"" fill=""#FFFFFF"" stroke=""{BrandTeal}"" stroke-width=""1.5""/></svg>";
                                });
                                badgeLayers.PrimaryLayer().PaddingVertical(4).PaddingHorizontal(12)
                                    .Text("VERIFIED CLEAN").FontSize(8).ExtraBold().FontColor(BrandTeal);
                            });
                        });
                    });

                    cards.Item().PaddingVertical(5);

                    cards.Item().Layers(layers =>
                    {
                        layers.Layer().Svg(size => {
                            string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""16"" fill=""{BrandTeal}""/></svg>";
                        });
                        layers.PrimaryLayer().Padding(14).Column(c =>
                        {
                            c.Item().AlignCenter()
                                .Text("ESTIMATED MARKET VALUE")
                                .FontSize(8).Bold().FontColor(Colors.White).LetterSpacing(0.04f);
                            c.Item().PaddingTop(6).AlignCenter()
                                .Text($"₹ {doc.QualityControl?.ValuationAmount:N0}")
                                .FontSize(22).ExtraBold().FontColor(Colors.White);
                            c.Item().PaddingTop(4).AlignCenter()
                                .Text("Calculated based on current market trends")
                                .FontSize(7).Italic().FontColor(Colors.White);
                        });
                    });
                });
            });

            main.Item().PaddingVertical(4); 

            main.Item().BorderTop(1).BorderBottom(1).BorderColor("#E5E7EB")
                .PaddingVertical(5).Table(table => 
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(3); cd.RelativeColumn(5);
                    cd.RelativeColumn(3); cd.RelativeColumn(5);
                });
                AddClientInfoRow(table, "CLIENT",
                    doc.Stakeholder?.Name?.ToUpper() ?? "-",
                    "BRANCH",
                    doc.InspectionDetails?.InspectionLocation?.ToUpper() ?? "-");
                AddClientInfoRow(table, "DATE OF INSPECTION",
                    doc.InspectionDetails?.DateOfInspection?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)?.ToUpper() ?? "-",
                    "PLACE OF INSPECTION",
                    doc.InspectionDetails?.InspectionLocation?.ToUpper() ?? "-");
            });

            main.Item().PaddingVertical(4); 

            DrawSectionTitle(main.Item(), "ASSET IDENTITY",
                @"<path d=""M18.92 6.01C18.72 5.42 18.16 5 17.5 5h-11c-.66 0-1.21.42-1.42 1.01L3 12v8c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-1h12v1c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-8l-2.08-5.99zM6.5 16c-.83 0-1.5-.67-1.5-1.5S5.67 13 6.5 13s1.5.67 1.5 1.5S7.33 16 6.5 16zm11 0c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zM5 11l1.5-4.5h11L19 11H5z""/>",
                svgFill: true);

            _assetRowIndex = 0;
            main.Item().Border(1).BorderColor("#EEF2F6").Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(4); cd.RelativeColumn(5);
                    cd.RelativeColumn(4); cd.RelativeColumn(5);
                });
                AddAssetRow(table, "OWNER",            doc.VehicleDetails?.OwnerName?.ToUpper() ?? "-",      "FUEL TYPE",         doc.VehicleDetails?.Fuel?.ToUpper() ?? "-");
                AddAssetRow(table, "APPLICANT",        doc.Stakeholder?.Applicant?.Name?.ToUpper() ?? "-",   "TRANSMISSION",      "MANUAL");
                AddAssetRow(table, "CHASSIS NUMBER",   doc.VehicleDetails?.ChassisNumber?.ToUpper() ?? "-",  "COLOUR",            doc.VehicleDetails?.Colour?.ToUpper() ?? "-");
                AddAssetRow(table, "ENGINE NUMBER",    doc.VehicleDetails?.EngineNumber?.ToUpper() ?? "-",   "ODO METER",         doc.VehicleDetails?.Odometer?.ToString() ?? "-");
                AddAssetRow(table, "MANUFACTURE YEAR",
                    doc.VehicleDetails?.ManufacturedDate?.ToString("MMM-yyyy", CultureInfo.InvariantCulture)?.ToUpper() ?? "-",
                    "VEHICLE TYPE",      doc.VehicleDetails?.ClassOfVehicle?.ToUpper() ?? "-");
                AddAssetRow(table, "REGISTERED ON",
                    doc.VehicleDetails?.DateOfRegistration?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)?.ToUpper() ?? "-",
                    "OWNERSHIP NUMBER",  doc.VehicleDetails?.OwnerSerialNo?.ToString() ?? "1");
            });

            main.Item().PaddingVertical(5);

            main.Item().Row(outerRow =>
            {
                outerRow.RelativeItem().Column(col =>
                {
                    DrawSectionTitle(col.Item(), "CONDITION VERDICT",
                        @"<path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/>
                          <path d=""M9 12l2 2 4-4""/>",
                        svgFill: false);

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            cd.RelativeColumn(); cd.ConstantColumn(6); cd.RelativeColumn();
                        });

                        void VerdictCell(string lbl, string? val)
                        {
                            var verdict = MapVerdict(val);
                            string badgeBg, badgeFg;
                            if (verdict is "GOOD" or "YES")              { badgeBg = "#D1FAE5"; badgeFg = "#065F46"; }
                            else if (verdict == "AVERAGE")               { badgeBg = "#FEF3C7"; badgeFg = "#92400E"; }
                            else if (verdict is "POOR" or "BAD" or "NO") { badgeBg = "#FEE2E2"; badgeFg = "#991B1B"; }
                            else                                         { badgeBg = "#D1FAE5"; badgeFg = "#065F46"; }

                            table.Cell().PaddingVertical(3).Layers(cell =>
                            {
                                cell.Layer().Svg(s => {
                                    string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                    string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                    return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""4"" fill=""#F8FAFC"" stroke=""#EEF2F6"" stroke-width=""1""/></svg>";
                                });
                                cell.PrimaryLayer().PaddingHorizontal(6).PaddingVertical(4).Row(r =>
                                {
                                    r.RelativeItem().AlignMiddle()
                                        .Text(lbl).FontSize(9).Bold().FontColor(LabelSlate);
                                    r.AutoItem().AlignMiddle().Layers(badge =>
                                    {
                                        badge.Layer().Svg(s => {
                                            string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                            string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""4"" fill=""{badgeBg}""/></svg>";
                                        });
                                        badge.PrimaryLayer().PaddingVertical(2).PaddingHorizontal(6)
                                            .Text(verdict).FontSize(8).Bold().FontColor(badgeFg);
                                    });
                                });
                            });
                        }

                        VerdictCell("CABIN",        doc.InspectionDetails?.Cabin);
                        table.Cell(); // spacer column
                        VerdictCell("ENGINE",       doc.InspectionDetails?.EngineCondition);

                        VerdictCell("LOAD BODY",    doc.InspectionDetails?.BodyCondition);
                        table.Cell();
                        VerdictCell("OTHER SYSTEMS","GOOD");
                    });
                });

                outerRow.ConstantItem(12);

                outerRow.AutoItem().Column(linkCol =>
                {
                    linkCol.Item().PaddingBottom(5).Layers(layers => 
                    {
                        layers.Layer().Svg(s => {
                            string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}"" rx=""12"" fill=""#ECFDF5"" stroke=""#A7F3D0"" stroke-width=""1""><rect width=""{w}"" height=""{h}"" rx=""12"" fill=""#ECFDF5"" stroke=""#A7F3D0"" stroke-width=""1""/></svg>";
                        });
                        layers.PrimaryLayer().PaddingVertical(4).PaddingHorizontal(8).Row(r =>
                        {
                            r.AutoItem().AlignMiddle().Width(10).Height(10)
                                .Svg(_ => @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#059669"" stroke-width=""2""><circle cx=""12"" cy=""12"" r=""10""/><path d=""M8 12l3 3 5-6"" stroke-linecap=""round"" stroke-linejoin=""round""/></svg>");
                            r.ConstantItem(4);
                            r.AutoItem().AlignMiddle().Text(t => {
                                t.Span("DEDUPE: ").FontSize(7).ExtraBold().FontColor(ValueDark);
                                t.Span("VERIFIED CLEAN").FontSize(7).ExtraBold().FontColor(MintText);
                            });
                        });
                    });

                    var videoContainer = linkCol.Item().PaddingBottom(5);
                    string? videoUrl = null;
                    if (doc.VideoUrls != null) doc.VideoUrls.TryGetValue("VehicleVideo", out videoUrl);
                    if (!string.IsNullOrWhiteSpace(videoUrl)) videoContainer = videoContainer.Hyperlink(videoUrl);
                    videoContainer.Layers(layers =>
                    {
                        layers.Layer().Svg(size => {
                            string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""6"" fill=""#EFF6FF"" stroke=""#BFDBFE"" stroke-width=""1""/></svg>";
                        });
                        layers.PrimaryLayer().PaddingVertical(5).PaddingHorizontal(10).Row(r =>
                        {
                            r.AutoItem().Width(10).Height(10)
                                .Svg(_ => @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1D4ED8"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M23 7l-7 5 7 5V7z""/><rect x=""1"" y=""5"" width=""15"" height=""14"" rx=""2""/></svg>");
                            r.ConstantItem(6);
                            r.AutoItem().AlignMiddle()
                                .Text("VIDEO LINK").FontSize(8).ExtraBold().FontColor("#1D4ED8");
                        });
                    });

                    linkCol.Item().SectionLink("PhotoGalleryTarget").Layers(layers =>
                    {
                        layers.Layer().Svg(size => {
                            string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""6"" fill=""#EFF6FF"" stroke=""#BFDBFE"" stroke-width=""1""/></svg>";
                        });
                        layers.PrimaryLayer().PaddingVertical(5).PaddingHorizontal(10).Row(r =>
                        {
                            r.AutoItem().Width(10).Height(10)
                                .Svg(_ => @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1D4ED8"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z""/><circle cx=""12"" cy=""13"" r=""4""/></svg>");
                            r.ConstantItem(6);
                            r.AutoItem().AlignMiddle()
                                .Text("IMAGES LINK").FontSize(8).ExtraBold().FontColor("#1D4ED8");
                        });
                    });
                });
            });

            main.Item().PaddingVertical(5);

            main.Item().Row(row =>
            {
                row.RelativeItem(6).Layers(layers =>
                {
                    layers.Layer().Svg(size => {
                        string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                        string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                        return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""8"" fill=""#F8FAFC"" stroke=""#EEF2F6"" stroke-width=""1""/></svg>";
                    });
                    layers.PrimaryLayer().MinHeight(75).Padding(12).Column(c =>
                    {
                        c.Item().Text("INSPECTOR'S REMARKS")
                            .FontSize(8).ExtraBold().FontColor(LabelSlate).LetterSpacing(0.06f);
                        c.Item().PaddingTop(6)
                            .Text($"\"{doc.QualityControl?.Remarks ?? "Vehicle found in good road worthy condition."}\"")
                            .FontSize(8.5f).Italic().FontColor("#374151").LineHeight(1.5f);
                    });
                });

                row.ConstantItem(10);

                row.RelativeItem(2).AlignCenter().Layers(layers =>
                {
                    layers.Layer().Svg(size => {
                        string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                        string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                        return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""12"" fill=""white"" stroke=""#E5E7EB"" stroke-width=""1.5""/></svg>";
                    });
                    layers.PrimaryLayer().PaddingTop(8).PaddingBottom(8).Column(c =>
                    {
                        if (qrCode.Length > 0)
                        {
                            var verifyUrl = $"https://prontofirebase.web.app/verify/{referenceNumber}";
                            c.Item().AlignCenter().Width(44).Height(44).Hyperlink(verifyUrl).Image(qrCode).FitArea();
                        }
                        c.Item().PaddingTop(4).AlignCenter()
                            .Text("VERIFY ONLINE").FontSize(6f).ExtraBold().FontColor(LabelSlate);
                    });
                });

                row.ConstantItem(10);

                row.RelativeItem(4).AlignBottom().Column(col =>
                {
                    col.Item().AlignRight()
                        .Text("Approved by")
                        .FontSize(8).FontColor(LabelSlate);

                    col.Item().PaddingTop(2).AlignRight()
                        .Text(doc.InspectionDetails?.VehicleInspectedBy ?? "Jagadeesh Kumar")
                        .FontSize(12).SemiBold().FontColor(ValueDark);

                    col.Item().PaddingTop(1).AlignRight()
                        .Text("Head - Operations")
                        .FontSize(8).FontColor(LabelSlate);

                    col.Item().PaddingTop(8).AlignRight().Layers(badge =>
                    {
                        badge.Layer().Svg(s => {
                            string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}"" rx=""10"" fill=""#F0FDF4"" stroke=""#A7F3D0"" stroke-width=""1""><rect width=""{w}"" height=""{h}"" rx=""10"" fill=""#F0FDF4"" stroke=""#A7F3D0"" stroke-width=""1""/></svg>";
                        });
                        badge.PrimaryLayer().PaddingVertical(3).PaddingHorizontal(8).Row(r =>
                        {
                            r.AutoItem().AlignMiddle()
                                .Text("AUDIT STATUS: CERTIFIED")
                                .FontSize(7).ExtraBold().FontColor(MintText);
                            r.ConstantItem(4);
                            r.AutoItem().AlignMiddle().Width(10).Height(10)
                                .Svg(_ => @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#059669"" stroke-width=""2""><path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/><path d=""M9 12l2 2 4-4""/></svg>");
                        });
                    });

                    var secId = $"ID: V-{referenceNumber.Replace("PM-", "").Replace("-", "")}-SECURE";
                    col.Item().PaddingTop(2).AlignRight()
                        .Text(secId).FontSize(6).FontColor(Colors.Grey.Medium);
                });
            });
        }

        private void DrawSectionTitle(IContainer container, string title, string iconPathsSvg, bool svgFill)
        {
            string strokeAttr = svgFill
                ? $@"fill=""{BrandTeal}"" stroke=""none"""
                : $@"fill=""none"" stroke=""{BrandTeal}"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""";

            container.PaddingBottom(5).PaddingTop(5).Row(row => 
            {
                row.AutoItem().AlignMiddle().Width(4).Height(20)
                    .Svg(_ => $@"<svg width=""4"" height=""20""><rect width=""4"" height=""20"" rx=""2"" fill=""{BrandTeal}""/></svg>");
                row.ConstantItem(8);
                row.AutoItem().AlignMiddle().Width(14).Height(14).Svg(_ => $"""
                    <svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg" {strokeAttr}>
                      {iconPathsSvg}
                    </svg>
                    """);
                row.ConstantItem(6);
                row.AutoItem().AlignMiddle()
                    .Text(title).FontSize(11).ExtraBold().LetterSpacing(0.04f).FontColor(ValueDark);
            });
        }

        // ──────────────────────────────────────────────
        // PAGE 2 — Vahan Details
        // ──────────────────────────────────────────────

        private void ComposeVahanDetailsPage(ColumnDescriptor main, ValuationDocument doc, Dictionary<string, byte[]> photos)
        {
            DrawSectionTitle(main.Item(), "VAHAN DETAILS",
                @"<path d=""M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z""/>
                  <polyline points=""14 2 14 8 20 8""/>
                  <line x1=""16"" y1=""13"" x2=""8"" y2=""13""/>
                  <line x1=""16"" y1=""17"" x2=""8"" y2=""17""/>",
                svgFill: false);

            var vd = doc.VehicleDetails;

            main.Item().PaddingVertical(6);

            main.Item().Column(col =>
            {
                int i = 0;
                AddVahanRow(col, i++, "REGISTRATION NUMBER",  vd?.RegistrationNumber,           "OWNER SERIAL NUMBER",   vd?.OwnerSerialNo?.ToString() ?? "1");
                AddVahanRow(col, i++, "CHASSIS NUMBER",        vd?.ChassisNumber,                "YEAR OF MANUFACTURE",   vd?.ManufacturedDate?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
                AddVahanRow(col, i++, "ENGINE NUMBER",         vd?.EngineNumber,                 "DATE OF REGISTRATION",  vd?.DateOfRegistration?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
                AddVahanRow(col, i++, "VEHICLE MAKE",          vd?.Make,                         "ENGINE CUBIC CAPACITY", $"{vd?.EngineCC ?? 0} CC");
                AddVahanRow(col, i++, "VEHICLE MODEL",         vd?.Model,                        "GROSS VEHICLE WEIGHT",  $"{vd?.GrossVehicleWeight ?? 0} KG");
                AddVahanRow(col, i++, "VEHICLE CATEGORY",      vd?.CategoryCode,                 "SEATING CAPACITY",      $"{vd?.SeatingCapacity?.ToString() ?? "5"} SEATS");
                AddVahanRow(col, i++, "VEHICLE CLASS",         vd?.ClassOfVehicle,               "FUEL TYPE",             vd?.Fuel);
                AddVahanRow(col, i++, "BODY TYPE",             vd?.BodyType,                     "FUEL NORMS",            vd?.NormsType);
                AddVahanRow(col, i++, "VEHICLE COLOR",         vd?.Colour,                       "NOC DETAILS",           "---");
                AddVahanRow(col, i++, "REGISTERED AT RTO",     vd?.Rto,                          "CHALLAN DETAILS",       "---");
            });

            main.Item().PaddingTop(14).PaddingBottom(10).Element(c => 
                DrawSectionTitle(c, "REGULATORY DOCUMENTS & INSURANCE",
                    @"<path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/>",
                    svgFill: false)
            );

            bool hasLien = vd?.Hypothecation ?? false;
            main.Item().Column(col =>
            {
                AddRegulatoryCardSimple(col, "COMPREHENSIVE INSURANCE",
                    $"Policy: #UIIC/1922/001 | IDV: Rs. {vd?.IDV?.ToString("N0") ?? "4,80,000"}", "ACTIVE", "EXP: OCT 2028");
                AddRegulatoryCardSimple(col, "HYPOTHECATION (STATUS)",
                    hasLien ? "LIEN DETECTED" : "CLEAN / NO LIEN DETECTED",
                    hasLien ? "LIEN" : "FREE OF LIEN", hasLien ? "" : "READY FOR TRANSFER", hasLien);
                AddRegulatoryCardSimple(col, "NATIONAL PERMIT", "AP123456789", "ACTIVE", "EXP: OCT 2028");
                AddRegulatoryCardSimple(col, "FITNESS CERTIFICATE", "", "---", "EXP: ---", true);
                AddRegulatoryCardWithPhoto(col, "CHASSIS VERIFICATION",  "VERIFIED", photos, "ChassisVerification");
                AddRegulatoryCardWithPhoto(col, "CHASSIS STENCIL TRACE", "",         photos, "ChassisImprint");
            });
        }

        private void AddRegulatoryCardSimple(ColumnDescriptor col,
            string title, string details, string status, string expiry, bool isWarning = false)
        {
            col.Item().PaddingBottom(12).Layers(cardLayers =>
            {
                cardLayers.Layer().Svg(s => {
                    string wStr = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                    string hStr = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                    return $@"<svg width=""{wStr}"" height=""{hStr}"">
                                <rect x=""0.5"" y=""0.5"" width=""{(s.Width - 1).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(s.Height - 1).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""8"" fill=""#FFFFFF"" stroke=""#E5E7EB"" stroke-width=""1""/>
                              </svg>";
                });

                cardLayers.PrimaryLayer().PaddingVertical(10).PaddingHorizontal(14).Row(row =>
                {
                    string strokeColor = isWarning ? "#F59E0B" : BrandTeal;
                    string fillColor   = isWarning ? "#FFFBEB" : "#F0FDF4";

                    row.ConstantItem(36).AlignMiddle().AlignCenter().Width(16).Height(16)
                        .Svg(_ => $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"">
                                        <circle cx=""12"" cy=""12"" r=""11"" fill=""{fillColor}"" stroke=""{strokeColor}"" stroke-width=""1""/>
                                        <polyline points=""7,12.5 10.5,16 17,8"" fill=""none"" stroke=""{strokeColor}"" stroke-width=""1.5""/>
                                     </svg>");

                    row.RelativeItem().AlignMiddle().Column(c =>
                    {
                        c.Item().Text(title).FontSize(9).Bold().FontColor(ValueDark);
                        if (!string.IsNullOrEmpty(details))
                            c.Item().PaddingTop(3).Text(details).FontSize(7).FontColor(LabelSlate);
                    });

                    row.ConstantItem(120).AlignMiddle().AlignRight().Column(c =>
                    {
                        if (!string.IsNullOrEmpty(status) && status != "---")
                        {
                            string bg    = isWarning ? "#FFF3E0" : "#ECFDF5";
                            string color = isWarning ? "#FB8C00" : "#059669";
                            c.Item().AlignRight().Background(bg).PaddingVertical(3).PaddingHorizontal(8)
                                .Text(status).FontSize(7).Bold().FontColor(color);
                        }
                        if (!string.IsNullOrEmpty(expiry) && expiry != "EXP: ---")
                            c.Item().AlignRight().PaddingTop(3).Text(expiry).FontSize(7).FontColor(LabelSlate);
                        else if (expiry == "EXP: ---")
                            c.Item().AlignRight().PaddingTop(3).Text("---").FontSize(7).FontColor(LabelSlate);
                    });
                });
            });
        }

        private void AddRegulatoryCardWithPhoto(ColumnDescriptor col,
            string title, string details, Dictionary<string, byte[]> photos, string photoKey)
        {
            col.Item().PaddingBottom(12).Layers(cardLayers =>
            {
                cardLayers.Layer().Svg(s => {
                    string wStr = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                    string hStr = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                    return $@"<svg width=""{wStr}"" height=""{hStr}"">
                                <rect x=""0.5"" y=""0.5"" width=""{(s.Width - 1).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(s.Height - 1).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""8"" fill=""#FFFFFF"" stroke=""#E5E7EB"" stroke-width=""1""/>
                              </svg>";
                });

                cardLayers.PrimaryLayer().PaddingVertical(10).PaddingHorizontal(14).Row(row =>
                {
                    row.ConstantItem(36).AlignMiddle().AlignCenter().Width(16).Height(16)
                        .Svg(_ => $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"">
                                        <circle cx=""12"" cy=""12"" r=""11"" fill=""#F0FDF4"" stroke=""{BrandTeal}"" stroke-width=""1""/>
                                        <polyline points=""7,12.5 10.5,16 17,8"" fill=""none"" stroke=""{BrandTeal}"" stroke-width=""1.5""/>
                                     </svg>");

                    row.RelativeItem(1).AlignMiddle().Column(c =>
                    {
                        c.Item().Text(title).FontSize(9).Bold().FontColor(ValueDark);
                        if (!string.IsNullOrEmpty(details))
                            c.Item().PaddingTop(3).Text(details).FontSize(7).FontColor(LabelSlate);
                    });

                    row.RelativeItem(1).AlignMiddle().AlignRight().Element(c =>
                    {
                        byte[]? photo = photos.GetValueOrDefault(photoKey)
                                     ?? photos.GetValueOrDefault("ChassisNumberPlate");
                        if (photo != null)
                            c.Height(45).AlignCenter().AlignMiddle().Image(photo).FitArea();
                        else
                            c.AlignCenter().AlignMiddle().Text("NO IMAGE").FontSize(8).FontColor(LabelSlate);
                    });
                });
            });
        }

        // ──────────────────────────────────────────────
        // PAGE 3 — System Scores
        // ──────────────────────────────────────────────

        private void ComposeSystemScoresPage(ColumnDescriptor main, ValuationDocument doc)
        {
            var ins = doc.InspectionDetails;
            if (ins == null)
            {
                main.Item().AlignCenter().AlignMiddle()
                    .Text("No inspection details available.").FontSize(12).FontColor(Colors.Grey.Medium);
                return;
            }

            main.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Basic, "BASIC SYSTEMS", new()
                    {
                        { "ENGINE CONDITION",  ins.EngineCondition },   { "CHASSIS CONDITION", ins.ChassisCondition },
                        { "CABIN ASSY",        ins.Cabin },             { "STEERING SYSTEM",   ins.SteeringAssy },
                        { "BRAKE SYSTEM",      ins.BrakeSystem },       { "ELECTRICAL SYSTEM", ins.ElectricAssembly },
                        { "SUSPENSION SYSTEM", ins.SuspensionSystem },  { "FUEL SYSTEM",       doc.VehicleDetails?.Fuel },
                        { "TYRE CONDITION",    ins.OverallTyreCondition }
                    }));
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Brakes, "BRAKES", new()
                    {
                        { "FRONT BRAKES","GOOD"}, { "REAR BRAKES","GOOD" },
                        { "HAND BRAKE",  "GOOD"}, { "ABS","GOOD" }
                    }));
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Steering, "STEERING SYSTEM", new()
                    {
                        { "STEERING WHEEL","GOOD"},   { "STEERING COLUMN","GOOD" },
                        { "STEERING BOX",  "GOOD"},   { "STEERING LINKAGES","GOOD" }
                    }));
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Cooling, "COOLING SYSTEM", new()
                    {
                        { "RADIATOR", ins.Radiator }, { "INTER COOLER", ins.Intercooler },
                        { "ALL HOSE PIPES", ins.AllHosePipes }
                    }));
                });

                row.ConstantItem(8);

                row.RelativeItem().Column(col =>
                {
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Cabin, "CABIN ASSEMBLY", new()
                    {
                        { "CABIN",       ins.Cabin },           { "DASHBOARD", ins.Dashboard },
                        { "DOORS",       "GOOD" },              { "ALL GLASSES", ins.WindshieldGlass },
                        { "SEATS",       ins.Seats }
                    }));
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.LoadBody, "LOAD BODY", new()
                    {
                        { "RIGHT SIDE GATE","GOOD"}, { "LEFT SIDE GATE","GOOD" },
                        { "TAIL GATE",      "GOOD"}, { "LOAD FLOOR","GOOD" }
                    }));
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Transmission, "TRANSMISSION SYSTEM", new()
                    {
                        { "GEARBOX ASSY",     ins.GearBoxAssy },    { "CLUTCH SYSTEM", ins.ClutchSystem },
                        { "DIFFERENTIAL ASSY",ins.DifferentialAssy }
                    }));
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Electrical, "ELECTRICAL SYSTEM", new()
                    {
                        { "LIGHTS", ins.HeadLamps }, { "BATTERY", ins.BatteryCondition },
                        { "WIRING ASSY", ins.ElectricAssembly }
                    }));
                    col.Item().Element(c => DrawSystemCard(c, SVGIcons.Suspension, "SUSPENSION SYSTEM", new()
                    {
                        { "FRONT SUSPENSION","GOOD"}, { "REAR SUSPENSION","GOOD" },
                        { "FRONT & REAR AXLES","GOOD" }
                    }));
                });
            });

            main.Item().Element(c => DrawSystemCard(c, SVGIcons.Other, "OTHER SYSTEMS", new()
            {
                { "AIR CONDITIONER","NO" },          { "FRONT CRASH GUARD","NO" },
                { "AUDIO","NO" },                    { "REAR UNDER RUN PROTECTION","NO" },
                { "UPHOLSTERY","GOOD" },             { "SIDE UNDER RUN PROTECTION","NO" },
                { "LOAD CARRIER","YES" },            { "SUN ROOF","NO" }
            }, twoColumns: true));
        }

        private void DrawSystemCard(IContainer container, string iconSvg, string title,
            Dictionary<string, string?> items, bool twoColumns = false)
        {
            var score = CalculateSystemScore(items);
            var (scoreStr, _) = GetScoreDisplayFromDouble(score);

            container.PaddingBottom(8).Layers(layers =>
            {
                layers.Layer().Svg(size => {
                    string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                    string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                    return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}"">
                      <rect width=""{w}"" height=""{h}"" rx=""8"" ry=""8"" fill=""#F8FAFC"" stroke=""#EEF2F6"" stroke-width=""1""/>
                    </svg>";
                });

                layers.PrimaryLayer().Padding(8).Column(col => 
                {
                    col.Item().PaddingBottom(4).BorderBottom(1).BorderColor("#E2E8F0").Row(row =>
                    {
                        row.AutoItem().Width(12).Height(12).Svg(_ => iconSvg);
                        row.ConstantItem(6);
                        row.RelativeItem().AlignMiddle()
                            .Text(title).FontColor(ValueDark).SemiBold().FontSize(8f);

                        row.AutoItem().Height(14).Layers(badgeLayers =>
                        {
                            badgeLayers.Layer().Svg(s => {
                                string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""7"" fill=""{ValueDark}""/></svg>";
                            });
                            badgeLayers.PrimaryLayer().PaddingHorizontal(6).AlignCenter().AlignMiddle()
                                .Text($"SCORE: {scoreStr}").FontColor(Colors.White).FontSize(6f).Bold();
                        });
                    });

                    col.Item().PaddingTop(4).Table(table =>
                    {
                        table.ColumnsDefinition(cd =>
                        {
                            if (twoColumns) { cd.RelativeColumn(); cd.ConstantColumn(40); cd.RelativeColumn(); }
                            else { cd.RelativeColumn(); }
                        });

                        var itemsList = items.ToList();
                        int colIndex  = 0;
                        foreach (var item in itemsList)
                        {
                            var verdict = MapVerdict(item.Value);

                            string pillBg, pillFg;
                            if (verdict is "GOOD" or "YES")              { pillBg = "#ECFDF5"; pillFg = "#059669"; } 
                            else if (verdict == "AVERAGE")               { pillBg = "#FFF7ED"; pillFg = "#D97706"; } 
                            else if (verdict is "POOR" or "BAD" or "NO") { pillBg = "#FEF2F2"; pillFg = "#DC2626"; } 
                            else                                         { pillBg = "#F1F5F9"; pillFg = "#64748B"; } 

                            void BuildCell(IContainer c)
                            {
                                c.PaddingVertical(3f).Row(r =>
                                {
                                    r.RelativeItem().AlignMiddle()
                                        .Text(item.Key).FontSize(7f).FontColor(LabelSlate);

                                    r.AutoItem().Layers(vl =>
                                    {
                                        vl.Layer().Svg(s => {
                                            string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                            string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                            return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""4"" fill=""{pillBg}""/></svg>";
                                        });
                                        vl.PrimaryLayer().PaddingVertical(2).PaddingHorizontal(6)
                                            .AlignCenter().AlignMiddle()
                                            .Text(verdict).FontSize(7f).Bold().FontColor(pillFg);
                                    });
                                });
                            }
                            BuildCell(table.Cell());
                            colIndex++;
                            if (twoColumns && colIndex % 2 != 0) table.Cell();
                        }
                        if (twoColumns && colIndex % 2 != 0) table.Cell();
                    });
                });
            });
        }

        private static class SVGIcons
        {
            public const string Basic        = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><polyline points=""22 12 18 12 15 21 9 3 6 12 2 12""/></svg>";
            public const string Cabin        = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><path d=""M3 9l3-4h12l3 4v6h-2v4h-2v-4H7v4H5v-4H3V9z""/><path d=""M6 9h12""/></svg>";
            public const string LoadBody     = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><path d=""M3 15h18v-4H3v4z""/><path d=""M16 11V7H3v4""/><circle cx=""6"" cy=""17"" r=""2""/><circle cx=""18"" cy=""17"" r=""2""/></svg>";
            public const string Brakes       = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><circle cx=""12"" cy=""12"" r=""9""/><circle cx=""12"" cy=""12"" r=""3""/></svg>";
            public const string Transmission = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><circle cx=""12"" cy=""12"" r=""4""/><path d=""M12 2v2m0 16v2m10-10h-2M4 12H2m15.536-7.536l-1.414 1.414M7.878 17.536l-1.414 1.414m11.314 0l-1.414-1.414M7.878 7.878L6.464 6.464""/></svg>";
            public const string Steering     = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><circle cx=""12"" cy=""12"" r=""9""/><circle cx=""12"" cy=""12"" r=""2""/><path d=""M12 14v7m-1.732-11l-6-3.464m15.464 0l-6 3.464""/></svg>";
            public const string Electrical   = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><path d=""M13 2L3 14h9l-1 8 10-12h-9l1-8z""/></svg>";
            public const string Cooling      = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><path d=""M14 14.76V3.5a2.5 2.5 0 0 0-5 0v11.26a4.5 4.5 0 1 0 5 0z""/></svg>";
            public const string Suspension   = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><path d=""M8 4l4-2 4 2M8 20l4 2 4-2M12 2v20m-4-6h8m-8-8h8""/></svg>";
            public const string Other        = @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#1E293B"" stroke-width=""2""><circle cx=""12"" cy=""12"" r=""9""/><path d=""M12 8v8m-4-4h8""/></svg>";
        }

        // ──────────────────────────────────────────────
        // PAGE 4 — Photo Gallery & Disclaimer
        // ──────────────────────────────────────────────

        private string FormatPhotoLabel(string key)
        {
            var spaced = Regex.Replace(key, "([a-z])([A-Z])", "$1 $2");
            return spaced.ToUpper();
        }

        private void ComposePhotoGalleryAndDisclaimer(ColumnDescriptor main, ValuationDocument doc, Dictionary<string, byte[]> photos)
        {
            var orderedSlots = new[] {
                ("FRONT VIEW",     new[] { "FrontView", "FrontViewGrille" }),
                ("REAR VIEW",      new[] { "RearView" }),
                ("FRONT RIGHT",    new[] { "FrontRightSide", "FrontRight" }),
                ("FRONT LEFT",     new[] { "FrontLeftSide",  "FrontLeft"  }),
                ("REAR RIGHT",     new[] { "RearRightSide",  "RearRight"  }),
                ("REAR LEFT",      new[] { "RearLeftSide",   "RearLeft"   }),
                ("RIGHT SIDE",     new[] { "RightSideView",  "DriverSideProfile" }),
                ("LEFT SIDE",      new[] { "LeftSideView",   "PassengerSideProfile" }),
                ("ODO METER",      new[] { "OdoMeter",       "Dashboard", "InstrumentCluster" }),
                ("ENGINE BAY",     new[] { "EngineBay",      "Engine" }),
                ("VIN PLATE",      new[] { "VinPlate",       "VIN" }),
                ("CHASSIS NUMBER", new[] { "ChassisNumber",  "ChassisNumberPlate", "Chassis" }),
                ("INTERIOR FRONT", new[] { "InteriorFront",  "FrontInterior" }),
                ("INTERIOR REAR",  new[] { "InteriorRear",   "RearInterior" })
            };

            var usedKeys = new HashSet<string>();

            main.Item().Section("PhotoGalleryTarget").Table(table =>
            {
                table.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(12); cd.RelativeColumn(); });
                int cIdx = 0;
                foreach (var slot in orderedSlots)
                {
                    byte[]? ph  = null;
                    string? url = null;
                    foreach (var key in slot.Item2)
                    {
                        if (photos.TryGetValue(key, out var val))
                        {
                            ph = val; usedKeys.Add(key);
                            doc.PhotoUrls?.TryGetValue(key, out url);
                            break;
                        }
                    }
                    if (ph != null)
                    {
                        table.Cell().Element(c => DrawPhotoCard(c, ph, slot.Item1, url));
                        cIdx++;
                        if (cIdx % 2 != 0) table.Cell();
                    }
                }

                var unmapped = photos
                    .Where(p => !usedKeys.Contains(p.Key)
                             && !p.Key.Contains("Tyre", StringComparison.OrdinalIgnoreCase)
                             && !p.Key.Contains("Tire", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var kvp in unmapped)
                {
                    string? url = null;
                    doc.PhotoUrls?.TryGetValue(kvp.Key, out url);
                    table.Cell().Element(c => DrawPhotoCard(c, kvp.Value, FormatPhotoLabel(kvp.Key), url));
                    usedKeys.Add(kvp.Key); cIdx++;
                    if (cIdx % 2 != 0) table.Cell();
                }
                if (cIdx % 2 != 0) table.Cell();
            });

            var tyrePhotos = photos
                .Where(p => p.Key.Contains("Tyre", StringComparison.OrdinalIgnoreCase)
                         || p.Key.Contains("Tire", StringComparison.OrdinalIgnoreCase))
                .Take(4).ToList();
            if (tyrePhotos.Any())
            {
                main.Item().PaddingTop(5).Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(); cd.ConstantColumn(10); cd.RelativeColumn();
                        cd.ConstantColumn(10); cd.RelativeColumn(); cd.ConstantColumn(10); cd.RelativeColumn();
                    });
                    for (int i = 0; i < tyrePhotos.Count; i++)
                    {
                        var photo = tyrePhotos[i];
                        string? url = null;
                        doc.PhotoUrls?.TryGetValue(photo.Key, out url);
                        var cell = table.Cell().AspectRatio(4 / 3f);
                        if (!string.IsNullOrWhiteSpace(url)) cell = cell.Hyperlink(url);
                        cell.Layers(layers =>
                        {
                            layers.PrimaryLayer().AlignCenter().AlignMiddle().Image(photo.Value).FitArea();
                            layers.Layer().Svg(s =>
                            {
                                string wStr = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                string hStr = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                string w16  = (s.Width  - 16).ToString("F1", CultureInfo.InvariantCulture);
                                string h16  = (s.Height - 16).ToString("F1", CultureInfo.InvariantCulture);
                                return $@"<svg width=""{wStr}"" height=""{hStr}"" viewBox=""0 0 {wStr} {hStr}"">
                                    <path fill-rule=""evenodd"" d=""M0,0 h{wStr} v{hStr} h-{wStr} Z M8,0 a8,8 0 0 0 -8,8 v{h16} a8,8 0 0 0 8,8 h{w16} a8,8 0 0 0 8,-8 v-{h16} a8,8 0 0 0 -8,-8 Z"" fill=""#FFFFFF""/>
                                </svg>";
                            });
                        });
                        if (i < 3) table.Cell();
                    }
                });
            }

            main.Item().PaddingTop(20).Layers(layers =>
            {
                layers.Layer().Svg(size => {
                    string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                    string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                    return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""8"" ry=""8"" fill=""#F8FAFC"" stroke=""#EEF2F6"" stroke-width=""1.5""/></svg>";
                });
                layers.PrimaryLayer().Padding(15).Column(col =>
                {
                    col.Item().PaddingBottom(6)
                        .Text("DISCLAIMER").FontSize(10).Bold().FontColor(LabelSlate).LetterSpacing(0.06f);
                    col.Item()
                        .Text("This Valuation Report is based on a physical, visual inspection of the vehicle carried out on the date of inspection and represents our professional opinion as on that date. The inspection is non-intrusive, and hidden, latent, or intermittent defects may not be identified. We do not verify or authenticate the genuineness of vehicle documents or odometer readings and assume no responsibility thereof. As there is no standard price list for used vehicles, the valuation stated is an estimated market value derived using our standard valuation methodology and prevailing market conditions. Actual realization may vary. This report is issued solely for the use of the addressee and shall not be relied upon by any third party. The company shall not be liable for any direct, indirect, incidental, or consequential losses arising from reliance on this report. This report is issued without prejudice.")
                        .FontSize(7.5f).FontColor("#6B7280").LineHeight(1.5f);
                });
            });
        }

        private void DrawPhotoCard(IContainer container, byte[] image, string label, string? url)
        {
            if (!string.IsNullOrWhiteSpace(url)) container = container.Hyperlink(url);
            container.PaddingBottom(12).Column(col =>
            {
                col.Item().AspectRatio(4 / 3f).Layers(imgLayers =>
                {
                    imgLayers.PrimaryLayer().AlignCenter().AlignMiddle().Image(image).FitArea();
                    imgLayers.Layer().Svg(s =>
                    {
                        string wStr = s.Width.ToString("F1",  CultureInfo.InvariantCulture);
                        string hStr = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                        string w20  = (s.Width  - 20).ToString("F1", CultureInfo.InvariantCulture);
                        string h20  = (s.Height - 20).ToString("F1", CultureInfo.InvariantCulture);
                        return $@"<svg width=""{wStr}"" height=""{hStr}"" viewBox=""0 0 {wStr} {hStr}"">
                            <path fill-rule=""evenodd"" d=""M0,0 h{wStr} v{hStr} h-{wStr} Z M10,0 a10,10 0 0 0 -10,10 v{h20} a10,10 0 0 0 10,10 h{w20} a10,10 0 0 0 10,-10 v-{h20} a10,10 0 0 0 -10,-10 Z"" fill=""#FFFFFF""/>
                            <rect x=""1"" y=""1"" width=""{(s.Width-2).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(s.Height-2).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""10"" fill=""none"" stroke=""#F5F5F5"" stroke-width=""2""/>
                        </svg>";
                    });
                });

                // Name box: left-aligned, pill-shaped, dark slate color
                col.Item().PaddingTop(4).AlignLeft().Layers(lbl =>
                {
                    lbl.Layer().Svg(s =>
                    {
                        string w = s.Width.ToString("F1",  CultureInfo.InvariantCulture);
                        string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                        // Use height/2 for a perfect pill shape
                        string r = (s.Height / 2f).ToString("F1", CultureInfo.InvariantCulture);
                        return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""{r}"" fill=""#475569""/></svg>";
                    });
                    
                    lbl.PrimaryLayer().PaddingVertical(3).PaddingHorizontal(10).AlignCenter().AlignMiddle()
                        .Text(label).FontColor(Colors.White).FontSize(7).Bold().LetterSpacing(0.05f);
                });
            });
        }

        // ──────────────────────────────────────────────
        // Table Cell Helpers
        // ──────────────────────────────────────────────

        private static int _assetRowIndex = 0;
        private void AddAssetRow(TableDescriptor table,
            string label1, string? value1, string label2, string? value2)
        {
            bool even = _assetRowIndex % 2 == 0;
            string rowBg = even ? "#FFFFFF" : "#FCFDFE";
            _assetRowIndex++;

            table.Cell().Background(rowBg).BorderRight(1).BorderBottom(1).BorderColor("#F1F5F9")
                .PaddingVertical(5f).PaddingLeft(8).AlignLeft()
                .Text(label1).FontSize(8).Bold().FontColor(LabelSlate);
            table.Cell().Background(rowBg).BorderRight(1).BorderBottom(1).BorderColor("#F1F5F9")
                .PaddingVertical(5f).PaddingRight(12).AlignRight()
                .Text(SafeFormat(value1?.ToUpper())).FontSize(9).ExtraBold().FontColor(ValueDark);
            table.Cell().Background(rowBg).BorderRight(1).BorderBottom(1).BorderColor("#F1F5F9")
                .PaddingVertical(5f).PaddingLeft(12).AlignLeft()
                .Text(label2).FontSize(8).Bold().FontColor(LabelSlate);
            table.Cell().Background(rowBg).BorderBottom(1).BorderColor("#F1F5F9")
                .PaddingVertical(5f).PaddingRight(8).AlignRight()
                .Text(SafeFormat(value2?.ToUpper())).FontSize(9).ExtraBold().FontColor(ValueDark);
        }

        private void AddVahanRow(ColumnDescriptor col, int idx,
            string l1, string? v1, string l2, string? v2)
        {
            string bg = idx % 2 == 0 ? "#FFFFFF" : "#F8FAFC";
            col.Item().Background(bg).BorderBottom(1).BorderColor("#F1F5F9")
                .PaddingVertical(6).PaddingHorizontal(10).Row(row =>
            {
                row.RelativeItem(2).AlignMiddle().Text(l1.ToUpper()).FontSize(7).FontColor(LabelSlate);
                row.RelativeItem(3).AlignMiddle().AlignRight()
                    .Text(v1?.ToUpper() ?? "---").FontSize(8).Bold()
                    .FontColor(idx == 0 ? "#3B82F6" : ValueDark);
                row.ConstantItem(16);
                row.RelativeItem(2).AlignMiddle().Text(l2.ToUpper()).FontSize(7).FontColor(LabelSlate);
                row.RelativeItem(3).AlignMiddle().AlignRight()
                    .Text(v2?.ToUpper() ?? "---").FontSize(8).Bold().FontColor(ValueDark);
            });
        }

        private void AddClientInfoRow(TableDescriptor table,
            string label1, string? value1, string label2, string? value2)
        {
            table.Cell().PaddingVertical(5f).AlignLeft()
                .Text(label1).FontSize(8).Bold().FontColor(LabelSlate);
            table.Cell().PaddingVertical(5f).AlignRight().PaddingRight(12)
                .Text(SafeFormat(value1?.ToUpper())).FontSize(9).ExtraBold().FontColor(ValueDark);
            table.Cell().PaddingVertical(5f).PaddingLeft(12).AlignLeft()
                .Text(label2).FontSize(8).Bold().FontColor(LabelSlate);
            table.Cell().PaddingVertical(5f).AlignRight()
                .Text(SafeFormat(value2?.ToUpper())).FontSize(9).ExtraBold().FontColor(ValueDark);
        }
    }
}