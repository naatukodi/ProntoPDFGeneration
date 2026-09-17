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

        /// <summary>Everything that differs between the two companies. Layout, spacing
        /// and wording are shared — only identity changes.</summary>
        private sealed record BrandTheme(
            string Key, string Name, string Primary, string TintBg,
            string LogoTrimmed, string LogoFull, string Watermark, string FooterNote);

        /// <summary>Valuer licence of the approver named on every report's sign-off.</summary>
        private const string ApproverLicenseNo = "74183";

        private static readonly BrandTheme VehgaTheme = new(
            "vehga", "VEHGA", "#009688", "#E6F5F3",
            "vehga-logo-trimmed.png", "vehga-logo.png", "VEHGA VERIFIED",
            "NOTE: THIS IS A DIGITALLY GENERATED REPORT, HENCE NO PHYSICAL SIGNATURE IS REQUIRED. VERIFIED VIA VEHGA SECURE CLOUD.");

        // Pronto's green is darkened from the logo's #02944E, which only reaches 3.9:1
        // on white; #02763E clears WCAG AA and matches Vehga's contrast on the page.
        private static readonly BrandTheme ProntoTheme = new(
            "pronto", "PRONTO MOTO", "#02763E", "#E8F5EE",
            "pronto-logo-trimmed.png", "pronto-logo-trimmed.png", "PRONTO VERIFIED",
            "NOTE: THIS IS A DIGITALLY GENERATED REPORT, HENCE NO PHYSICAL SIGNATURE IS REQUIRED. VERIFIED VIA PRONTO SECURE CLOUD.");

        // AsyncLocal rather than an instance field: the service may be registered as a
        // singleton, and two reports for different brands can be generated concurrently.
        private static readonly AsyncLocal<BrandTheme?> CurrentTheme = new();
        private static BrandTheme Theme => CurrentTheme.Value ?? VehgaTheme;

        private static BrandTheme ThemeFor(string? brand) =>
            string.Equals(brand, "pronto", StringComparison.OrdinalIgnoreCase) ? ProntoTheme : VehgaTheme;

        // Kept under the old name so the ~40 existing call sites need no edit; it now
        // resolves per-document instead of being a fixed teal.
        private static string BrandTeal => Theme.Primary;

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
            // Set before anything composes: BrandTeal and the header/watermark/footer
            // all read from this for the rest of the generation.
            CurrentTheme.Value = ThemeFor(doc.Brand);

            var referenceNumber = await EnsureReferenceNumberAsync(doc);
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
                    // Ligatures off. Lato's `liga` feature folds "ti", "tt" and "fi" into
                    // single glyphs, and those glyphs have no cmap entry -- they exist only
                    // as GSUB output -- so Skia's ToUnicode map, which it builds by reverse
                    // cmap lookup, has no way to name them and leaves them out. A reader
                    // then falls back to the raw glyph id: "inspection" came out as
                    // "inspec<U+099E>on" (glyph 2462 read as a Bengali codepoint), and a
                    // search for "inspection", "valuation" or "identified" found nothing in
                    // any report we have ever issued. The "fi" case was quieter but no
                    // better -- it mapped to U+FB01, so the text looked clean and still
                    // failed the search. Drawing each letter as its own glyph costs a
                    // little typographic polish and makes the report readable by machine.
                    page.DefaultTextStyle(x => x.FontFamily("Helvetica").FontSize(8).FontColor(Colors.Black)
                        .DisableFontFeature(FontFeatures.StandardLigatures));

                    // Left as SVG deliberately. Its typeface is host-dependent like the gauge's
                    // was, but this is a 1.5%-opacity wash where the face is imperceptible --
                    // and QuestPDF's Rotate pivots on the corner rather than the centre, so
                    // drawing it as text moved the watermark 130pt up the page. A visible
                    // misplacement is a worse trade than an invisible font difference.
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
                        fill="#808080" fill-opacity="0.015">{Theme.Watermark}</text>
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
                </svg>
                """;
        }

        /// <summary>
        /// Where the gauge's two numbers belong, in points from the top of the gauge box.
        ///
        /// They used to be &lt;text&gt; inside the SVG, but Skia's SVG renderer ignores
        /// font-family and draws with whatever the host's default typeface happens to be --
        /// Segoe UI on one App Service worker and DejaVu SERIF on the next, which put the
        /// report's most prominent number in a serif face after a redeploy. Drawn through
        /// QuestPDF instead they are the bundled Lato, identical on every host.
        ///
        /// Lato's ascender+descender is exactly 1.2em, the same as QuestPDF's line box, so
        /// the baseline sits one ascender (0.987em) below the line top. These return the
        /// LINE TOP that puts each baseline where the SVG used to draw it.
        /// </summary>
        private static (float NumSize, float NumTop, float SubSize, float SubTop) ScoreGaugeTextLayout(float width, float height)
        {
            float cx = width / 2f, cy = height * 0.50f;
            float radius  = Math.Min(cx, cy) * 0.82f;
            float numSize = radius * 0.88f;
            float subSize = radius * 0.30f;
            float numY    = cy + numSize * 0.28f;          // baseline, as in the SVG
            float subY    = numY + subSize * 1.2f;
            const float AscenderEm = 0.987f;               // Lato hhea ascender 1974/2000
            return (numSize, numY - numSize * AscenderEm, subSize, subY - subSize * AscenderEm);
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

        // "PM" for Pronto Moto, "VG" for Vehga. Documents created before multi-brand
        // have no Brand and are Vehga — but they already carry a persisted PM- reference
        // from when the prefix was hardcoded, so they keep it. The result is a permanent
        // mix of PM- and VG- across Vehga's history, which is the accepted price of not
        // invalidating QR codes on reports already in customers' hands.
        private static string BrandPrefix(string? brand) =>
            string.Equals(brand, "pronto", StringComparison.OrdinalIgnoreCase) ? "PM" : "VG";

        // The prefix every report carried before the brand split. Legacy documents must
        // be backfilled with THIS, not with their brand's prefix: it is what was printed
        // on them, and the QR resolves to reports/{reference}.pdf.
        private const string LegacyPrefix = "PM";

        // attempt 0 reproduces the original pre-collision-check hash exactly, so the
        // backfill can recover the reference an existing report was printed with.
        private static string ComposeReference(ValuationDocument doc, int attempt, string? forcePrefix = null)
        {
            var seed = $"{doc.VehicleDetails?.RegistrationNumber}|{doc.CreatedAt:yyyyMMdd}|{doc.id}";
            if (attempt > 0) seed += $"|{attempt}";

            using var sha256 = SHA256.Create();
            var h      = sha256.ComputeHash(Encoding.UTF8.GetBytes(seed));
            var num    = ((h[0] << 16) | (h[1] << 8) | h[2]) % 1_000_000;
            var letter = (char)('A' + h[3] % 26);
            return $"{forcePrefix ?? BrandPrefix(doc.Brand)}-{num:D6}-{letter}";
        }

        private async Task<bool> ReferenceTakenAsync(string reference, string? excludeDocId)
        {
            var query = new QueryDefinition(
                    "SELECT VALUE COUNT(1) FROM c WHERE c.ReferenceNumber = @ref AND c.id != @id")
                .WithParameter("@ref", reference)
                .WithParameter("@id", excludeDocId ?? string.Empty);

            using var iterator = Container.GetItemQueryIterator<int>(query);
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync();
                if (page.FirstOrDefault() > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the document's permanent reference, assigning one the first time.
        /// Once assigned it is never recomputed: the QR code encodes the blob path
        /// reports/{reference}.pdf, so a changed reference orphans printed reports.
        ///
        /// The reference space is only 26 million (6 digits x 1 letter), so by the
        /// birthday bound a collision is roughly 1% likely at ~720 reports and 50% at
        /// ~6,000 — and a collision means one report silently overwrites another's blob
        /// and its QR then serves the wrong vehicle. Hence the uniqueness check.
        /// </summary>
        private async Task<string> EnsureReferenceNumberAsync(ValuationDocument doc)
        {
            if (!string.IsNullOrWhiteSpace(doc.ReferenceNumber)) return doc.ReferenceNumber;

            var reference = ComposeReference(doc, 0);
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                if (!await ReferenceTakenAsync(reference, doc.id)) break;
                reference = ComposeReference(doc, attempt);
            }

            doc.ReferenceNumber = reference;
            await PersistReferenceNumberAsync(doc, reference);
            return reference;
        }

        /// <summary>
        /// One-off migration: gives every document that predates persisted references the
        /// exact reference it was already printed with, so the prefix split can't move it.
        /// Uses the legacy PM- prefix deliberately — these reports are in customers' hands
        /// with PM- QR codes regardless of which company they belong to.
        /// Idempotent: documents that already have a reference are skipped.
        /// </summary>
        public async Task<BackfillResult> BackfillReferenceNumbersAsync(
            bool dryRun = true, CancellationToken ct = default)
        {
            var result = new BackfillResult();
            int scanned = 0, assigned = 0, collisions = 0;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Existing references first, so the collision check sees them.
            using (var it = Container.GetItemQueryIterator<string>(new QueryDefinition(
                "SELECT VALUE c.ReferenceNumber FROM c WHERE IS_STRING(c.ReferenceNumber) AND LENGTH(c.ReferenceNumber) > 0")))
            {
                while (it.HasMoreResults)
                    foreach (var r in await it.ReadNextAsync(ct))
                        if (!string.IsNullOrWhiteSpace(r)) taken.Add(r);
            }

            using var docs = Container.GetItemQueryIterator<ValuationDocument>(new QueryDefinition(
                "SELECT * FROM c WHERE NOT IS_DEFINED(c.ReferenceNumber) OR c.ReferenceNumber = null OR c.ReferenceNumber = ''"));

            while (docs.HasMoreResults)
            {
                foreach (var doc in await docs.ReadNextAsync(ct))
                {
                    scanned++;
                    var reference = ComposeReference(doc, 0, LegacyPrefix);
                    for (var attempt = 1; attempt <= 10 && taken.Contains(reference); attempt++)
                    {
                        collisions++;
                        reference = ComposeReference(doc, attempt, LegacyPrefix);
                    }

                    taken.Add(reference);
                    assigned++;

                    // A migration must not fail quietly: report exactly why a document
                    // could not be written, or 21 of them vanish without explanation.
                    if (string.IsNullOrWhiteSpace(doc.id) || string.IsNullOrWhiteSpace(doc.VehicleNumber))
                    {
                        result.SkippedMissingKey++;
                        if (result.Examples.Count < 10)
                            result.Examples.Add($"missing key: id={doc.id ?? "(null)"} vehicle={doc.VehicleNumber ?? "(null)"} applicant={doc.ApplicantContact ?? "(null)"}");
                        continue;
                    }

                    if (dryRun) { result.Written++; continue; }

                    try
                    {
                        await Container.PatchItemAsync<ValuationDocument>(
                            doc.id,
                            GetPk(doc.VehicleNumber, doc.ApplicantContact ?? string.Empty),
                            new[] { PatchOperation.Set("/ReferenceNumber", reference) },
                            cancellationToken: ct);
                        result.Written++;
                    }
                    catch (CosmosException ex)
                    {
                        result.Failed++;
                        if (result.Examples.Count < 10)
                            result.Examples.Add($"{ex.StatusCode}: id={doc.id} pk={doc.VehicleNumber}|{doc.ApplicantContact}");
                    }
                }
            }

            result.Scanned = scanned;
            result.Assigned = assigned;
            result.Collisions = collisions;
            return result;
        }

        public sealed class BackfillResult
        {
            public int Scanned { get; set; }
            public int Assigned { get; set; }
            public int Collisions { get; set; }
            public int Written { get; set; }
            /// <summary>Documents whose id or partition key is incomplete, so they cannot be patched.</summary>
            public int SkippedMissingKey { get; set; }
            public int Failed { get; set; }
            public List<string> Examples { get; } = new();
        }

        private async Task PersistReferenceNumberAsync(ValuationDocument doc, string reference)
        {
            if (string.IsNullOrWhiteSpace(doc.id) || string.IsNullOrWhiteSpace(doc.VehicleNumber))
                return;

            try
            {
                await Container.PatchItemAsync<ValuationDocument>(
                    doc.id,
                    GetPk(doc.VehicleNumber, doc.ApplicantContact ?? string.Empty),
                    new[] { PatchOperation.Set("/ReferenceNumber", reference) });
            }
            catch (CosmosException)
            {
                // Never fail PDF generation because the write-back failed: the caller
                // still gets a valid report, and the next generation retries the patch.
            }
        }

        /// <summary>
        /// Normalises an inspection value to one of GOOD / AVERAGE / POOR / DAMAGED /
        /// MISSING / NO / NA. Mirrors CONDITION_OPTIONS in the portal's
        /// inspection-field-registry.ts and conditionOptions in the app's
        /// inspection_field_registry.dart — keep the three in sync.
        /// </summary>
        private string MapVerdict(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "NA";
            var lower = input.ToLower().Trim();
            if (lower is "true" or "yes" or "1" or "good" or "ok") return "GOOD";
            if (lower is "false" or "0" or "bad" or "poor")        return "POOR";
            if (lower == "no")                                        return "NO";
            if (lower is "average" or "fair")                        return "AVERAGE";
            if (lower is "damaged" or "damage")                      return "DAMAGED";
            if (lower.StartsWith("missing") || lower is "not present" or "absent") return "MISSING";
            if (lower is "n/a" or "na" or "n.a." or "not applicable") return "NA";
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
                    case "DAMAGED":          scores.Add(2.0); break; // worse than POOR, better than absent
                    case "NO":               scores.Add(1.0); break;
                    case "MISSING":          scores.Add(0.5); break; // part is not on the vehicle
                    case "NA": break;                                // not applicable — excluded from the average
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

        /// <summary>The fields of a section that count toward its score.</summary>
        private Dictionary<string, string?> ScorableItems(SectionDef sec, InspectionDetails ins) =>
            sec.Fields.Where(f => f.Scored)
                      .ToDictionary(f => f.Label, f => GetInsValue(ins, f.Key));

        // Overall score = average of the same system card scores shown on page 3.
        // Sections without any inspection data are excluded from the average.
        private double CalculateOverallVehicleScore(ValuationDocument doc)
        {
            var ins = doc.InspectionDetails;
            if (ins != null)
            {
                var vk = ResolveVehicleTypeKey(doc);
                if (!PdfFieldRegistry.TryGetValue(vk, out var allSections) || allSections.Length == 0)
                    allSections = PdfFieldRegistry["cv"];

                var sectionScores = new List<double>();
                foreach (var sec in allSections)
                {
                    if (!IsScored(sec)) continue;
                    var s = CalculateSystemScoreOrNull(ScorableItems(sec, ins));
                    if (s.HasValue) sectionScores.Add(s.Value);
                }
                if (sectionScores.Any())
                    return Math.Round(sectionScores.Average(), 1);
            }
            return ParseScoreValue(doc.QualityControl?.OverallRating);
        }

        /// <summary>
        /// One badge per system for the cover's INDIVIDUAL RATINGS grid.
        ///
        /// Driven off the same registry and the same scoring call as page 3's system
        /// cards, so the cover cannot state a figure the detail page contradicts -- the
        /// property the old banded verdicts were written to protect, kept here.
        ///
        /// Sections with no inspection data are dropped rather than shown as zero, and
        /// unscored sections (OTHER SYSTEMS) never appear: the grid is captioned as
        /// ratings, and a section the report does not score has no rating to give.
        /// </summary>
        private List<(string Name, double Score)> SystemRatings(ValuationDocument doc)
        {
            var result = new List<(string, double)>();
            var ins = doc.InspectionDetails;
            if (ins == null) return result;

            var vk = ResolveVehicleTypeKey(doc);
            if (!PdfFieldRegistry.TryGetValue(vk, out var sections) || sections.Length == 0)
                sections = PdfFieldRegistry["cv"];

            foreach (var sec in sections)
            {
                if (!IsScored(sec)) continue;
                var score = CalculateSystemScoreOrNull(ScorableItems(sec, ins));
                if (score.HasValue) result.Add((sec.Name, score.Value));
            }
            return result;
        }

        /// <summary>Ring, fill and ink for a score badge, on the bands the rest of the report uses.</summary>
        private static (string Ring, string Fill, string Ink) ScoreBadgeColors(double score) =>
            score >= 7.0 ? ("#1C8A4C", "#E3F7EA", "#14663A")
          : score >= 4.0 ? ("#B3760C", "#FFF3D9", "#8A5A09")
          :                ("#C0392B", "#FDE3E3", "#94271C");

        /// <summary>
        /// A rating tile: bordered cell holding a ringed score circle over its system name.
        /// Fixed height so every tile in the grid lines up whether its name wraps or not.
        /// </summary>
        private void RatingTile(IContainer cell, string name, double score)
        {
            var (ring, fill, ink) = ScoreBadgeColors(score);
            cell.Padding(1.5f).Layers(layers =>
            {
                layers.Layer().Svg(s =>
                {
                    string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                    string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                    return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}""><rect x=""0.5"" y=""0.5"" width=""{(s.Width - 1).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(s.Height - 1).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""5"" fill=""#FBFDFC"" stroke=""#E3E8EA"" stroke-width=""1""/></svg>";
                });
                layers.PrimaryLayer().PaddingVertical(2).PaddingHorizontal(2).Column(c =>
                {
                    c.Item().AlignCenter().Width(21).Height(21).Layers(b =>
                    {
                        b.Layer().Svg(_ =>
                            $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 21 21""><circle cx=""10.5"" cy=""10.5"" r=""9.7"" fill=""{fill}"" stroke=""{ring}"" stroke-width=""1.4""/></svg>");
                        b.PrimaryLayer().AlignCenter().AlignMiddle()
                            .Text(score.ToString("F1", CultureInfo.InvariantCulture))
                            .FontSize(7.5f).ExtraBold().FontColor(ink);
                    });
                    c.Item().PaddingTop(2).AlignCenter().AlignMiddle()
                        .Text(name).FontSize(5.2f).ExtraBold().FontColor(LabelSlate).LineHeight(0.95f);
                });
            });
        }

        private (string Score, string Color) GetScoreDisplayFromDouble(double score)
        {
            var c = score >= 7.0 ? MintText    // GOOD range
                  : score >= 4.0 ? "#D97706"   // AVERAGE range
                  : "#DC2626";                  // POOR range
            return ($"{score.ToString("F1", CultureInfo.InvariantCulture)}/10", c);
        }

        /// <summary>
        /// The four verdicts on the cover, read against the sheet the AVO was actually
        /// given rather than one fixed property each.
        ///
        /// Every sheet names the same question differently -- the body is `loadBodyAssy`
        /// on a truck, `bodyAssy` on a car, `bodyCondition` on a two-wheeler and
        /// `bodyStructure` on a bus -- and the cover read only `bodyCondition`. That key
        /// exists on the 2W sheet alone, so every commercial vehicle printed LOAD BODY =
        /// NA while the answer sat on the checklist page as LOAD BODY ASSY. CABIN carried
        /// the mirror image of the same fault (`cabin` is CV-only), so no vehicle type
        /// could ever fill both cells. OTHER SYSTEMS was the literal string "GOOD" -- a
        /// verdict no inspector gave, on the cover of every report ever issued.
        ///
        /// Candidates are filtered to the keys THIS vehicle's registry actually asks for,
        /// so a stale value on a field belonging to another sheet -- a case whose segment
        /// was corrected after inspection -- can never be printed.
        /// </summary>
        private (string? Cabin, string? Engine, string? Body, string Other) ResolveCoverVerdicts(ValuationDocument doc)
        {
            var ins = doc.InspectionDetails;
            if (ins == null) return (null, null, null, "NA");

            var vk = ResolveVehicleTypeKey(doc);
            if (!PdfFieldRegistry.TryGetValue(vk, out var sections) || sections.Length == 0)
                sections = PdfFieldRegistry["cv"];

            var asked = sections.SelectMany(s => s.Fields)
                                .Select(f => f.Key)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string? FirstAnswered(params string[] candidates)
            {
                foreach (var key in candidates)
                {
                    if (!asked.Contains(key)) continue;
                    var v = GetInsValue(ins, key);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                return null;
            }

            // Construction equipment has no body panel section, and neither a bus nor a
            // tractor has a plain "cabin" item, so those resolve to nothing and print NA.
            // That is the honest answer where asserting a verdict was not.
            var cabin  = FirstAnswered("cabin", "cabinAssy", "cabinStructure", "driverCabin", "operatorPlatform");
            var engine = FirstAnswered("engineCondition");
            var body   = FirstAnswered("loadBodyAssy", "bodyAssy", "bodyCondition", "bodyStructure");

            // OTHER SYSTEMS used to be summarised here as a fourth cover verdict, banded
            // from its section score. It is no longer scored, and a band on the cover is
            // a score by another name, so the box was dropped rather than left printing
            // a number the report no longer stands behind. The tuple keeps its shape so
            // the caller's layout is unchanged; nothing renders it.
            return (cabin, engine, body, "NA");
        }

        /// <summary>
        /// How the duplicate check reads on the report.
        ///
        /// Reflects the real check the case flow runs — other cases matching this
        /// vehicle, engine or chassis — not the QC checklist's "valDedupe", which
        /// is the VAHAN blacklist flag and answers a different question.
        ///
        /// A match is stated as a count, never as "DUPLICATE". The check spans both
        /// companies and excludes this case, so a match means the vehicle has been
        /// through Vehga or Pronto before — often legitimately, on a re-valuation, on
        /// a repo following a retail, or because it was inspected for both. The report
        /// reports it; it does not accuse.
        ///
        /// The count is deliberately not split by company. Which company a match sits
        /// in is shown in the portal, where someone can act on it; on the report a
        /// match is a match. That is what makes VERIFIED CLEAN mean what a reader
        /// takes it to mean — clean across both — at the cost of a legitimate
        /// dual-brand case reading as a prior case, which is rare enough to accept.
        ///
        /// Null (never checked) reads PENDING rather than clean: asserting a vehicle
        /// is clear on no evidence is the one answer this must not give.
        /// </summary>
        private static (string Label, string Bg, string Stroke, string Color, string Icon) DedupeChip(ValuationDocument doc)
        {
            var matches = doc.DedupeCheck?.MatchCount;

            var label = matches is null ? "PENDING"
                      : matches == 0    ? "VERIFIED CLEAN"
                      : matches == 1    ? "1 PRIOR CASE"
                      :                   $"{matches} PRIOR CASES";

            var clean = matches == 0;
            var flag  = matches > 0;

            return (
                label,
                clean ? "#ECFDF5" : flag ? "#FFFBEB" : "#F1F5F9",
                clean ? "#A7F3D0" : flag ? "#FDE68A" : "#E2E8F0",
                clean ? MintText  : flag ? "#B45309" : "#64748B",
                clean ? @"<path d=""M8 12l3 3 5-6"" stroke-linecap=""round"" stroke-linejoin=""round""/>"
              : flag  ? @"<path d=""M12 7v6M12 16.5v.5"" stroke-linecap=""round""/>"
              :         @"<path d=""M8 12h8"" stroke-linecap=""round""/>"
            );
        }

        /// <summary>
        /// How the chassis punch reads on the cover, under the market value.
        ///
        /// Source is the QC officer's own entry — the chassis punch pills on the QC
        /// page write "Original" / "Re-Punched" / "Tampered" onto QualityControl,
        /// and the approver sees the same value on the final report page before
        /// approving. Nothing derives it, so a case where QC never recorded the
        /// punch prints NOT RECORDED rather than guessing at the metal.
        ///
        /// Colours follow severity: original is clean, re-punched is a caution, and
        /// tampered is a rejection — the same reading the QC pills give on screen.
        /// </summary>
        private static (string Label, string Bg, string Stroke, string Color) ChassisPunchChip(ValuationDocument doc)
        {
            // Two places record the same finding. The QC pills write the word onto
            // QualityControl.ChassisPunch AND the verdict onto the checklist's
            // "docChassis" — but a case reviewed before those were wired together
            // has only the checklist entry, and reading just the first field
            // printed NOT RECORDED on a report the portal showed as ORIGINAL.
            var raw = (doc.QualityControl?.ChassisPunch ?? string.Empty).Trim();
            if (raw.Length == 0)
                raw = (doc.QualityControl?.QcChecklist?.GetValueOrDefault("docChassis") ?? string.Empty).Trim();

            var key = raw.ToUpperInvariant().Replace("-", "").Replace(" ", "");

            return key switch
            {
                "ORIGINAL"  => ("ORIGINAL",     "#ECFDF5", "#A7F3D0", MintText),
                "REPUNCHED" => ("RE-PUNCHED",   "#FFFBEB", "#FDE68A", "#B45309"),
                "TAMPERED"  => ("TAMPERED",     "#FEF2F2", "#FECACA", "#DC2626"),
                ""          => ("NOT RECORDED", "#F1F5F9", "#E2E8F0", "#64748B"),
                _           => (raw.ToUpperInvariant(), "#F1F5F9", "#E2E8F0", "#64748B")
            };
        }

        /// <summary>
        /// How the VAHAN blacklist check reads on the report.
        ///
        /// Answers "is this vehicle blacklisted?", so YES is the bad answer and NO
        /// is the good one — the colours carry that, green for NO and red for YES,
        /// because a glance at this chip must not be able to invert its meaning.
        ///
        /// Source is the QC checklist's "valDedupe" — the key is historical and kept
        /// so saved verdicts are not orphaned; what it records is the blacklist flag
        /// VAHAN returned, which the reviewer can override on the QC page.
        /// Never checked reads PENDING rather than NO.
        /// </summary>
        private static (string Label, string Bg, string Stroke, string Color, string Icon) BlacklistChip(ValuationDocument doc)
        {
            var verdict = doc.QualityControl?.QcChecklist?.GetValueOrDefault("valDedupe");

            var listed = verdict == "fail";   // fail = VAHAN reports it blacklisted
            var clear  = verdict == "pass";

            return (
                clear ? "NO" : listed ? "YES" : "PENDING",
                clear ? "#ECFDF5" : listed ? "#FEF2F2" : "#F1F5F9",
                clear ? "#A7F3D0" : listed ? "#FECACA" : "#E2E8F0",
                clear ? MintText  : listed ? "#DC2626" : "#64748B",
                clear ? @"<path d=""M8 12l3 3 5-6"" stroke-linecap=""round"" stroke-linejoin=""round""/>"
              : listed ? @"<path d=""M9 9l6 6M15 9l-6 6"" stroke-linecap=""round""/>"
              :          @"<path d=""M8 12h8"" stroke-linecap=""round""/>"
            );
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
                        var trimmedPath   = Path.Combine(AppContext.BaseDirectory, "png", Theme.LogoTrimmed);
                        var absolutePath  = Path.Combine(AppContext.BaseDirectory, "png", Theme.LogoFull);
                        // Resolved against the output directory only. Working-directory
                        // fallbacks used to hide a missing asset on a dev machine while
                        // the deployed service rendered text instead of the logo.
                        string? logoPath  = File.Exists(trimmedPath)  ? trimmedPath
                                          : File.Exists(absolutePath) ? absolutePath
                                          : null;
                        if (logoPath != null)
                            logoRow.ConstantItem(140).Height(34).Image(logoPath).FitArea();
                        else
                            logoRow.AutoItem().Text(Theme.Name).FontSize(24).Bold().FontColor(BrandTeal);
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
                .Text(Theme.FooterNote)
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
                    // Original size, kept. The row is as tall as the right-hand column
                    // either way, so the photo costs the page nothing -- narrowing it only
                    // shrank the picture. Not ExtendVertical: that takes the whole
                    // remaining page and pushes every later section onto a second one.
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
                        layers.PrimaryLayer().Padding(6).Column(c =>
                        {
                            c.Item().AlignCenter()
                                .Text("OVERALL VEHICLE SCORE")
                                .FontSize(8).ExtraBold().FontColor(BrandTeal).LetterSpacing(0.04f);

                            var gaugeScore = CalculateOverallVehicleScore(doc);
                            // 64, not 82. The cover gained the ratings grid and the value
                            // section; this is the least-missed 18pt on the page.
                            var gaugeText = ScoreGaugeTextLayout(206.25f, 48f);
                            c.Item().Height(48).Layers(gaugeLayers =>
                            {
                                gaugeLayers.PrimaryLayer().AlignCenter()
                                    .Svg(size => GenerateScoreGaugeSvg(size, gaugeScore));
                                gaugeLayers.Layer().PaddingTop(gaugeText.NumTop).AlignCenter()
                                    .Text(gaugeScore.ToString("F1", CultureInfo.InvariantCulture))
                                    .FontSize(gaugeText.NumSize).ExtraBold().FontColor(BrandTeal);
                                gaugeLayers.Layer().PaddingTop(gaugeText.SubTop).AlignCenter()
                                    .Text("/ 10").FontSize(gaugeText.SubSize).Bold().FontColor("#9CA3AF");
                            });

                            // Was a hard-coded "VERIFIED CLEAN" on every report, whatever
                            // the case held. Now it states the duplicate check's result,
                            // same source as the DEDUPE chip further down the page.
                            var gaugeDedupe = DedupeChip(doc);
                            c.Item().AlignCenter().PaddingTop(2).Layers(badgeLayers =>
                            {
                                badgeLayers.Layer().Svg(s => {
                                    string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                    string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                    return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""6"" fill=""#FFFFFF"" stroke=""{gaugeDedupe.Color}"" stroke-width=""1.5""/></svg>";
                                });
                                badgeLayers.PrimaryLayer().PaddingVertical(4).PaddingHorizontal(12)
                                    .Text(gaugeDedupe.Label).FontSize(8).ExtraBold().FontColor(gaugeDedupe.Color);
                            });
                        });
                    });

                    cards.Item().PaddingVertical(3);

                    // INDIVIDUAL RATINGS. This replaced the four banded verdict boxes
                    // (CABIN / ENGINE / LOAD BODY / OTHER SYSTEMS): a figure per system
                    // instead of a word per area, from the same scoring call page 3 uses.
                    // The market value card that used to sit here has moved to its own
                    // section at the foot of the page.
                    cards.Item().Layers(layers =>
                    {
                        layers.Layer().Svg(size => {
                            string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                            string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                            return $@"<svg width=""{w}"" height=""{h}""><rect x=""0.5"" y=""0.5"" width=""{(size.Width - 1).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(size.Height - 1).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""12"" fill=""#FCFDFD"" stroke=""#E3E8EA"" stroke-width=""1""/></svg>";
                        });
                        layers.PrimaryLayer().Padding(5).Column(c =>
                        {
                            c.Item().PaddingBottom(3).Row(t =>
                            {
                                t.AutoItem().AlignMiddle().Width(10).Height(10).Svg(_ =>
                                    $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""{BrandTeal}"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/></svg>");
                                t.ConstantItem(5);
                                t.RelativeItem().AlignMiddle()
                                    .Text("CONDITION VERDICT \u2014 INDIVIDUAL RATINGS")
                                    .FontSize(7.5f).ExtraBold().FontColor(ValueDark).LetterSpacing(0.02f);
                            });

                            var ratings = SystemRatings(doc);
                            if (ratings.Count == 0)
                            {
                                c.Item().PaddingVertical(16).AlignCenter()
                                    .Text("NO INSPECTION DATA").FontSize(8).Bold().FontColor(LabelSlate);
                                return;
                            }

                            // Four across, as the mockup has it. A count that is not a
                            // multiple of four pads with empty cells, so the last row
                            // leaves gaps instead of stretching the tiles that remain.
                            const int Cols = 4;
                            c.Item().Table(tbl =>
                            {
                                tbl.ColumnsDefinition(cd =>
                                {
                                    for (int i = 0; i < Cols; i++) cd.RelativeColumn();
                                });
                                foreach (var (name, score) in ratings)
                                {
                                    var n = name; var sc = score;
                                    tbl.Cell().Element(cell => RatingTile(cell, n, sc));
                                }
                                for (int i = ratings.Count % Cols; i != 0 && i < Cols; i++)
                                    tbl.Cell();
                            });
                        });
                    });

                });
            });

            // Vehicle name and chassis punch share one line beneath the photo and
            // the value cards. Both were previously the last item of their own
            // column, which only lined them up by coincidence of column height —
            // a row makes it exact and keeps each box its column's width.
            main.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem(7).Layers(layers =>
                {
                    layers.Layer().Svg(s => {
                        string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                        string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                        // Tint follows the brand: this panel sits directly behind
                        // BrandTeal text, so a fixed Vehga tint would leave a teal
                        // plate under green lettering on a Pronto report.
                        return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""8"" fill=""{Theme.TintBg}""/></svg>";
                    });
                    layers.PrimaryLayer().PaddingVertical(5).PaddingHorizontal(6).AlignMiddle().AlignCenter()
                        .Text(vehicleName)
                        .FontSize(8).ExtraBold().FontColor(BrandTeal);
                });

                row.ConstantItem(12);

                // Chassis punch, as recorded at QC and confirmed at final report.
                var punch = ChassisPunchChip(doc);
                row.RelativeItem(5).Layers(layers =>
                {
                    layers.Layer().Svg(size => {
                        string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                        string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                        return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""6"" fill=""{punch.Bg}"" stroke=""{punch.Stroke}"" stroke-width=""1""/></svg>";
                    });
                    layers.PrimaryLayer().PaddingVertical(5).PaddingHorizontal(10).AlignMiddle().AlignCenter()
                        .Text(t =>
                        {
                            t.Span("CHASSIS PUNCH: ").FontSize(8).ExtraBold().FontColor("#64748B");
                            t.Span(punch.Label).FontSize(8).ExtraBold().FontColor(punch.Color);
                        });
                });
            });

            main.Item().PaddingVertical(1.5f);

            // One row of four, not two rows of two. Stacking each label over its value
            // lets all four sit side by side, which is part of what frees the vertical
            // space the ratings grid above now takes.
            var stripPlace = doc.InspectionDetails?.InspectionLocation?.ToUpper() ?? "-";
            main.Item().Border(1).BorderColor("#E5E7EB")
                .PaddingVertical(5).PaddingHorizontal(10).Row(row =>
            {
                // Not four equal quarters. CLIENT carries the longest value on the strip
                // by a wide margin -- EQUITAS SMALL FINANCE BANK wraps at a quarter width,
                // where a branch name and a date have room to spare.
                void Cell(float weight, string label, string? value, bool divider)
                {
                    row.RelativeItem(weight)
                       .BorderLeft(divider ? 1 : 0).BorderColor("#E5E7EB")
                       .PaddingLeft(divider ? 10 : 0)
                       .Element(c => StripField(c, label, value));
                }
                Cell(1.5f, "CLIENT",              doc.Stakeholder?.Name?.ToUpper() ?? "-", false);
                Cell(0.8f, "BRANCH",              stripPlace, true);
                Cell(0.85f, "DATE OF INSPECTION",
                     doc.InspectionDetails?.DateOfInspection?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? "-", true);
                Cell(0.95f, "PLACE OF INSPECTION", stripPlace, true);
            });

            main.Item().PaddingVertical(1);

            DrawSectionTitle(main.Item(), "ASSET IDENTITY",
                @"<path d=""M18.92 6.01C18.72 5.42 18.16 5 17.5 5h-11c-.66 0-1.21.42-1.42 1.01L3 12v8c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-1h12v1c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-8l-2.08-5.99zM6.5 16c-.83 0-1.5-.67-1.5-1.5S5.67 13 6.5 13s1.5.67 1.5 1.5S7.33 16 6.5 16zm11 0c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zM5 11l1.5-4.5h11L19 11H5z""/>",
                svgFill: true, pad: 3f);

            // Two columns of label-over-value, not four columns of label | value.
            // The mockup's layout, and it is also what retires the column-width juggling
            // the old table needed: each field now owns half the width instead of a
            // quarter, so a 29-character owner name has room without borrowing it from
            // the column beside it.
            main.Item().Border(1).BorderColor("#EEF2F6").Padding(5).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(); cd.ConstantColumn(16); cd.RelativeColumn();
                });

                var vd = doc.VehicleDetails;
                var pairs = new (string L, string? V, string R, string? RV)[]
                {
                    ("OWNER",            vd?.OwnerName,
                     "APPLICANT",        doc.Stakeholder?.Applicant?.Name),
                    ("CHASSIS NUMBER",   vd?.ChassisNumber,
                     "ENGINE NUMBER",    vd?.EngineNumber),
                    ("MANUFACTURE YEAR", ResolveMfgYear(vd),
                     "REGISTERED ON",    vd?.DateOfRegistration?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                    ("FUEL TYPE",        vd?.Fuel,
                     "TRANSMISSION",     doc.InspectionDetails?.TransmissionType),
                    ("COLOUR",           vd?.Colour,
                     "ODO METER",        vd?.Odometer?.ToString() ?? doc.InspectionDetails?.Odometer?.ToString()),
                    ("VEHICLE TYPE",     vd?.ClassOfVehicle,
                     "OWNERSHIP NUMBER", vd?.OwnerSerialNo?.ToString() ?? "1"),
                };

                for (int i = 0; i < pairs.Length; i++)
                {
                    var (l, v, r, rv) = pairs[i];
                    bool last = i == pairs.Length - 1;
                    // Rule between rows, not under the last one, so the list does not
                    // print a second line hard against the card's own border.
                    void Slot(string label, string? value) =>
                        table.Cell().BorderBottom(last ? 0 : 1).BorderColor("#F1F5F9")
                             .PaddingVertical(2)
                             .Element(c => AssetField(c, label, value));

                    Slot(l, v);
                    table.Cell();
                    Slot(r, rv);
                }
            });

            main.Item().PaddingVertical(1);

            DrawSectionTitle(main.Item(), "ESTIMATED MARKET VALUE",
                @"<circle cx=""12"" cy=""12"" r=""9""/><path d=""M12 7v10M9.5 9.2h5M9.5 11.4h5M13.2 9.2c1 0 1.6.8 1.6 1.7s-.7 1.7-1.8 1.7h-3l3.8 4.4""/>",
                svgFill: false, pad: 3f);

            main.Item().Row(valueRow =>
            {
                // The value card, which used to sit under the gauge at the top right.
                valueRow.RelativeItem(7).Layers(layers =>
                {
                    layers.Layer().Svg(size => {
                        string w = size.Width.ToString("F1", CultureInfo.InvariantCulture);
                        string h = size.Height.ToString("F1", CultureInfo.InvariantCulture);
                        return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""12"" fill=""{BrandTeal}""/></svg>";
                    });
                    layers.PrimaryLayer().PaddingVertical(7).PaddingHorizontal(13).Row(r =>
                    {
                        r.RelativeItem().AlignMiddle().Column(c =>
                        {
                            c.Item().Text("ESTIMATED MARKET VALUE")
                                .FontSize(8).Bold().FontColor(Colors.White).LetterSpacing(0.04f);
                            c.Item().PaddingTop(4)
                                .Text($"\u20b9 {FormatIndianCurrency(doc.QualityControl?.ValuationAmount ?? 0)}")
                                .FontSize(17).ExtraBold().FontColor(Colors.White);
                            c.Item().PaddingTop(3).Text("Calculated based on current market trends")
                                .FontSize(7).Italic().FontColor("#D6F0E2");
                        });
                        r.AutoItem().AlignMiddle().Width(30).Height(30).Layers(b =>
                        {
                            b.Layer().Svg(_ =>
                                @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 30 30""><circle cx=""15"" cy=""15"" r=""14"" fill=""#FFFFFF"" fill-opacity=""0.16""/></svg>");
                            b.PrimaryLayer().AlignCenter().AlignMiddle()
                                .Text("\u20b9").FontSize(13).ExtraBold().FontColor(Colors.White);
                        });
                    });
                });

                valueRow.ConstantItem(12);

                // Dedupe, blacklist and the two links, as one list of label/value rows
                // rather than four free-standing pills.
                valueRow.RelativeItem(5).Column(list =>
                {
                    var dedupe = DedupeChip(doc);
                    var black  = BlacklistChip(doc);

                    void ListRow(string label, string value, string valueColor, bool last)
                    {
                        list.Item().PaddingBottom(last ? 0 : 3).Layers(layers =>
                        {
                            layers.Layer().Svg(s2 => {
                                string w = s2.Width.ToString("F1", CultureInfo.InvariantCulture);
                                string h = s2.Height.ToString("F1", CultureInfo.InvariantCulture);
                                return $@"<svg width=""{w}"" height=""{h}""><rect x=""0.5"" y=""0.5"" width=""{(s2.Width - 1).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(s2.Height - 1).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""5"" fill=""#FFFFFF"" stroke=""#E5E7EB"" stroke-width=""1""/></svg>";
                            });
                            layers.PrimaryLayer().PaddingVertical(3.5f).PaddingHorizontal(10).Row(r =>
                            {
                                r.RelativeItem().AlignMiddle()
                                    .Text(label).FontSize(8).ExtraBold().FontColor(ValueDark);
                                r.AutoItem().AlignMiddle()
                                    .Text(value).FontSize(8).ExtraBold().FontColor(valueColor);
                            });
                        });
                    }

                    ListRow("DEDUPE",      dedupe.Label, dedupe.Color, false);
                    ListRow("BLACKLIST",   black.Label,  black.Color,  false);

                    // The images row links to the browser gallery uploaded beside the PDF,
                    // and reads NOT AVAILABLE when there is none -- the same behaviour as
                    // the video row, deliberately, rather than the internal jump to this
                    // report's own gallery pages that it used to fall back to.
                    //
                    // It was briefly wired to doc.ImagesUrl, which is a different field
                    // and is not populated, so the row printed NOT AVAILABLE on every
                    // report whether a gallery existed or not.
                    string? galleryUrl = (_blobContainer != null && !string.IsNullOrWhiteSpace(_blobBaseUrl))
                        ? $"{_blobBaseUrl.TrimEnd('/')}/{_blobContainer.Name}/{referenceNumber}-gallery.html"
                        : null;

                    string? videoUrl = null;
                    doc.VideoUrls?.TryGetValue("VehicleVideo", out videoUrl);

                    void LinkRow(string label, Func<IContainer, IContainer>? link, string valueText, bool last)
                    {
                        var item = list.Item().PaddingBottom(last ? 0 : 3);
                        if (link != null) item = link(item);
                        item.Layers(layers =>
                        {
                            layers.Layer().Svg(s2 => {
                                string w = s2.Width.ToString("F1", CultureInfo.InvariantCulture);
                                string h = s2.Height.ToString("F1", CultureInfo.InvariantCulture);
                                return $@"<svg width=""{w}"" height=""{h}""><rect x=""0.5"" y=""0.5"" width=""{(s2.Width - 1).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(s2.Height - 1).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""5"" fill=""#FFFFFF"" stroke=""#E5E7EB"" stroke-width=""1""/></svg>";
                            });
                            layers.PrimaryLayer().PaddingVertical(3.5f).PaddingHorizontal(10).Row(r =>
                            {
                                r.RelativeItem().AlignMiddle()
                                    .Text(label).FontSize(8).ExtraBold().FontColor(ValueDark);
                                r.AutoItem().AlignMiddle().Text(valueText).FontSize(8).ExtraBold()
                                    .FontColor(link == null ? LabelSlate : "#1D4ED8");
                            });
                        });
                    }

                    LinkRow("VIDEO LINK",
                        string.IsNullOrWhiteSpace(videoUrl) ? null : c => c.Hyperlink(videoUrl!),
                        string.IsNullOrWhiteSpace(videoUrl) ? "NOT AVAILABLE" : "VIEW \u00bb", false);

                    LinkRow("IMAGES LINK",
                        string.IsNullOrWhiteSpace(galleryUrl) ? null : c => c.Hyperlink(galleryUrl!),
                        string.IsNullOrWhiteSpace(galleryUrl) ? "NOT AVAILABLE" : "VIEW \u00bb", true);
                });
            });

            main.Item().PaddingVertical(3);

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
                        c.Item().PaddingTop(4)
                            .Text($"\"{doc.QualityControl?.Remarks ?? "Vehicle found in good road worthy condition."}\"")
                            .FontSize(8.5f).Italic().FontColor("#374151").LineHeight(1.3f);
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
                    layers.PrimaryLayer().PaddingTop(5).PaddingBottom(5).Column(c =>
                    {
                        if (qrCode.Length > 0)
                        {
                            var verifyUrl = $"https://prontofirebase.web.app/verify/{referenceNumber}";
                            c.Item().AlignCenter().Width(36).Height(36).Hyperlink(verifyUrl).Image(qrCode).FitArea();
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
                            return $@"<svg width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""6"" fill=""#F0FDF4"" stroke=""#A7F3D0"" stroke-width=""1""/></svg>";
                        });
                        badge.PrimaryLayer().PaddingVertical(5).PaddingHorizontal(10).Row(r =>
                        {
                            r.AutoItem().AlignMiddle()
                                .Text("AUDIT STATUS: CERTIFIED")
                                .FontSize(7).ExtraBold().FontColor(MintText);
                            r.ConstantItem(6);
                            r.AutoItem().AlignMiddle().Width(10).Height(10)
                                .Svg(_ => @"<svg viewBox=""0 0 24 24"" fill=""none"" stroke=""#059669"" stroke-width=""2""><path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/><path d=""M9 12l2 2 4-4""/></svg>");
                        });
                    });

                    // The approver's valuer licence, the same on every report — it
                    // identifies the person who signed off, not the case.
                    col.Item().PaddingTop(3).AlignRight()
                        .Text($"License No: {ApproverLicenseNo}").FontSize(8).SemiBold().FontColor(LabelSlate);
                });
            });
        }

        /// <summary>
        /// Section heading. <paramref name="pad"/> is the space above and below it: the
        /// cover runs tighter than the Vahan page, which has room to spare.
        /// </summary>
        private void DrawSectionTitle(IContainer container, string title, string iconPathsSvg, bool svgFill,
            float pad = 5f)
        {
            string strokeAttr = svgFill
                ? $@"fill=""{BrandTeal}"" stroke=""none"""
                : $@"fill=""none"" stroke=""{BrandTeal}"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round""";

            container.PaddingBottom(pad).PaddingTop(pad).Row(row => 
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
                // NOC DETAILS and CHALLAN DETAILS sat here as permanent "---": neither has
                // a source field in SurepassRcResponse. Replaced with two values that do,
                // so the client's requested rows cost no extra height.
                AddVahanRow(col, i++, "VEHICLE COLOR",         vd?.Colour,                       "RC STATUS",             ResolveRcStatus(vd));
                // Was TAX VALID UPTO. Road tax now has its own status card below, where a
                // life-time-tax vehicle can read LIFE TIME instead of a blank date, so this
                // slot carries the PUC expiry -- mapped from VAHAN and previously unused.
                AddVahanRow(col, i++, "REGISTERED AT RTO",     vd?.Rto,                          "PUC VALID UPTO",        FormatPucUpto(vd));
                AddVahanAddressRow(col, i++, "PRESENT ADDRESS",   vd?.PresentAddress);
                AddVahanAddressRow(col, i++, "PERMANENT ADDRESS", vd?.PermanentAddress);
            });

            main.Item().PaddingTop(14).PaddingBottom(10).Element(c => 
                DrawSectionTitle(c, "REGULATORY DOCUMENTS & INSURANCE",
                    @"<path d=""M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z""/>",
                    svgFill: false)
            );

            bool hasLien = vd?.Hypothecation ?? false;

            var insStat = DocumentStatus(vd?.InsurancePolicyNo, vd?.InsuranceValidUpTo);
            // The card is titled COMPREHENSIVE INSURANCE but never named the insurer,
            // which is mapped from VAHAN and was simply not being read.
            var insurer = vd?.Insurer?.Trim();
            var insParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(insurer)) insParts.Add(insurer!);
            insParts.Add(string.IsNullOrWhiteSpace(vd?.InsurancePolicyNo)
                ? "Policy: ---"
                : $"Policy: {vd!.InsurancePolicyNo}");
            if (vd?.IDV != null) insParts.Add($"IDV: Rs. {vd.IDV?.ToString("N0")}");
            var insDetails = string.Join(" | ", insParts);

            var permitStat = DocumentStatus(vd?.PermitNo, vd?.PermitValidUpTo);
            var permitDetails = string.IsNullOrWhiteSpace(vd?.PermitNo)
                ? "---"
                : string.IsNullOrWhiteSpace(vd?.PermitType) ? vd!.PermitNo! : $"{vd!.PermitType} | {vd.PermitNo}";

            var fitStat = DocumentStatus(vd?.FitnessNo, vd?.FitnessValidTo);
            var fitDetails = string.IsNullOrWhiteSpace(vd?.FitnessNo) ? "" : $"Certificate: {vd!.FitnessNo}";

            var taxStat = TaxStatus(vd);

            // Two across. Stacked full-width, five document cards ran most of the page for
            // a line of text each; paired, they take half the height and leave the page
            // room for the chassis evidence beneath them.
            //
            // Spacing lives on the table, not as PaddingBottom on each card. A trailing
            // 12pt on the LAST card is space nothing occupies, but QuestPDF still has to
            // fit it: on a report whose RTO value wrapped, the final chassis card ended at
            // 787pt against a ~796 limit and that padding pushed the requirement to 799,
            // so the card moved to a page of its own and renumbered every page after it.
            main.Item().Table(grid =>
            {
                grid.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(); cd.ConstantColumn(12); cd.RelativeColumn();
                });

                var cards = new List<Action<IContainer>>
                {
                    c => AddRegulatoryCardSimple(c, "COMPREHENSIVE INSURANCE",
                             insDetails, insStat.Status, insStat.Expiry, insStat.Warn),
                    c => AddRegulatoryCardSimple(c, "HYPOTHECATION (STATUS)",
                             hasLien ? "LIEN DETECTED" : "CLEAN / NO LIEN DETECTED",
                             hasLien ? "LIEN" : "FREE OF LIEN",
                             hasLien ? "" : "READY FOR TRANSFER", hasLien),
                    c => AddRegulatoryCardSimple(c, "NATIONAL PERMIT",
                             permitDetails, permitStat.Status, permitStat.Expiry, permitStat.Warn),
                    c => AddRegulatoryCardSimple(c, "FITNESS CERTIFICATE",
                             fitDetails, fitStat.Status, fitStat.Expiry, fitStat.Warn),
                    c => AddRegulatoryCardSimple(c, "TAX VALIDITY",
                             "Road tax / LTT status", taxStat.Status, taxStat.Expiry, taxStat.Warn),
                };

                for (int i = 0; i < cards.Count; i++)
                {
                    var draw = cards[i];
                    // No ExtendVertical here: on a table cell it makes each card claim the
                    // rest of the page, which paginates the section into four.
                    grid.Cell().PaddingBottom(i < cards.Count - 2 ? 12 : 0)
                        .Element(c => draw(c));
                    if (i % 2 == 0) grid.Cell();   // the gap column
                }
                // Odd card count leaves the last slot empty rather than stretching a card
                // across a width its one line of text cannot carry.
                if (cards.Count % 2 != 0) { grid.Cell(); grid.Cell(); }
            });

            // Full width, both of them: these are wide strips of a chassis number, and at
            // half width the digits stop being legible -- which is the only thing the
            // reader is looking at them for.
            main.Item().PaddingTop(12).Column(col =>
            {
                col.Spacing(12);
                AddRegulatoryCardWithPhoto(col, "CHASSIS VERIFICATION",  "VERIFIED", photos, "ChassisVerification");
                AddRegulatoryCardWithPhoto(col, "CHASSIS STENCIL TRACE", "",         photos, "ChassisStencilTrace");
            });
        }

        /// <summary>
        /// Road tax status for its own card.
        ///
        /// Separate from <see cref="DocumentStatus"/> because a life-time-tax vehicle has
        /// no expiry at all: VAHAN answers "LTT" rather than a date, and forcing that
        /// through the date path would read as a missing document on a vehicle whose tax
        /// can never lapse.
        /// </summary>
        private static (string Status, string Expiry, bool Warn) TaxStatus(VehicleDetailsDto? vd)
        {
            var raw = vd?.TaxPaidUpto?.Trim();
            if (!string.IsNullOrWhiteSpace(raw) && raw.Equals("LTT", StringComparison.OrdinalIgnoreCase))
                return ("LIFE TIME", "", false);

            if (vd?.TaxUpto is DateTime d)
                return (d.Date >= DateTime.UtcNow.Date ? "ACTIVE" : "EXPIRED",
                        $"EXP: {d.ToString("MMM yyyy", CultureInfo.InvariantCulture).ToUpper()}",
                        d.Date < DateTime.UtcNow.Date);

            if (!string.IsNullOrWhiteSpace(raw)) return ("ON RECORD", "", false);
            return ("---", "EXP: ---", true);
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

        private void AddRegulatoryCardSimple(IContainer cell,
            string title, string details, string status, string expiry, bool isWarning = false)
        {
            // 52pt clears a two-line detail string, which is what the insurance card
            // carries once insurer, policy and IDV are all present. A floor rather than a
            // fixed height: the pair on a row then matches, and a card that still needs
            // more room grows instead of clipping.
            cell.MinHeight(52).Layers(cardLayers =>
            {
                cardLayers.Layer().Svg(s => {
                    string wStr = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                    string hStr = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                    return $@"<svg width=""{wStr}"" height=""{hStr}"">
                                <rect x=""0.5"" y=""0.5"" width=""{(s.Width - 1).ToString("F1", CultureInfo.InvariantCulture)}"" height=""{(s.Height - 1).ToString("F1", CultureInfo.InvariantCulture)}"" rx=""8"" fill=""#FFFFFF"" stroke=""#E5E7EB"" stroke-width=""1""/>
                              </svg>";
                });

                cardLayers.PrimaryLayer().PaddingVertical(9).PaddingHorizontal(10).Row(row =>
                {
                    string strokeColor = isWarning ? "#F59E0B" : BrandTeal;
                    string fillColor   = isWarning ? "#FFFBEB" : "#F0FDF4";

                    row.ConstantItem(26).AlignMiddle().AlignCenter().Width(16).Height(16)
                        .Svg(_ => $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"">
                                        <circle cx=""12"" cy=""12"" r=""11"" fill=""{fillColor}"" stroke=""{strokeColor}"" stroke-width=""1""/>
                                        <polyline points=""7,12.5 10.5,16 17,8"" fill=""none"" stroke=""{strokeColor}"" stroke-width=""1.5""/>
                                     </svg>");

                    row.RelativeItem().AlignMiddle().Column(c =>
                    {
                        c.Item().Text(title).FontSize(9).Bold().FontColor(ValueDark);
                        if (!string.IsNullOrEmpty(details))
                            c.Item().PaddingTop(2).Text(details).FontSize(7.5f).FontColor(LabelSlate);
                    });

                    row.AutoItem().AlignMiddle().AlignRight().PaddingLeft(6).Column(c =>
                    {
                        if (!string.IsNullOrEmpty(status) && status != "---")
                        {
                            string bg    = isWarning ? "#FFF3E0" : "#ECFDF5";
                            string color = isWarning ? "#FB8C00" : "#059669";
                            c.Item().AlignRight().Background(bg).PaddingVertical(3).PaddingHorizontal(8)
                                .Text(status).FontSize(8).Bold().FontColor(color);
                        }
                        if (!string.IsNullOrEmpty(expiry) && expiry != "EXP: ---")
                            c.Item().AlignRight().PaddingTop(3).Text(expiry).FontSize(8).FontColor(LabelSlate);
                        else if (expiry == "EXP: ---")
                            c.Item().AlignRight().PaddingTop(3).Text("---").FontSize(8).FontColor(LabelSlate);
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
            col.Item().Layers(cardLayers =>
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
                        c.Item().Text(title).FontSize(10).Bold().FontColor(ValueDark);
                        if (!string.IsNullOrEmpty(details))
                            c.Item().PaddingTop(3).Text(details).FontSize(8).FontColor(LabelSlate);
                    });

                    row.RelativeItem(7).AlignMiddle().AlignRight().Element(c =>
                    {
                        byte[]? raw = photos.GetValueOrDefault(photoKey)
                                   ?? photos.GetValueOrDefault("ChassisNumberPlate");
                        byte[]? photo = raw != null ? CropCenterFocus(raw) : null;
                        if (photo != null)
                            c.Height(45).AlignCenter().AlignMiddle().Image(photo).FitArea();
                        else
                            c.AlignCenter().AlignMiddle().Text("NO IMAGE").FontSize(9).FontColor(LabelSlate);
                    });
                });
            });
        }

        // ──────────────────────────────────────────────
        // PAGE 3 — System Scores
        // ──────────────────────────────────────────────

        // ── PDF inspection-field registry (mirrors inspection-field-registry.ts) ──
        // Every field is scored. Where an item does not apply to the vehicle the
        // inspector answers N/A, which MapVerdict excludes from the average — that
        // is the lever for "not a defect", rather than exempting field names here.
        /// <summary>
        /// One row on a system card. <paramref name="Scored"/> false means the value is
        /// printed but excluded from the card's score — mirrors `scored` on
        /// InspectionField in the portal's inspection-field-registry.ts, which must be
        /// changed in the same commit or screen and report will disagree.
        /// </summary>
        private record FieldDef(string Label, string Key, bool Scored = true);
        private record SectionDef(string Name, FieldDef[] Fields);

        /// <summary>
        /// Sections printed but excluded from scoring, mirroring `scored: false` on
        /// InspectionSection in the portal registry.
        ///
        /// OTHER SYSTEMS lists accessories and fitments — air conditioning, crash guards,
        /// a load carrier. Whether one was ever fitted is a fact about how the vehicle was
        /// built, not a judgement of its condition, so a vehicle that never had a crash
        /// guard should not score below one that does.
        /// </summary>
        private static readonly HashSet<string> UnscoredSections =
            new(StringComparer.OrdinalIgnoreCase) { "OTHER SYSTEMS" };

        private static bool IsScored(SectionDef sec) => !UnscoredSections.Contains(sec.Name);

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
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs", Scored: false),
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
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs", Scored: false),
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
                    // Present in the VEHGA checklist (2W sheet, row 6) and collected by
                    // the portal; it was missing here, so it was never printed or scored.
                    new("CABIN ASSY","cabinAssy"),
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
                    new("BRAKE LEVERS / FLUID","brakeLeversFluid"), new("ABS","abs", Scored: false),
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
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs", Scored: false),
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
                    new("PARKING BRAKE","parkingBrake"), new("ABS","abs", Scored: false),
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

        /// <summary>
        /// Which inspection registry a case is printed and scored against.
        ///
        /// The vehicle type lives in VehicleSegment ("two-wheeler", "bus", …).
        /// ValuationType holds Retail or Repo — the inspection type, not the
        /// vehicle — and matches none of the cases in NormVehicleTypeKey, so
        /// reading it alone resolved EVERY report to "cv" whatever was actually
        /// inspected: a two-wheeler printed CV sections (load body, tail gate)
        /// that the AVO was never asked about, while the 2W fields they did fill
        /// in never appeared. ValuationType stays last only as a safety net for a
        /// case whose segment was never set.
        ///
        /// Mirrors resolveVehicleType() in the portal's inspection pages, which
        /// has always fallen back to vehicleSegment for exactly this reason.
        /// </summary>
        private static string ResolveVehicleTypeKey(ValuationDocument doc) =>
            NormVehicleTypeKey(FirstNonBlank(
                doc.VehicleSegment,
                doc.Stakeholder?.VehicleSegment,
                doc.Stakeholder?.ValuationType));

        /// <summary>First value that is neither null nor blank. A segment stored
        /// as "" is as absent as one stored as null, so ?? alone is not enough.</summary>
        private static string? FirstNonBlank(params string?[] values) =>
            values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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

            var vk = ResolveVehicleTypeKey(doc);
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

            // Every field is listed; only the scored ones move the number.
            Dictionary<string, string?>? BuildScoreItems(SectionDef sec) =>
                IsScored(sec) ? ScorableItems(sec, ins) : null;

            main.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    foreach (var sec in leftSections)
                        col.Item().Element(c => DrawSystemCard(c, GetSectionIcon(sec.Name), sec.Name, BuildItems(sec), BuildScoreItems(sec)));
                });

                row.ConstantItem(8);

                row.RelativeItem().Column(col =>
                {
                    foreach (var sec in rightSections)
                        col.Item().Element(c => DrawSystemCard(c, GetSectionIcon(sec.Name), sec.Name, BuildItems(sec), BuildScoreItems(sec)));
                });
            });

            // OTHER SYSTEMS spans full width with fields in 2 columns
            main.Item().Element(c => DrawSystemCard(c, SVGIcons.Other, otherSection.Name,
                BuildItems(otherSection), BuildScoreItems(otherSection), twoColumns: true));
        }

        /// <summary>
        /// One system card. <paramref name="scoreItems"/> is the subset of
        /// <paramref name="items"/> that counts toward the score; pass null for a section
        /// that is not scored at all, and the badge says so instead of printing a figure.
        /// </summary>
        private void DrawSystemCard(IContainer container, string iconSvg, string title,
            Dictionary<string, string?> items, Dictionary<string, string?>? scoreItems,
            bool twoColumns = false)
        {
            var scoreStr = scoreItems is null
                ? "NOT SCORED"
                : $"SCORE: {GetScoreDisplayFromDouble(CalculateSystemScore(scoreItems)).Score}";

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
                            .Text(title).FontColor(ValueDark).SemiBold().FontSize(9f);

                        row.AutoItem().Height(14).Layers(badgeLayers =>
                        {
                            badgeLayers.Layer().Svg(s => {
                                string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""7"" fill=""{ValueDark}""/></svg>";
                            });
                            badgeLayers.PrimaryLayer().PaddingHorizontal(6).AlignCenter().AlignMiddle()
                                .Text(scoreStr).FontColor(Colors.White).FontSize(7f).Bold();
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
                            else if (verdict is "POOR" or "BAD" or "NO" or "DAMAGED" or "MISSING")
                                                                         { pillBg = "#FEF2F2"; pillFg = "#DC2626"; }
                            else                                         { pillBg = "#F1F5F9"; pillFg = "#64748B"; }

                            void BuildCell(IContainer c)
                            {
                                c.PaddingVertical(2.5f).Row(r =>
                                {
                                    r.RelativeItem().AlignMiddle()
                                        .Text(item.Key).FontSize(8f).FontColor(LabelSlate);

                                    r.AutoItem().Layers(vl =>
                                    {
                                        vl.Layer().Svg(s => {
                                            string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                                            string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                                            return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""4"" fill=""{pillBg}""/></svg>";
                                        });
                                        vl.PrimaryLayer().PaddingVertical(2).PaddingHorizontal(6)
                                            .AlignCenter().AlignMiddle()
                                            .Text(verdict).FontSize(8f).Bold().FontColor(pillFg);
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

        /// <summary>
        /// Row glyphs, taken from Lucide (ISC licence) at their native 24x24 geometry --
        /// the same set the report mockup uses, so these are the upstream paths rather
        /// than a redrawing of a screenshot.
        ///
        /// Inner markup only: the stroke colour is the brand's, which is not known until
        /// a document is composing, so these cannot be whole-SVG constants like
        /// <see cref="SVGIcons"/>. Lucide art fills roughly 2..22 of the viewBox, so the
        /// chip supplies the margin -- see LabelIconPad.
        /// </summary>
        private static class RowIcons
        {
            // lucide/user-round
            public const string Owner        = @"<circle cx=""12"" cy=""8"" r=""5""/><path d=""M20 21a8 8 0 0 0-16 0""/>";
            // lucide/user-round-check
            public const string Applicant    = @"<path d=""M2 21a8 8 0 0 1 13.292-6""/><circle cx=""10"" cy=""8"" r=""5""/><path d=""m16 19 2 2 4-4""/>";
            // lucide/shield-check
            public const string Chassis      = @"<path d=""M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z""/><path d=""m9 12 2 2 4-4""/>";
            // lucide/cog
            public const string Engine       = @"<path d=""M11 10.27 7 3.34""/><path d=""m11 13.73-4 6.93""/><path d=""M12 22v-2""/><path d=""M12 2v2""/><path d=""M14 12h8""/><path d=""m17 20.66-1-1.73""/><path d=""m17 3.34-1 1.73""/><path d=""M2 12h2""/><path d=""m20.66 17-1.73-1""/><path d=""m20.66 7-1.73 1""/><path d=""m3.34 17 1.73-1""/><path d=""m3.34 7 1.73 1""/><circle cx=""12"" cy=""12"" r=""2""/><circle cx=""12"" cy=""12"" r=""8""/>";
            // lucide/calendar
            public const string MfgYear      = @"<path d=""M8 2v3""/><path d=""M16 2v3""/><rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><path d=""M3 9h18""/>";
            // lucide/calendar-check
            public const string Registered   = @"<path d=""M8 2v3""/><path d=""M16 2v3""/><rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><path d=""M3 9h18""/><path d=""m9 15 2 2 4-4""/>";
            // lucide/fuel
            public const string Fuel         = @"<path d=""M14 13h2a2 2 0 0 1 2 2v2a2 2 0 0 0 4 0v-6.998a2 2 0 0 0-.59-1.42L18 5""/><path d=""M14 21V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v16""/><path d=""M2 21h13""/><path d=""M3 9h11""/>";
            // tabler/manual-gearbox (MIT) -- the shift pattern, not Lucide's sliders, which
            // read as "settings" rather than as a gearbox. Tabler draws on the same 24x24
            // grid at stroke 2, so it sits beside the Lucide glyphs without adjustment.
            public const string Transmission = @"<path d=""M3 6a2 2 0 1 0 4 0a2 2 0 1 0 -4 0""/><path d=""M10 6a2 2 0 1 0 4 0a2 2 0 1 0 -4 0""/><path d=""M17 6a2 2 0 1 0 4 0a2 2 0 1 0 -4 0""/><path d=""M3 18a2 2 0 1 0 4 0a2 2 0 1 0 -4 0""/><path d=""M10 18a2 2 0 1 0 4 0a2 2 0 1 0 -4 0""/><path d=""M5 8l0 8""/><path d=""M12 8l0 8""/><path d=""M19 8v2a2 2 0 0 1 -2 2h-12""/>";
            // lucide/palette. Its four dots are r=.5 filled; stroked at r=.4 they read the
            // same at this size and need no separate fill colour threaded through.
            public const string Colour       = @"<path d=""M12 22a1 1 0 0 1 0-20 10 9 0 0 1 10 9 5 5 0 0 1-5 5h-2.25a1.75 1.75 0 0 0-1.4 2.8l.3.4a1.75 1.75 0 0 1-1.4 2.8z""/><circle cx=""13.5"" cy=""6.5"" r="".4""/><circle cx=""17.5"" cy=""10.5"" r="".4""/><circle cx=""6.5"" cy=""12.5"" r="".4""/><circle cx=""8.5"" cy=""7.5"" r="".4""/>";
            // lucide/gauge
            public const string Odometer     = @"<path d=""m12 14 4-4""/><path d=""M3.34 19a10 10 0 1 1 17.32 0""/>";
            // lucide/car
            public const string VehicleType  = @"<path d=""M19 17h2c.6 0 1-.4 1-1v-3c0-.9-.7-1.7-1.5-1.9C18.7 10.6 16 10 16 10s-1.3-1.4-2.2-2.3c-.5-.4-1.1-.7-1.8-.7H5c-.6 0-1.1.4-1.4.9l-1.4 2.9A3.7 3.7 0 0 0 2 12v4c0 .6.4 1 1 1h2""/><circle cx=""7"" cy=""17"" r=""2""/><path d=""M9 17h6""/><circle cx=""17"" cy=""17"" r=""2""/>";
            // lucide/award
            public const string Ownership    = @"<path d=""m15.477 12.89 1.515 8.526a.5.5 0 0 1-.81.47l-3.58-2.687a1 1 0 0 0-1.197 0l-3.586 2.686a.5.5 0 0 1-.81-.469l1.514-8.526""/><circle cx=""12"" cy=""8"" r=""6""/>";
            // lucide/building
            public const string Building     = @"<path d=""M12 10h.01""/><path d=""M12 14h.01""/><path d=""M12 6h.01""/><path d=""M16 10h.01""/><path d=""M16 14h.01""/><path d=""M16 6h.01""/><path d=""M8 10h.01""/><path d=""M8 14h.01""/><path d=""M8 6h.01""/><path d=""M9 22v-3a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v3""/><rect x=""4"" y=""2"" width=""16"" height=""20"" rx=""2""/>";
            // lucide/map-pin
            public const string Pin          = @"<path d=""M20 10c0 4.993-5.539 10.193-7.399 11.799a1 1 0 0 1-1.202 0C9.539 20.193 4 14.993 4 10a8 8 0 0 1 16 0""/><circle cx=""12"" cy=""10"" r=""3""/>";

            /// <summary>Neutral mark for a label with no entry in <see cref="LabelIcons"/>.</summary>
            public const string Field        = @"<path d=""M4 6h16M4 12h16M4 18h10""/>";
        }

        /// <summary>
        /// Label to glyph. A label with no entry falls back to a neutral mark rather than
        /// to nothing: an iconless row would pull its text left of every neighbour and
        /// break the column, which is worse than a generic mark.
        ///
        /// BRANCH and PLACE OF INSPECTION share a pin, and DATE OF INSPECTION reuses the
        /// same calendar as MANUFACTURE YEAR.
        /// </summary>
        private static readonly Dictionary<string, string> LabelIcons =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["CLIENT"]              = RowIcons.Building,
                ["BRANCH"]              = RowIcons.Pin,
                ["DATE OF INSPECTION"]  = RowIcons.MfgYear,
                ["PLACE OF INSPECTION"] = RowIcons.Pin,

                ["OWNER"]               = RowIcons.Owner,
                ["APPLICANT"]           = RowIcons.Applicant,
                ["CHASSIS NUMBER"]      = RowIcons.Chassis,
                ["ENGINE NUMBER"]       = RowIcons.Engine,
                ["MANUFACTURE YEAR"]    = RowIcons.MfgYear,
                ["REGISTERED ON"]       = RowIcons.Registered,
                ["FUEL TYPE"]           = RowIcons.Fuel,
                ["TRANSMISSION"]        = RowIcons.Transmission,
                ["COLOUR"]              = RowIcons.Colour,
                ["ODO METER"]           = RowIcons.Odometer,
                ["VEHICLE TYPE"]        = RowIcons.VehicleType,
                ["OWNERSHIP NUMBER"]    = RowIcons.Ownership,
            };

        private const float LabelIconBox = 15f;
        // Lucide art runs to the edge of its viewBox, so the inset is ours to supply;
        // at 2.2 on a 15pt chip the glyph lands at the proportion the mockup uses.
        private const float LabelIconPad = 2.2f;
        // Native Lucide weight. Scaled into the drawn area this lands at ~0.88pt, which
        // is the stroke the mockup's own icons carry.
        private const float IconStroke   = 2f;

        /// <summary>Brand-stroked SVG for one row glyph.</summary>
        private static string RowGlyph(string label)
        {
            var body = LabelIcons.TryGetValue(label, out var found) ? found : RowIcons.Field;
            return $@"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"" fill=""none"" stroke=""{BrandTeal}"" stroke-width=""{IconStroke.ToString("F1", CultureInfo.InvariantCulture)}"" stroke-linecap=""round"" stroke-linejoin=""round"">{body}</svg>";
        }

        /// <summary>Tinted chip with the row's glyph centred in it.</summary>
        private void IconChip(IContainer cell, string label, float box)
        {
            var glyph = RowGlyph(label);
            cell.Width(box).Height(box).Layers(l =>
            {
                l.Layer().Svg(s =>
                {
                    string w = s.Width.ToString("F1", CultureInfo.InvariantCulture);
                    string h = s.Height.ToString("F1", CultureInfo.InvariantCulture);
                    string r = (box / 3.2f).ToString("F1", CultureInfo.InvariantCulture);
                    return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w}"" height=""{h}""><rect width=""{w}"" height=""{h}"" rx=""{r}"" fill=""{Theme.TintBg}""/></svg>";
                });
                l.PrimaryLayer().Padding(box * LabelIconPad / LabelIconBox).Svg(_ => glyph);
            });
        }

        /// <summary>
        /// One ASSET IDENTITY field: icon chip on the left, label over value on the right.
        ///
        /// Replaces the old four-column table, where the label sat in its own column and
        /// the value was right-aligned in the next. Stacking them halves the number of
        /// columns, which is what buys a 29-character owner name room to sit on one line
        /// without the column juggling that layout needed.
        /// </summary>
        private void AssetField(IContainer cell, string label, string? value)
        {
            cell.Row(row =>
            {
                row.AutoItem().AlignMiddle().Element(c => IconChip(c, label, LabelIconBox));
                row.ConstantItem(7);
                row.RelativeItem().AlignMiddle().Column(c =>
                {
                    c.Item().Text(label).FontSize(7).Bold().FontColor(LabelSlate).LetterSpacing(0.03f);
                    c.Item().PaddingTop(1)
                        .Text(SafeFormat(value?.ToUpper())).FontSize(9).ExtraBold().FontColor(ValueDark);
                });
            });
        }

        /// <summary>
        /// One client-strip cell. Same shape as <see cref="AssetField"/> on a smaller
        /// chip: the strip splits one row four ways, so it has a fraction of the width
        /// an asset row gets.
        /// </summary>
        private void StripField(IContainer cell, string label, string? value)
        {
            cell.Row(row =>
            {
                row.AutoItem().AlignMiddle().Element(c => IconChip(c, label, 13f));
                row.ConstantItem(5);
                row.RelativeItem().AlignMiddle().Column(c =>
                {
                    c.Item().Text(label).FontSize(7).Bold().FontColor(LabelSlate).LetterSpacing(0.03f);
                    c.Item().PaddingTop(1)
                        .Text(SafeFormat(value?.ToUpper())).FontSize(9).ExtraBold().FontColor(ValueDark);
                });
            });
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

            // Tyres get their own unlabeled row below, and the chassis identification
            // photos already render on page 2's regulatory cards.
            var tyreSlotKeys = new[] { "TireFrontLeft", "TireFrontRight", "TireRearLeft", "TireRearRight" };
            var reservedKeys = new HashSet<string>(
                GalleryPhotoSlots.SelectMany(s => s.Keys)
                    .Concat(tyreSlotKeys)
                    .Concat(new[] { "ChassisVerification", "ChassisStencilTrace" }));

            // Any other uploaded photo QC ticked — an alternate for a slot already
            // filled, or a type the named slots never covered (Underbody, seats,
            // working/operation shots, custom photos). Without this they could be
            // selected on the QC page but never reached the report.
            var extraSlots = photos.Keys
                .Where(k => !reservedKeys.Contains(k))
                .Where(k => selectedKeys != null && selectedKeys.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => (Label: HumanizePhotoKey(k), Keys: new[] { k }))
                .ToArray();

            // All labeled gallery photos flow through one continuous table (2 per row).
            // QuestPDF paginates a Table's rows across pages automatically, so this
            // naturally packs ~6 photos per page instead of forcing fixed category
            // pages that leave mostly-empty pages when few photos are selected.
            RenderPhotoTable(main.Item().PaddingTop(4).Section("PhotoGalleryTarget"),
                GalleryPhotoSlots.Concat(extraSlots).ToArray(), doc, photos, selectedKeys);

            // Tyres: all uploaded (and selected, if a selection is set) tyre photos in one unlabeled row of four.
            // Flows directly after the photo table — same page if there's room, a new page otherwise.
            var tyrePhotos = tyreSlotKeys
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


        /// <summary>
        /// A single label/value row spanning the full width.
        ///
        /// An Indian postal address is 80-120 characters and would wrap to three or four
        /// lines inside AddVahanRow's right-aligned quarter-width column — which is
        /// exactly the overflow that once renumbered every page after this one. Left
        /// aligned, given the whole width, and hard-capped so a pathological VAHAN string
        /// cannot blow the page budget however long it is.
        /// </summary>
        private void AddVahanAddressRow(ColumnDescriptor col, int idx, string label, string? value)
        {
            const int MaxChars = 110;
            var text = (value ?? "").Trim();
            if (text.Length > MaxChars) text = text[..(MaxChars - 1)].TrimEnd(',', ' ') + "\u2026";

            string bg = idx % 2 == 0 ? "#FFFFFF" : "#F8FAFC";
            col.Item().Background(bg).BorderBottom(1).BorderColor("#F1F5F9")
                .PaddingVertical(4).PaddingHorizontal(10).Row(row =>
            {
                row.RelativeItem(2).AlignMiddle().Text(label.ToUpper()).FontSize(8).FontColor(LabelSlate);
                row.RelativeItem(8).AlignMiddle().AlignLeft()
                    .Text(text.Length == 0 ? "---" : text.ToUpper()).FontSize(8).Bold().FontColor(ValueDark);
            });
        }

        /// <summary>
        /// RC status as the report should print it.
        ///
        /// Prefers the text VAHAN returned; falls back to the bool for the whole back
        /// catalogue, where RcStatusText is null because it was never mapped. Never
        /// renders the bool directly — bool.ToString() is "True".
        /// </summary>
        private static string? ResolveRcStatus(VehicleDetailsDto? vd)
        {
            var text = vd?.RcStatusText?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
            return vd?.RcStatus switch { true => "ACTIVE", false => "INACTIVE", _ => null };
        }

        /// <summary>
        /// Road tax validity: the parsed date where there is one, otherwise the raw token.
        ///
        /// TaxPaidUpto is a string because VAHAN answers "LTT" for a life-time-tax
        /// vehicle, which is not a date and must not be forced into one.
        /// </summary>
        /// <summary>Pollution certificate expiry, or null so the row prints "---".</summary>
        private static string? FormatPucUpto(VehicleDetailsDto? vd) =>
            vd?.PollutionCertificateUpto is DateTime d
                ? d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                : null;

        private static string? FormatTaxUpto(VehicleDetailsDto? vd)
        {
            if (vd?.TaxUpto is DateTime d) return d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

            var raw = vd?.TaxPaidUpto?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return null;   // AddVahanRow prints "---"
            return raw.Equals("LTT", StringComparison.OrdinalIgnoreCase) ? "LIFE TIME TAX" : raw;
        }

        private void AddVahanRow(ColumnDescriptor col, int idx,
            string l1, string? v1, string l2, string? v2)
        {
            string bg = idx % 2 == 0 ? "#FFFFFF" : "#F8FAFC";
            // Back to 6pt. This was trimmed to 4 to pay for the two address rows, on an
            // estimate of about 9pt of slack; measured, the page had 99pt, and pairing the
            // document cards has since freed roughly another 100. The rows can breathe.
            col.Item().Background(bg).BorderBottom(1).BorderColor("#F1F5F9")
                .PaddingVertical(6).PaddingHorizontal(10).Row(row =>
            {
                row.RelativeItem(2).AlignMiddle().Text(l1.ToUpper()).FontSize(8).FontColor(LabelSlate);
                row.RelativeItem(3).AlignMiddle().AlignRight()
                    .Text(v1?.ToUpper() ?? "---").FontSize(9).Bold()
                    .FontColor(idx == 0 ? "#3B82F6" : ValueDark);
                row.ConstantItem(16);
                row.RelativeItem(2).AlignMiddle().Text(l2.ToUpper()).FontSize(8).FontColor(LabelSlate);
                row.RelativeItem(3).AlignMiddle().AlignRight()
                    .Text(v2?.ToUpper() ?? "---").FontSize(9).Bold().FontColor(ValueDark);
            });
        }

    }
}