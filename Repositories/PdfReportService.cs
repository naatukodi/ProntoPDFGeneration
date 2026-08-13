using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using Valuation.Api.Models;
using QRCoder;

namespace Valuation.Api.Services
{
    public class PdfReportService
    {
        private readonly CosmosClient _cosmos;
        private readonly string _dbId;
        private readonly string _containerId;
        private readonly HttpClient _httpClient;
        private readonly BlobContainerClient? _blobContainer;
        private readonly string? _blobBaseUrl;

        // Master Brand Colors matched to original design
        private static readonly string BrandTeal  = "#009688";
        private static readonly string LabelSlate = "#64748B";
        private static readonly string ValueDark  = "#1a1a1a";
        private static readonly string MintBg     = "#F0FDF4";
        private static readonly string MintText   = "#059669";
        private static readonly TimeSpan PhotoDownloadTimeout = TimeSpan.FromSeconds(45);
        // Limit concurrent downloads so photos don't starve each other on slow links
        private static readonly SemaphoreSlim PhotoDownloadGate = new(4);
        private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".mpeg", ".mpg" };

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

            var blobConnStr   = configuration["BlobStorage:ConnectionString"];
            var blobContainer = configuration["BlobStorage:ContainerName"] ?? "reports";
            _blobBaseUrl      = configuration["BlobStorage:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(blobConnStr))
                _blobContainer = new BlobContainerClient(blobConnStr, blobContainer);
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

            // Fallback: chassis photos may be stored on InspectionDetails instead of PhotoUrls
            var ins = doc.InspectionDetails;
            if (ins != null)
            {
                if (!photoStreams.ContainsKey("ChassisVerification") && !string.IsNullOrEmpty(ins.ChassisVerificationPhotoUrl))
                {
                    var bytes = await TryDownloadUrl(ins.ChassisVerificationPhotoUrl);
                    if (bytes != null) photoStreams["ChassisVerification"] = bytes;
                }
                if (!photoStreams.ContainsKey("ChassisStencilTrace") && !string.IsNullOrEmpty(ins.ChassisStencilTracePhotoUrl))
                {
                    var bytes = await TryDownloadUrl(ins.ChassisStencilTracePhotoUrl);
                    if (bytes != null) photoStreams["ChassisStencilTrace"] = bytes;
                }
            }

            // QR points to the stored original PDF in blob (tamper-proof) if configured,
            // otherwise falls back to the Firebase verify page.
            string qrUrl = (_blobContainer != null && !string.IsNullOrWhiteSpace(_blobBaseUrl))
                ? $"{_blobBaseUrl.TrimEnd('/')}/{_blobContainer.Name}/{referenceNumber}.pdf"
                : $"https://prontofirebase.web.app/verify/{referenceNumber}";
            var qrCodeBytes = GenerateQRCode(qrUrl);

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

            var pdfBytes = pdfDoc.GeneratePdf();

            // Upload original PDF to blob storage so QR scan always returns the unedited version
            if (_blobContainer != null)
            {
                try
                {
                    await _blobContainer.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.Blob);
                    var blobClient = _blobContainer.GetBlobClient($"{referenceNumber}.pdf");
                    using var ms = new MemoryStream(pdfBytes);
                    await blobClient.UploadAsync(ms, overwrite: true);

                    // Browser photo gallery served next to the PDF; the cover page IMAGES LINK opens it
                    var galleryHtml = BuildGalleryHtml(doc, referenceNumber);
                    if (galleryHtml != null)
                    {
                        var galleryBlob = _blobContainer.GetBlobClient($"{referenceNumber}-gallery.html");
                        using var hs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(galleryHtml));
                        await galleryBlob.UploadAsync(hs, new Azure.Storage.Blobs.Models.BlobUploadOptions
                        {
                            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = "text/html; charset=utf-8" }
                        });
                    }
                }
                catch { /* fail silently — PDF is still returned even if upload fails */ }
            }

            return pdfBytes;
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

        private static bool IsVideoEntry(string key, string url)
        {
            if (key.Contains("video", StringComparison.OrdinalIgnoreCase)) return true;
            var path = url.Split('?')[0];
            return VideoExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<Dictionary<string, byte[]>> DownloadPhotosAsync(Dictionary<string, string>? photoUrls)
        {
            var result = new Dictionary<string, byte[]>();
            if (photoUrls == null || !photoUrls.Any()) return result;

            var tasks = photoUrls
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value) && !IsVideoEntry(kvp.Key, kvp.Value))
                .Select(async kvp =>
                {
                    await PhotoDownloadGate.WaitAsync();
                    try
                    {
                        // One retry: transient blob/network hiccups shouldn't drop report photos
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            try
                            {
                                using var cts = new CancellationTokenSource(PhotoDownloadTimeout);
                                var resp      = await _httpClient.GetAsync(kvp.Value, cts.Token);
                                if (resp.IsSuccessStatusCode)
                                {
                                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                                    if (bytes.Length > 0)
                                        return (Key: kvp.Key, Bytes: bytes, Ok: true);
                                }
                            }
                            catch (OperationCanceledException) { }
                            catch (Exception) { }
                        }
                        return (Key: kvp.Key, Bytes: Array.Empty<byte>(), Ok: false);
                    }
                    finally
                    {
                        PhotoDownloadGate.Release();
                    }
                });

            foreach (var r in (await Task.WhenAll(tasks)).Where(r => r.Ok && r.Bytes.Length > 0))
                result[r.Key] = r.Bytes;

            return result;
        }

        private async Task<byte[]?> TryDownloadUrl(string url)
        {
            try
            {
                using var cts  = new CancellationTokenSource(PhotoDownloadTimeout);
                var resp       = await _httpClient.GetAsync(url, cts.Token);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsByteArrayAsync();
            }
            catch { }
            return null;
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

        private double? CalculateSystemScoreOrNull(Dictionary<string, string?> items)
        {
            var scores = new List<double>();
            foreach (var item in items)
            {
                var v = MapVerdict(item.Value);
                switch (v)
                {
                    case "GOOD": case "YES": scores.Add(8.5); break; // GOOD = 7-10 range → 8.5
                    case "AVERAGE":          scores.Add(5.5); break; // AVERAGE = 4-7 range → 5.5
                    case "POOR": case "BAD": scores.Add(2.5); break; // POOR = 1-4 range → 2.5
                    case "NO":               scores.Add(1.0); break;
                    case "NA": break;
                    default:
                        var m = Regex.Match(v, @"\d+(\.\d+)?");
                        if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                            scores.Add(Math.Clamp(n, 0, 10));
                        break;
                }
            }
            return scores.Any() ? Math.Round(scores.Average(), 1) : (double?)null;
        }

        private double CalculateSystemScore(Dictionary<string, string?> items)
            => CalculateSystemScoreOrNull(items) ?? 8.0;

        // Overall score = average of the same system card scores shown on page 3.
        // Sections without any inspection data are excluded from the average.
        private double CalculateOverallVehicleScore(ValuationDocument doc)
        {
            var ins = doc.InspectionDetails;
            if (ins != null)
            {
                var vk = NormVehicleTypeKey(doc.Stakeholder?.ValuationType);
                if (!PdfFieldRegistry.TryGetValue(vk, out var allSections) || allSections.Length == 0)
                    allSections = PdfFieldRegistry["cv"];

                var sectionScores = new List<double>();
                foreach (var sec in allSections)
                {
                    var items = sec.Fields.ToDictionary(f => f.Label, f => GetInsValue(ins, f.Key));
                    var s = CalculateSystemScoreOrNull(items);
                    if (s.HasValue) sectionScores.Add(s.Value);
                }
                if (sectionScores.Any())
                    return Math.Round(sectionScores.Average(), 1);
            }
            return ParseScoreValue(doc.QualityControl?.OverallRating);
        }

        private (string Score, string Color) GetScoreDisplayFromDouble(double score)
        {
            var c = score >= 7.0 ? MintText    // GOOD range
                  : score >= 4.0 ? "#D97706"   // AVERAGE range
                  : "#DC2626";                  // POOR range
            return ($"{score.ToString("F1", CultureInfo.InvariantCulture)}/10", c);
        }

        private string SafeFormat(string? value, string def = "-") =>
            string.IsNullOrWhiteSpace(value) ? def : value;

        // Indian number format: ₹ 14,00,000 instead of ₹ 1,400,000
        private static string FormatIndianCurrency(decimal amount) =>
            amount.ToString("N0", new CultureInfo("en-IN"));

        private static readonly HashSet<string> _conditionWords = new(StringComparer.OrdinalIgnoreCase)
            { "good", "bad", "poor", "average", "na", "yes", "no", "ok", "fair", "true", "false", "1", "0", "qc", "avo", "backend" };

        // Returns the actual approver name, falling back through document fields
        // if VehicleInspectedBy was accidentally stored as a condition value
        private string ResolveApprovedByName(ValuationDocument doc)
        {
            var name = doc.InspectionDetails?.VehicleInspectedBy?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && !_conditionWords.Contains(name))
                return name;
            return doc.CompletedBy ?? doc.UpdatedBy ?? doc.AssignedTo ?? "Jagadeesh Kumar";
        }

        // ManufacturedDate is often null; fall back to YearOfMfg + MonthOfMfg integers from Vahan
        private static string ResolveMfgYear(VehicleDetailsDto? vd)
        {
            if (vd == null) return "-";
            if (vd.ManufacturedDate.HasValue)
                return vd.ManufacturedDate.Value.ToString("MMM-yyyy", CultureInfo.InvariantCulture).ToUpper();
            if (vd.YearOfMfg.HasValue)
            {
                if (vd.MonthOfMfg is > 0 and <= 12)
                    return new DateTime(vd.YearOfMfg.Value, vd.MonthOfMfg.Value, 1)
                        .ToString("MMM-yyyy", CultureInfo.InvariantCulture).ToUpper();
                return vd.YearOfMfg.Value.ToString();
            }
            return "-";
        }

        // ──────────────────────────────────────────────
        // Header
        // ──────────────────────────────────────────────

        private void ComposeHeader(IContainer container, ValuationDocument doc, string referenceNumber)
        {
            container.PaddingBottom(4).BorderBottom(1).BorderColor("#E2E8F0").PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Height(34).AlignLeft().Row(logoRow =>
                    {
                        var trimmedPath   = Path.Combine(AppContext.BaseDirectory, "png", "vehga-logo-trimmed.png");
                        var absolutePath  = Path.Combine(AppContext.BaseDirectory, "png", "vehga-logo.png");
                        // Resolved against the output directory only. Working-directory
                        // fallbacks used to hide a missing asset on a dev machine while
                        // the deployed service rendered text instead of the logo.
                        string? logoPath  = File.Exists(trimmedPath)  ? trimmedPath
                                          : File.Exists(absolutePath) ? absolutePath
                                          : null;
                        if (logoPath != null)
                            logoRow.ConstantItem(140).Height(34).Image(logoPath).FitArea();
                        else
                            logoRow.AutoItem().Text("VEHGA").FontSize(24).Bold().FontColor(BrandTeal);
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
                .Text("NOTE: THIS IS A DIGITALLY GENERATED REPORT, HENCE NO PHYSICAL SIGNATURE IS REQUIRED. VERIFIED VIA VEHGA SECURE CLOUD.")
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
                        .Text((doc.Stakeholder?.ValuationType ?? "RETAIL").ToUpper()).FontSize(8).Bold().FontColor(Colors.White);
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
                            byte[]? img = photos.GetValueOrDefault("FrontViewGrille")
                                       ?? photos.GetValueOrDefault("FrontView")
                                       ?? photos.GetValueOrDefault("FrontLeftSide")
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
                        layers.PrimaryLayer().PaddingVertical(5).PaddingHorizontal(6).AlignCenter()
                            .Text(vehicleName)
                            .FontSize(8).ExtraBold().FontColor(BrandTeal);
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

                            var gaugeScore = CalculateOverallVehicleScore(doc);
                            c.Item().AlignCenter().Height(82)
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
                                .Text($"₹ {FormatIndianCurrency(doc.QualityControl?.ValuationAmount ?? 0)}")
                                .FontSize(22).ExtraBold().FontColor(Colors.White);
                            c.Item().PaddingTop(4).AlignCenter()
                                .Text("Calculated based on current market trends")
                                .FontSize(7).Italic().FontColor(Colors.White);
                        });
                    });
                });
            });

            main.Item().PaddingVertical(2);

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

            main.Item().PaddingVertical(2);

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
                AddAssetRow(table, "ENGINE NUMBER",    doc.VehicleDetails?.EngineNumber?.ToUpper() ?? "-",   "ODO METER",         doc.VehicleDetails?.Odometer?.ToString() ?? doc.InspectionDetails?.Odometer?.ToString() ?? "-");
                AddAssetRow(table, "MANUFACTURE YEAR",
                    ResolveMfgYear(doc.VehicleDetails),
                    "VEHICLE TYPE",      doc.VehicleDetails?.ClassOfVehicle?.ToUpper() ?? "-");
                AddAssetRow(table, "REGISTERED ON",
                    doc.VehicleDetails?.DateOfRegistration?.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)?.ToUpper() ?? "-",
                    "OWNERSHIP NUMBER",  doc.VehicleDetails?.OwnerSerialNo?.ToString() ?? "1");
            });

            main.Item().PaddingVertical(3);

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
                    // Dedupe status comes from the QC checklist ("valDedupe": pass/fail)
                    var dedupe = doc.QualityControl?.QcChecklist?.GetValueOrDefault("valDedupe");
                    string dedupeLabel  = dedupe == "pass" ? "VERIFIED CLEAN" : dedupe == "fail" ? "FLAGGED" : "PENDING";
                    string dedupeBg     = dedupe == "pass" ? "#ECFDF5" : dedupe == "fail" ? "#FEF2F2" : "#F1F5F9";
                    string dedupeStroke = dedupe == "pass" ? "#A7F3D0" : dedupe == "fail" ? "#FECACA" : "#E2E8F0";
                    string dedupeColor  = dedupe == "pass" ? MintText  : dedupe == "fail" ? "#DC2626" : "#64748B";
                    string dedupeIcon   = dedupe == "pass" ? @"<path d=""M8 12l3 3 5-6"" stroke-linecap=""round"" stroke-linejoin=""round""/>"
                                        : dedupe == "fail" ? @"<path d=""M9 9l6 6M15 9l-6 6"" stroke-linecap=""round""/>"
                                        : @"<path d=""M8 12h8"" stroke-linecap=""round""/>";

                    linkCol.Item().PaddingBottom(5).Layers(layers =>
                    {
                        layers.Layer().Svg(s => {
                            string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""12"" fill=""{dedupeBg}"" stroke=""{dedupeStroke}"" stroke-width=""1""/></svg>";
                        });
                        layers.PrimaryLayer().PaddingVertical(4).PaddingHorizontal(8).Row(r =>
                        {
                            r.AutoItem().AlignMiddle().Width(10).Height(10)
                                .Svg(_ => $@"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""{dedupeColor}"" stroke-width=""2""><circle cx=""12"" cy=""12"" r=""10""/>{dedupeIcon}</svg>");
                            r.ConstantItem(4);
                            r.AutoItem().AlignMiddle().Text(t => {
                                t.Span("DEDUPE: ").FontSize(7).ExtraBold().FontColor(ValueDark);
                                t.Span(dedupeLabel).FontSize(7).ExtraBold().FontColor(dedupeColor);
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

                    // Opens the browser photo gallery uploaded next to the PDF; falls back to
                    // the in-PDF gallery pages when blob storage is not configured.
                    string? galleryUrl = (_blobContainer != null && !string.IsNullOrWhiteSpace(_blobBaseUrl))
                        ? $"{_blobBaseUrl.TrimEnd('/')}/{_blobContainer.Name}/{referenceNumber}-gallery.html"
                        : null;
                    var imagesChip = galleryUrl != null
                        ? linkCol.Item().Hyperlink(galleryUrl)
                        : linkCol.Item().SectionLink("PhotoGalleryTarget");
                    imagesChip.Layers(layers =>
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
                        c.Item().Text("REMARKS")
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
                        .Text("Mahesh Garikina")
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
                AddVahanRow(col, i++, "CHASSIS NUMBER",        vd?.ChassisNumber,                "YEAR OF MANUFACTURE",   ResolveMfgYear(vd));
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

            var insStat = DocumentStatus(vd?.InsurancePolicyNo, vd?.InsuranceValidUpTo);
            var insDetails = string.IsNullOrWhiteSpace(vd?.InsurancePolicyNo)
                ? "Policy: ---"
                : $"Policy: {vd!.InsurancePolicyNo}" + (vd.IDV != null ? $" | IDV: Rs. {vd.IDV?.ToString("N0")}" : "");

            var permitStat = DocumentStatus(vd?.PermitNo, vd?.PermitValidUpTo);
            var permitDetails = string.IsNullOrWhiteSpace(vd?.PermitNo)
                ? "---"
                : string.IsNullOrWhiteSpace(vd?.PermitType) ? vd!.PermitNo! : $"{vd!.PermitType} | {vd.PermitNo}";

            var fitStat = DocumentStatus(vd?.FitnessNo, vd?.FitnessValidTo);
            var fitDetails = string.IsNullOrWhiteSpace(vd?.FitnessNo) ? "" : $"Certificate: {vd!.FitnessNo}";

            main.Item().Column(col =>
            {
                AddRegulatoryCardSimple(col, "COMPREHENSIVE INSURANCE",
                    insDetails, insStat.Status, insStat.Expiry, insStat.Warn);
                AddRegulatoryCardSimple(col, "HYPOTHECATION (STATUS)",
                    hasLien ? "LIEN DETECTED" : "CLEAN / NO LIEN DETECTED",
                    hasLien ? "LIEN" : "FREE OF LIEN", hasLien ? "" : "READY FOR TRANSFER", hasLien);
                AddRegulatoryCardSimple(col, "NATIONAL PERMIT", permitDetails, permitStat.Status, permitStat.Expiry, permitStat.Warn);
                AddRegulatoryCardSimple(col, "FITNESS CERTIFICATE", fitDetails, fitStat.Status, fitStat.Expiry, fitStat.Warn);
                AddRegulatoryCardWithPhoto(col, "CHASSIS VERIFICATION",  "VERIFIED", photos, "ChassisVerification");
                AddRegulatoryCardWithPhoto(col, "CHASSIS STENCIL TRACE", "",         photos, "ChassisStencilTrace");
            });
        }

        // Derives card status from real document data: valid date → ACTIVE/EXPIRED,
        // number without date → ON RECORD, nothing → "---" with warning styling.
        private static (string Status, string Expiry, bool Warn) DocumentStatus(string? docNumber, DateTime? validUpTo)
        {
            if (validUpTo.HasValue)
            {
                var exp = $"EXP: {validUpTo.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()}";
                return validUpTo.Value.Date >= DateTime.UtcNow.Date
                    ? ("ACTIVE", exp, false)
                    : ("EXPIRED", exp, true);
            }
            return string.IsNullOrWhiteSpace(docNumber)
                ? ("---", "EXP: ---", true)
                : ("ON RECORD", "EXP: ---", false);
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

        private static byte[] CropCenterFocus(byte[] imageBytes, float widthRatio = 0.80f, float heightRatio = 0.55f)
        {
            try
            {
                using var original = SKBitmap.Decode(imageBytes);
                if (original == null) return imageBytes;
                int cropW = (int)(original.Width  * widthRatio);
                int cropH = (int)(original.Height * heightRatio);
                int x = (original.Width  - cropW) / 2;
                int y = (original.Height - cropH) / 2;
                using var cropped = new SKBitmap(cropW, cropH);
                original.ExtractSubset(cropped, new SKRectI(x, y, x + cropW, y + cropH));
                using var image = SKImage.FromBitmap(cropped);
                using var data  = image.Encode(SKEncodedImageFormat.Jpeg, 92);
                return data.ToArray();
            }
            catch { return imageBytes; }
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

                    row.RelativeItem(3).AlignMiddle().Column(c =>
                    {
                        c.Item().Text(title).FontSize(9).Bold().FontColor(ValueDark);
                        if (!string.IsNullOrEmpty(details))
                            c.Item().PaddingTop(3).Text(details).FontSize(7).FontColor(LabelSlate);
                    });

                    row.RelativeItem(7).AlignMiddle().AlignRight().Element(c =>
                    {
                        byte[]? raw = photos.GetValueOrDefault(photoKey)
                                   ?? photos.GetValueOrDefault("ChassisNumberPlate");
                        byte[]? photo = raw != null ? CropCenterFocus(raw) : null;
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

        // ── PDF inspection-field registry (mirrors inspection-field-registry.ts) ──
        private record FieldDef(string Label, string Key);
        private record SectionDef(string Name, FieldDef[] Fields);

        private static readonly Dictionary<string, SectionDef[]> PdfFieldRegistry = new()
        {
            ["cv"] = new SectionDef[]
            {
                new("BASIC SYSTEMS", new FieldDef[] {
                    new("ENGINE CONDITION","engineCondition"), new("CHASSIS CONDITION","chassisCondition"),
                    new("CABIN ASSY","cabinAssy"),             new("LOAD BODY ASSY","loadBodyAssy"),
                    new("STEERING SYSTEM","steeringSystem"),   new("BRAKE SYSTEM","brakeSystem"),
                    new("ELECTRICAL SYSTEM","electricalSystem"),new("SUSPENSION SYSTEM","suspensionSystem"),
                    new("FUEL SYSTEM","fuelSystem"),           new("TYRE CONDITION","tyreCondition"),
                }),
                new("CABIN ASSEMBLY", new FieldDef[] {
                    new("CABIN","cabin"), new("DASHBOARD","dashboard"),
                    new("DOORS","doors"), new("ALL GLASSES","allGlasses"), new("SEATS","seats"),
                }),
                new("LOAD BODY", new FieldDef[] {
                    new("RIGHT SIDE GATE","rightSideGate"), new("LEFT SIDE GATE","leftSideGate"),
                    new("TAIL GATE","tailGate"),            new("LOAD FLOOR","loadFloor"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES","frontBrakes"), new("REAR BRAKES","rearBrakes"),
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS","headLights"), new("TAIL LIGHTS / INDICATORS","tailLightsIndicators"),
                    new("BATTERY","batteryCondition"), new("WIRING ASSY","wiringAssy"),
                }),
                new("COOLING SYSTEM", new FieldDef[] {
                    new("RADIATOR","radiator"), new("INTER COOLER","intercooler"),
                    new("ALL HOSE PIPES","allHosePipes"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY","gearBoxAssy"), new("CLUTCH SYSTEM","clutchSystem"),
                    new("DIFFERENTIAL ASSY","differentialAssy"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL","steeringWheel"), new("STEERING COLUMN","steeringColumn"),
                    new("STEERING BOX","steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION","frontSuspension"), new("REAR SUSPENSION","rearSuspension"),
                    new("FRONT & REAR AXLES","axles"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AIR CONDITIONER","airConditioner"), new("AUDIO","audio"),
                    new("UPHOLSTERY","upholstery"),          new("HYDRAULIC LIFT","hydraulicLift"),
                    new("FRONT CRASH GUARD","frontCrashGuard"), new("REAR CRASH GUARD","rearCrashGuard"),
                    new("SIDE UNDER RUN PROTECTION","sideUnderRunProtection"), new("PAINT WORK","paintWork"),
                }),
            },
            ["4w"] = new SectionDef[]
            {
                new("BASIC SYSTEMS", new FieldDef[] {
                    new("ENGINE CONDITION","engineCondition"), new("CHASSIS CONDITION","chassisCondition"),
                    new("CABIN ASSY","cabinAssy"),            new("BODY ASSY","bodyAssy"),
                    new("STEERING SYSTEM","steeringSystem"),  new("BRAKE SYSTEM","brakeSystem"),
                    new("ELECTRICAL SYSTEM","electricalSystem"),new("SUSPENSION SYSTEM","suspensionSystem"),
                    new("FUEL SYSTEM","fuelSystem"),          new("TYRE CONDITION","tyreCondition"),
                }),
                new("EXTERIOR", new FieldDef[] {
                    new("BONNET ASSY","bonnet"), new("BUMPERS","bumpers"),
                    new("DOORS","doors"), new("ALL GLASSES","allGlasses"), new("SIDE FENDERS","sideFenders"),
                }),
                new("INTERIOR", new FieldDef[] {
                    new("DASH BOARD","dashboard"), new("SEATS & MATS","seats"),
                    new("UPHOLSTERY","upholstery"), new("INTERIOR TRIMS","interiorTrims"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES","frontBrakes"), new("REAR BRAKES","rearBrakes"),
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS","headLights"), new("TAIL LIGHTS / INDICATORS","tailLightsIndicators"),
                    new("BATTERY","batteryCondition"), new("WIRING ASSY","wiringAssy"),
                }),
                new("COOLING SYSTEM", new FieldDef[] {
                    new("RADIATOR","radiator"), new("INTER COOLER","intercooler"),
                    new("ALL HOSE PIPES","allHosePipes"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY","gearBoxAssy"), new("CLUTCH SYSTEM","clutchSystem"),
                    new("DRIVE SHAFTS","driveShafts"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL","steeringWheel"), new("STEERING COLUMN","steeringColumn"),
                    new("STEERING BOX","steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION","frontSuspension"), new("REAR SUSPENSION","rearSuspension"),
                    new("FRONT & REAR AXLES","axles"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AIR CONDITIONER","airConditioner"), new("AUDIO","audio"),
                    new("AIR BAGS","airBags"),               new("FRONT CRASH GUARD","frontCrashGuard"),
                    new("REAR CRASH GUARD","rearCrashGuard"), new("SUN ROOF","sunRoof"),
                    new("PAINT WORK","paintWork"),
                }),
            },
            ["2w"] = new SectionDef[]
            {
                new("BASIC SYSTEMS", new FieldDef[] {
                    new("ENGINE CONDITION","engineCondition"), new("CHASSIS CONDITION","chassisCondition"),
                    new("BODY CONDITION","bodyCondition"),     new("STEERING SYSTEM","steeringSystem"),
                    new("BRAKE SYSTEM","brakeSystem"),         new("ELECTRICAL SYSTEM","electricalSystem"),
                    new("SUSPENSION SYSTEM","suspensionSystem"),new("FUEL SYSTEM","fuelSystem"),
                    new("TYRE CONDITION","tyreCondition"),
                }),
                new("EXTERIOR", new FieldDef[] {
                    new("FUEL TANK ASSY","fuelTankCondition"), new("FRONT SCOOP","frontScoop"),
                    new("SEAT","seatCondition"),               new("R/V MIRRORS","rvMirrors"),
                    new("LOCK SET","lockSet"),
                }),
                new("BODY", new FieldDef[] {
                    new("MUD GUARD - FRONT","frontMudGuard"), new("MUD GUARD - REAR","rearMudGuard"),
                    new("SIDE COVERS","sideCovers"),          new("BELLY / FLOOR PANELS","bellyPanels"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES","frontBrakes"), new("REAR BRAKES","rearBrakes"),
                    new("BRAKE LEVERS / FLUID","brakeLeversFluid"), new("ABS","abs"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS","headLights"), new("TAIL LIGHTS / INDICATORS","tailLightsIndicators"),
                    new("BATTERY","batteryCondition"), new("WIRING ASSY","wiringAssy"),
                }),
                new("COOLING SYSTEM", new FieldDef[] {
                    new("RADIATOR","radiator"), new("SILENCER","silencer"),
                    new("SILENCER COVER","silencerCover"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY","gearBoxAssy"), new("CLUTCH SYSTEM","clutchSystem"),
                    new("ACCELERATOR","accelerator"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("HANDLE BAR","handleBar"), new("STEERING STEM","steeringStem"),
                    new("FRONT FORK","frontForkAssy"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SHOCK ABSORBER","frontShockAbsorber"), new("REAR SHOCK ABSORBER","rearShockAbsorber"),
                    new("ALLOY / WHEEL RIM","alloyWheelRim"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("MAIN STAND","mainStand"), new("SIDE STAND","sideStand"),
                    new("LEG GUARD","legGuard"),   new("SAREE GUARD","sareeGuard"),
                    new("HORN","horn"),             new("KICK PEDAL / FOOT REST","kickPedalFootRest"),
                    new("CHAIN GUARD","chainGuard"), new("SELF START","selfStart"),
                }),
            },
            ["3w"] = new SectionDef[]
            {
                new("BASIC SYSTEMS", new FieldDef[] {
                    new("ENGINE CONDITION","engineCondition"), new("CHASSIS CONDITION","chassisCondition"),
                    new("CABIN ASSY","cabinAssy"),            new("LOAD BODY ASSY","loadBodyAssy"),
                    new("STEERING SYSTEM","steeringSystem"),  new("BRAKE SYSTEM","brakeSystem"),
                    new("ELECTRICAL SYSTEM","electricalSystem"),new("SUSPENSION SYSTEM","suspensionSystem"),
                    new("FUEL SYSTEM","fuelSystem"),          new("TYRE CONDITION","tyreCondition"),
                }),
                new("CABIN ASSEMBLY", new FieldDef[] {
                    new("FRONT PANEL","frontPanel"), new("FR GLASS FRAME","frontGlassFrame"),
                    new("DASH BOARD","dashboard"),   new("SEATS & MATS","seats"),
                    new("MUDGUARDS","mudguards"),
                }),
                new("LOAD BODY", new FieldDef[] {
                    new("RIGHT SIDE GATE","rightSideGate"), new("LEFT SIDE GATE","leftSideGate"),
                    new("TAIL GATE","tailGate"),            new("LOAD FLOOR","loadFloor"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES","frontBrakes"), new("REAR BRAKES","rearBrakes"),
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("LIGHTS","headLights"), new("BATTERY","batteryCondition"),
                    new("WIRING ASSY","wiringAssy"), new("SWITCHES","switches"),
                }),
                new("COOLING SYSTEM", new FieldDef[] {
                    new("RADIATOR","radiator"), new("INTER COOLER","intercooler"),
                    new("ALL HOSE PIPES","allHosePipes"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY","gearBoxAssy"), new("CLUTCH SYSTEM","clutchSystem"),
                    new("DIFFERENTIAL ASSY","differentialAssy"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING HANDLE","steeringHandle"), new("STEERING COLUMN","steeringColumn"),
                    new("STEERING LINKAGES","steeringLinkages"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION","frontSuspension"), new("REAR SUSPENSION","rearSuspension"),
                    new("FRONT & REAR AXLES","axles"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AIR CONDITIONER","airConditioner"), new("AUDIO","audio"),
                    new("UPHOLSTERY","upholstery"),          new("LOAD CARRIER","loadCarrier"),
                    new("FRONT CRASH GUARD","frontCrashGuard"), new("REAR CRASH GUARD","rearCrashGuard"),
                    new("SIDE MIRRORS","sideMirrors"),       new("PAINT WORK","paintWork"),
                }),
            },
            ["ce"] = new SectionDef[]
            {
                new("BASIC SYSTEMS", new FieldDef[] {
                    new("ENGINE CONDITION","engineCondition"), new("CHASSIS / FRAME CONDITION","chassisCondition"),
                    new("CABIN ASSY","cabinAssy"),             new("HYDRAULIC SYSTEM","hydraulicSystem"),
                    new("STEERING / CONTROL SYSTEM","steeringControlSystem"), new("BRAKE SYSTEM","brakeSystem"),
                    new("ELECTRICAL SYSTEM","electricalSystem"),new("SUSPENSION SYSTEM","suspensionSystem"),
                    new("FUEL SYSTEM","fuelSystem"),           new("TYRE / TRACK CONDITION","tyreCondition"),
                }),
                new("CABIN ASSY", new FieldDef[] {
                    new("CABIN STRUCTURE","cabinStructure"), new("DASHBOARD & CONTROLS","dashboardControls"),
                    new("DOORS","doors"),                    new("GLASS PANELS","glassPanels"),
                    new("SEAT","seats"),
                }),
                new("ATTACHMENTS", new FieldDef[] {
                    new("BOOM / ARM","boomArm"),          new("BUCKET / BLADE","bucketBlade"),
                    new("COUNTER WEIGHT","counterWeight"), new("PINS & BUSHES","pinsAndBushes"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("SERVICE BRAKE","serviceBrake"), new("RETARDER","retarder"),
                    new("PARKING BRAKE","parkingBrake"), new("EMERGENCY STOP","emergencyStop"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("LIGHTS","headLights"), new("BATTERY","batteryCondition"),
                    new("WIRING ASSY","wiringAssy"), new("SENSORS","sensors"),
                }),
                new("COOLING SYSTEM", new FieldDef[] {
                    new("RADIATOR","radiator"), new("HYDRAULIC OIL COOLER","hydraulicOilCooler"),
                    new("ALL HOSE PIPES","allHosePipes"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY","gearBoxAssy"), new("TORQUE CONVERTER","torqueConverter"),
                    new("FINAL DRIVE","finalDrive"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING / CONTROL LEVERS","steeringControlLevers"),
                    new("HYDRAULIC STEERING PUMP","hydraulicSteeringPump"),
                    new("SWIVEL JOINTS","swivelJoints"),
                }),
                new("HYDRAULIC SYSTEM", new FieldDef[] {
                    new("HYDRAULIC PUMP","hydraulicPump"), new("CYLINDERS","hydraulicCylinders"),
                    new("HOSES & FITTINGS","hosesAndFittings"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("SWING MECHANISM","swingMechanism"), new("TRACK CHAINS","trackChains"),
                    new("SPROCKETS","sprockets"),            new("ROLLERS","rollers"),
                    new("HOUR METER","hourMeter"),           new("BONNET / GUARD","bonnetGuard"),
                    new("ROCK BREAKER","rockBreaker"),       new("PAINT WORK","paintWork"),
                }),
            },
            ["bus"] = new SectionDef[]
            {
                new("BASIC SYSTEMS", new FieldDef[] {
                    new("ENGINE CONDITION","engineCondition"), new("CHASSIS CONDITION","chassisCondition"),
                    new("COACH CONDITION","coachCondition"),   new("BODY STRUCTURE","bodyStructure"),
                    new("STEERING SYSTEM","steeringSystem"),   new("BRAKE SYSTEM","brakeSystem"),
                    new("ELECTRICAL SYSTEM","electricalSystem"),new("SUSPENSION SYSTEM","suspensionSystem"),
                    new("FUEL SYSTEM","fuelSystem"),           new("TYRE CONDITION","tyreCondition"),
                }),
                new("COACH ASSEMBLY", new FieldDef[] {
                    new("DRIVER CABIN","driverCabin"), new("DASHBOARD","dashboard"),
                    new("DOORS","doors"),               new("ALL GLASSES","allGlasses"),
                    new("BUMPERS & GRILLES","bumpersAndGrilles"),
                }),
                new("BODY ASSY", new FieldDef[] {
                    new("SEATS & BERTHS","seatsAndBerths"), new("INTERIOR TRIMS","interiorTrims"),
                    new("SIDE BODY PANELS","sideBodyPanels"), new("REAR BODY PANELS","rearBodyPanels"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES","frontBrakes"), new("REAR BRAKES","rearBrakes"),
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS","headLights"), new("TAIL LIGHTS / INDICATORS","tailLightsIndicators"),
                    new("BATTERY","batteryCondition"), new("WIRING ASSY","wiringAssy"),
                }),
                new("COOLING SYSTEM", new FieldDef[] {
                    new("RADIATOR","radiator"), new("INTER COOLER","intercooler"),
                    new("ALL HOSE PIPES","allHosePipes"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY","gearBoxAssy"), new("CLUTCH SYSTEM","clutchSystem"),
                    new("DIFFERENTIAL ASSY","differentialAssy"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL","steeringWheel"), new("STEERING COLUMN","steeringColumn"),
                    new("STEERING BOX","steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION","frontSuspension"), new("REAR SUSPENSION","rearSuspension"),
                    new("FRONT & REAR AXLES","axles"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AIR CONDITIONER","airConditioner"), new("AUDIO","audio"),
                    new("UPHOLSTERY","upholstery"),          new("LOAD CARRIER","loadCarrier"),
                    new("FRONT CRASH GUARD","frontCrashGuard"), new("REAR CRASH GUARD","rearCrashGuard"),
                    new("SIDE MIRRORS","sideMirrors"),       new("PAINT WORK","paintWork"),
                }),
            },
            ["fe"] = new SectionDef[]
            {
                new("BASIC SYSTEMS", new FieldDef[] {
                    new("ENGINE CONDITION","engineCondition"), new("CHASSIS CONDITION","chassisCondition"),
                    new("OPERATOR PLATFORM","operatorPlatform"),new("BODY ASSY","bodyAssy"),
                    new("STEERING SYSTEM","steeringSystem"),   new("BRAKE SYSTEM","brakeSystem"),
                    new("ELECTRICAL SYSTEM","electricalSystem"),new("SUSPENSION SYSTEM","suspensionSystem"),
                    new("FUEL SYSTEM","fuelSystem"),           new("TYRE CONDITION","tyreCondition"),
                }),
                new("CABIN ASSEMBLY", new FieldDef[] {
                    new("OPERATOR STATION","operatorStation"), new("DASH BOARD","dashboard"),
                    new("CANOPY","canopy"),                    new("LOCK SET","lockSet"),
                    new("SEAT","seats"),
                }),
                new("BODY ASSY", new FieldDef[] {
                    new("BONNET","bonnet"), new("FRONT GRILLES","frontGrilles"),
                    new("SIDE FENDERS","sideFenders"), new("FUEL TANK","fuelTankFe"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("RIGHT INDIVIDUAL BRAKES","rightIndividualBrakes"),
                    new("LEFT INDIVIDUAL BRAKES","leftIndividualBrakes"),
                    new("PARKING BRAKE","parkingBrake"),
                    new("BRAKE EQUALIZATION","brakeEqualization"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS","headLights"), new("TAIL LIGHTS / INDICATORS","tailLightsIndicators"),
                    new("BATTERY","batteryCondition"), new("WIRING ASSY","wiringAssy"),
                }),
                new("COOLING SYSTEM", new FieldDef[] {
                    new("RADIATOR","radiator"), new("FAN ASSY","fanAssy"),
                    new("ALL HOSE PIPES","allHosePipes"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY","gearBoxAssy"), new("CLUTCH SYSTEM","clutchSystem"),
                    new("DIFFERENTIAL ASSY","differentialAssy"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL","steeringWheel"), new("STEERING COLUMN","steeringColumn"),
                    new("STEERING BOX","steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT AXLE","frontAxleFe"), new("REAR AXLE","rearAxleFe"),
                    new("TIE RODS & JOINTS","tieRodsJoints"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("MUFFLER","muffler"),              new("AIR FILTER","airFilter"),
                    new("ATTACHMENT HITCH","attachmentHitch"), new("HYDRAULIC LIFT ARM","hydraulicLiftFe"),
                    new("FRONT CRASH GUARD","frontCrashGuard"), new("DROP ARM","dropArm"),
                    new("REAR DRAWBAR","rearDrawbar"),     new("PAINT WORK","paintWork"),
                }),
            },
        };

        private static string? GetInsValue(InspectionDetails ins, string camelKey)
        {
            var pascal = char.ToUpperInvariant(camelKey[0]) + camelKey.Substring(1);
            return typeof(InspectionDetails).GetProperty(pascal, BindingFlags.Public | BindingFlags.Instance)?.GetValue(ins)?.ToString();
        }

        private static string NormVehicleTypeKey(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "cv";
            var s = raw.Trim().ToLowerInvariant();
            if (s.Contains("commercial") || s == "cv")   return "cv";
            if (s.Contains("four")       || s == "4w")   return "4w";
            if (s.Contains("two")        || s == "2w")   return "2w";
            if (s.Contains("three")      || s == "3w")   return "3w";
            if (s.Contains("construction") || s == "ce") return "ce";
            if (s.Contains("bus")        || s == "bus")  return "bus";
            if (s.Contains("tractor") || s.Contains("farm") || s == "fe") return "fe";
            return "cv";
        }

        private static string GetSectionIcon(string name) => name switch
        {
            "BASIC SYSTEMS"                             => SVGIcons.Basic,
            "BRAKES"                                    => SVGIcons.Brakes,
            "ELECTRICAL SYSTEM"                         => SVGIcons.Electrical,
            "COOLING SYSTEM"                            => SVGIcons.Cooling,
            "TRANSMISSION SYSTEM"                       => SVGIcons.Transmission,
            "STEERING SYSTEM"                           => SVGIcons.Steering,
            "SUSPENSION SYSTEM"                         => SVGIcons.Suspension,
            "CABIN ASSEMBLY" or "CABIN ASSY" or "COACH ASSEMBLY" => SVGIcons.Cabin,
            "LOAD BODY" or "BODY ASSY" or "EXTERIOR" or "INTERIOR" => SVGIcons.LoadBody,
            "ATTACHMENTS" or "HYDRAULIC SYSTEM"         => SVGIcons.Cooling,
            _                                           => SVGIcons.Other,
        };

        private void ComposeSystemScoresPage(ColumnDescriptor main, ValuationDocument doc)
        {
            var ins = doc.InspectionDetails;
            if (ins == null)
            {
                main.Item().AlignCenter().AlignMiddle()
                    .Text("No inspection details available.").FontSize(12).FontColor(Colors.Grey.Medium);
                return;
            }

            var vk = NormVehicleTypeKey(doc.Stakeholder?.ValuationType);
            if (!PdfFieldRegistry.TryGetValue(vk, out var allSections) || allSections.Length == 0)
                allSections = PdfFieldRegistry["cv"];

            // Last section is OTHER SYSTEMS — rendered full-width at bottom in 2 columns
            var mainSections = allSections.Take(allSections.Length - 1).ToArray();
            var otherSection = allSections.Last();

            // 4 sections on the left, rest on the right — balances heavy BASIC SYSTEMS against lighter right-side sections
            int mid = Math.Min(4, mainSections.Length);
            var leftSections  = mainSections.Take(mid).ToArray();
            var rightSections = mainSections.Skip(mid).ToArray();

            Dictionary<string, string?> BuildItems(SectionDef sec) =>
                sec.Fields.ToDictionary(f => f.Label, f => GetInsValue(ins, f.Key));

            main.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    foreach (var sec in leftSections)
                        col.Item().Element(c => DrawSystemCard(c, GetSectionIcon(sec.Name), sec.Name, BuildItems(sec)));
                });

                row.ConstantItem(8);

                row.RelativeItem().Column(col =>
                {
                    foreach (var sec in rightSections)
                        col.Item().Element(c => DrawSystemCard(c, GetSectionIcon(sec.Name), sec.Name, BuildItems(sec)));
                });
            });

            // OTHER SYSTEMS spans full width with fields in 2 columns
            main.Item().Element(c => DrawSystemCard(c, SVGIcons.Other, otherSection.Name,
                BuildItems(otherSection), twoColumns: true));
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

                layers.PrimaryLayer().Padding(7).Column(col =>
                {
                    col.Item().PaddingBottom(4).BorderBottom(1).BorderColor("#E2E8F0").Row(row =>
                    {
                        row.AutoItem().Width(12).Height(12).Svg(_ => iconSvg);
                        row.ConstantItem(5);
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

                    col.Item().PaddingTop(3).Table(table =>
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
                                c.PaddingVertical(2.5f).Row(r =>
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

        private void RenderPhotoTable(IContainer into, (string Label, string[] Keys)[] slots,
            ValuationDocument doc, Dictionary<string, byte[]> photos, HashSet<string>? selectedKeys)
        {
            into.Table(table =>
            {
                table.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(12); cd.RelativeColumn(); });
                int col = 0;
                foreach (var slot in slots)
                {
                    byte[]? ph = null; string? url = null; string? matchedKey = null;
                    foreach (var key in slot.Keys)
                    {
                        if (photos.TryGetValue(key, out var val)) { ph = val; matchedKey = key; doc.PhotoUrls?.TryGetValue(key, out url); break; }
                    }
                    if (ph == null) continue;
                    if (selectedKeys != null && !selectedKeys.Contains(matchedKey!)) continue;
                    var p = ph; var l = slot.Label; var u = url;
                    table.Cell().Element(c => DrawPhotoCard(c, p, l, u));
                    col++;
                    if (col % 2 != 0) table.Cell();
                }
                if (col % 2 != 0) table.Cell();
            });
        }

        // Shared by the PDF gallery pages and the browser gallery HTML so both use identical labels.
        private static readonly (string Label, string[] Keys)[] GalleryPhotoSlots = new (string Label, string[] Keys)[] {
            ("FRONT VIEW",  new[] { "FrontViewGrille", "FrontView" }),
            ("REAR VIEW",   new[] { "RearViewTailgate", "RearView" }),
            ("FRONT RIGHT", new[] { "FrontRightSide", "FrontRight" }),
            ("FRONT LEFT",  new[] { "FrontLeftSide",  "FrontLeft" }),
            ("REAR RIGHT",  new[] { "RearRightSide",  "RearRight" }),
            ("REAR LEFT",   new[] { "RearLeftSide",   "RearLeft"  }),
            ("RIGHT SIDE",  new[] { "DriverSideProfile",    "RightSideView"      }),
            ("LEFT SIDE",   new[] { "PassengerSideProfile", "LeftSideView"       }),
            ("ODO METER",   new[] { "Odometer", "OdoMeter", "InstrumentCluster" }),
            ("ENGINE BAY",  new[] { "EngineBay", "Engine"                        }),
            ("DASHBOARD",   new[] { "Dashboard", "DashboardCloseup"             }),
            ("SELFIE",      new[] { "SelfieWithVehicle", "Selfie"               }),
            // Chassis identification photos (ChassisVerification / ChassisStencilTrace
            // render separately on page 2's regulatory cards)
            ("CHASSIS NUMBER", new[] { "ChassisNumberPlate", "ChassisNumber", "Chassis", "ChassisImprint" }),
            ("VIN PLATE",      new[] { "VinPlate", "VIN" }),
        };

        private static string HumanizePhotoKey(string key)
        {
            var spaced = Regex.Replace(key, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            return spaced.ToUpperInvariant();
        }

        // Standalone browser gallery uploaded next to the PDF; the cover page IMAGES LINK opens it.
        private string? BuildGalleryHtml(ValuationDocument doc, string referenceNumber)
        {
            var urls  = doc.PhotoUrls ?? new Dictionary<string, string>();
            var used  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<object>();

            void Add(string label, string? url)
            {
                if (!string.IsNullOrWhiteSpace(url))
                    items.Add(new { n = label, u = url });
            }

            foreach (var slot in GalleryPhotoSlots)
            {
                foreach (var key in slot.Keys)
                {
                    if (urls.TryGetValue(key, out var u) && !string.IsNullOrWhiteSpace(u) && !IsVideoEntry(key, u))
                    {
                        Add(slot.Label, u);
                        foreach (var k in slot.Keys) used.Add(k);
                        break;
                    }
                }
            }

            // Tyre photos are excluded from the browser gallery
            foreach (var k in new[] { "TireFrontLeft", "TireFrontRight", "TireRearLeft", "TireRearRight" })
                used.Add(k);

            // Any photos not covered by a named slot
            foreach (var kv in urls)
            {
                if (used.Contains(kv.Key) || string.IsNullOrWhiteSpace(kv.Value) || IsVideoEntry(kv.Key, kv.Value))
                    continue;
                Add(HumanizePhotoKey(kv.Key), kv.Value);
            }

            if (items.Count == 0) return null;

            var regNo = (doc.VehicleDetails?.RegistrationNumber ?? referenceNumber).ToUpperInvariant();
            var json  = Newtonsoft.Json.JsonConvert.SerializeObject(items,
                new Newtonsoft.Json.JsonSerializerSettings { StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.EscapeHtml });

            return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{regNo}} — Photo Gallery</title>
<style>
  * { margin:0; padding:0; box-sizing:border-box; }
  body { background:#0f172a; color:#fff; font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;
         height:100dvh; display:flex; flex-direction:column; overflow:hidden; }
  header { padding:10px 14px; display:flex; align-items:center; gap:10px; background:#111c33; }
  header .reg   { font-weight:700; font-size:14px; letter-spacing:.5px; color:#5eead4; white-space:nowrap; }
  header .name  { font-weight:700; font-size:13px; text-align:center; flex:1; }
  header .count { font-size:12px; color:#94a3b8; white-space:nowrap; }
  .stage { flex:1; position:relative; display:flex; align-items:center; justify-content:center; min-height:0; }
  .stage img { max-width:100%; max-height:100%; object-fit:contain; }
  .nav { position:absolute; top:50%; transform:translateY(-50%); width:44px; height:44px; border-radius:50%;
         border:none; background:rgba(255,255,255,.12); color:#fff; font-size:22px; cursor:pointer; }
  .nav:active { background:rgba(255,255,255,.3); }
  .prev { left:10px; } .next { right:10px; }
  .thumbs { display:flex; gap:6px; overflow-x:auto; padding:8px 10px; background:#111c33; }
  .thumbs img { width:64px; height:48px; object-fit:cover; border-radius:6px; opacity:.5; cursor:pointer;
                flex:0 0 auto; border:2px solid transparent; }
  .thumbs img.active { opacity:1; border-color:#5eead4; }
</style>
</head>
<body>
<header><span class="reg">{{regNo}}</span><span class="name" id="name"></span><span class="count" id="count"></span></header>
<div class="stage">
  <img id="main" alt="">
  <button class="nav prev" onclick="go(-1)">&#10094;</button>
  <button class="nav next" onclick="go(1)">&#10095;</button>
</div>
<div class="thumbs" id="thumbs"></div>
<script>
const photos = {{json}};
let i = 0;
const main = document.getElementById('main'), nameEl = document.getElementById('name'),
      countEl = document.getElementById('count'), thumbs = document.getElementById('thumbs');
photos.forEach((p, idx) => {
  const t = document.createElement('img');
  t.src = p.u; t.loading = 'lazy'; t.alt = p.n;
  t.onclick = () => show(idx);
  thumbs.appendChild(t);
});
function show(n) {
  i = (n + photos.length) % photos.length;
  main.src = photos[i].u;
  nameEl.textContent = photos[i].n;
  countEl.textContent = (i + 1) + ' / ' + photos.length;
  [...thumbs.children].forEach((t, idx) => t.classList.toggle('active', idx === i));
  thumbs.children[i].scrollIntoView({ inline:'center', block:'nearest', behavior:'smooth' });
  if (photos[i + 1]) new Image().src = photos[i + 1].u;
}
function go(d) { show(i + d); }
document.addEventListener('keydown', e => {
  if (e.key === 'ArrowLeft') go(-1);
  if (e.key === 'ArrowRight') go(1);
});
let sx = null;
const stage = document.querySelector('.stage');
stage.addEventListener('touchstart', e => sx = e.touches[0].clientX, { passive:true });
stage.addEventListener('touchend', e => {
  if (sx === null) return;
  const dx = e.changedTouches[0].clientX - sx;
  if (Math.abs(dx) > 40) go(dx < 0 ? 1 : -1);
  sx = null;
}, { passive:true });
show(0);
</script>
</body>
</html>
""";
        }

        private void ComposePhotoGalleryAndDisclaimer(ColumnDescriptor main, ValuationDocument doc, Dictionary<string, byte[]> photos)
        {
            // QC's chosen subset of gallery photos. Null/empty (never set, or every photo unchecked)
            // falls back to the standard behavior of including everything available.
            var selectedKeys = doc.SelectedGalleryPhotos != null && doc.SelectedGalleryPhotos.Count > 0
                ? new HashSet<string>(doc.SelectedGalleryPhotos)
                : null;

            // All labeled gallery photos flow through one continuous table (2 per row).
            // QuestPDF paginates a Table's rows across pages automatically, so this
            // naturally packs ~6 photos per page instead of forcing fixed category
            // pages that leave mostly-empty pages when few photos are selected.
            RenderPhotoTable(main.Item().PaddingTop(4).Section("PhotoGalleryTarget"),
                GalleryPhotoSlots, doc, photos, selectedKeys);

            // Tyres: all uploaded (and selected, if a selection is set) tyre photos in one unlabeled row of four.
            // Flows directly after the photo table — same page if there's room, a new page otherwise.
            var tyreKeys = new[] { "TireFrontLeft", "TireFrontRight", "TireRearLeft", "TireRearRight" };
            var tyrePhotos = tyreKeys
                .Where(photos.ContainsKey)
                .Where(k => selectedKeys == null || selectedKeys.Contains(k))
                .Select(k => photos[k]).ToList();
            if (tyrePhotos.Any())
            {
                main.Item().PaddingTop(4).Row(row =>
                {
                    foreach (var tyre in tyrePhotos)
                    {
                        var img = tyre;
                        row.RelativeItem().Padding(3).Element(c => DrawUnlabeledPhoto(c, img));
                    }
                    // Keep four-column sizing when fewer than four tyres exist
                    for (var i = tyrePhotos.Count; i < 4; i++) row.RelativeItem();
                });
            }

            // Disclaimer
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

        private void DrawUnlabeledPhoto(IContainer container, byte[] image)
        {
            container.AspectRatio(3 / 4f).Layers(imgLayers =>
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