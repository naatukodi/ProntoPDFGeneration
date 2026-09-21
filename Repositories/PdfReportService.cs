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
    public partial class PdfReportService
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

        // Who signs the report off. Fixed, not resolved from the case: the approver is
        // the person who certifies the valuation, not whoever inspected the vehicle or
        // last touched the document. Reading it off the case put the AVO's name over
        // the licence number of someone else.
        private const string ApproverName        = "Mahesh Garikina";
        private const string ApproverDesignation = "Head - Operations";

        /// <summary>Valuer licence of the approver named on every report's sign-off,
        /// stated with its issuing body as the approved template prints it.</summary>
        private const string ApproverLicenseNo = "IRDA/IND/SLA - 74183";

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
                    // Zero margin, gutters applied per band: the template's top accent
                    // bar bleeds to the paper edge, which a page margin would inset.
                    page.Margin(0);
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
                    page.DefaultTextStyle(x => x.FontFamily(ReportFont).FontSize(9.5f).FontColor(Ink)
                        .DisableFontFeature(FontFeatures.StandardLigatures));

                    // Left as SVG deliberately. Its typeface is host-dependent like the gauge's
                    // was, but this is a 1.5%-opacity wash where the face is imperceptible --
                    // and QuestPDF's Rotate pivots on the corner rather than the centre, so
                    // drawing it as text moved the watermark 130pt up the page. A visible
                    // misplacement is a worse trade than an invisible font difference.
                    page.Background().Svg(size => GenerateWatermarkSvg(size));
                    page.Header().Element(c => ComposeHeader(c, doc, referenceNumber));

                    page.Content().PaddingHorizontal(PageGutter).Column(main =>
                    {
                        ComposeCoverPage(main, doc, photoStreams, qrCodeBytes, referenceNumber);
                        main.Item().PageBreak();

                        ComposeVahanDetailsPage(main, doc, photoStreams);
                        main.Item().PageBreak();

                        ComposeSystemScoresPage(main, doc);
                        main.Item().PageBreak();

                        ComposePhotoGalleryAndDisclaimer(main, doc, photoStreams);
                    });

                    page.Footer().Element(c => ComposeFooter(c, doc, referenceNumber));
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

        /// <summary>
        /// "VEHGA VERIFIED" across the page, at the template's placement: centred on a
        /// line 118mm down, rotated 32 degrees, tracked 4mm apart, in a 4.5% teal wash.
        ///
        /// Still SVG rather than QuestPDF text. QuestPDF's Rotate pivots on the corner
        /// rather than the centre, which threw the old watermark 130pt up the page; the
        /// typeface is host-dependent here but at this opacity that is imperceptible.
        /// </summary>
        private string GenerateWatermarkSvg(QuestPDF.Infrastructure.Size size)
        {
            float cx = size.Width / 2f;
            float cy = Mm(118);
            string cxS = cx.ToString("F1", CultureInfo.InvariantCulture);
            string cyS = cy.ToString("F1", CultureInfo.InvariantCulture);
            return $"""
                <svg xmlns="http://www.w3.org/2000/svg" width="{size.Width}" height="{size.Height}">
                  <text x="{cxS}" y="{cyS}"
                        transform="rotate(-32, {cxS}, {cyS})"
                        text-anchor="middle" dominant-baseline="middle"
                        font-family="Lato, Calibri, Helvetica, Arial, sans-serif"
                        font-size="74" font-weight="bold"
                        letter-spacing="{Mm(4).ToString("F1", CultureInfo.InvariantCulture)}"
                        fill="{Teal}" fill-opacity="0.045">{Theme.Watermark}</text>
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
            // Case-insensitive on purpose. The named report slots look photos up by keys
            // like "SelfieWithVehicle"; a case stored differently would miss its slot,
            // fall through to the unnamed-photo path and print the raw key as its caption.
            // Two keys differing only in case would collide here, but the assignment below
            // is an indexer rather than Add, so the later one simply wins.
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
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
        /// MISSING / YES / NO / NA. Mirrors CONDITION_OPTIONS in the portal's
        /// inspection-field-registry.ts and conditionOptions in the app's
        /// inspection_field_registry.dart — keep the three in sync.
        ///
        /// YES prints as YES. It used to be folded into GOOD, which read wrongly once
        /// the checklist asked yes/no questions (ENGINE STARTED: GOOD). It scores the
        /// same as GOOD. true/false are the pre-2026-09 Engine Started / Vehicle Moved
        /// answers, still raw in Cosmos for cases nobody has re-saved.
        /// </summary>
        private string MapVerdict(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "NA";
            var lower = input.ToLower().Trim();
            if (lower is "yes" or "true")               return "YES";
            if (lower is "1" or "good" or "ok")         return "GOOD";
            if (lower is "0" or "bad" or "poor")        return "POOR";
            if (lower is "no" or "false")               return "NO";
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
            return scores.Any() ? Round1(scores.Average()) : (double?)null;
        }

        /// <summary>
        /// To one decimal place with the portal's arithmetic — round1 in inspection-score.ts,
        /// Math.round(n * 10) / 10 — so the AVO page and the report agree to the digit.
        /// Math.Round(x, 1) rounds exact halves to even: a TIRES card of AVERAGE with one
        /// missing tyre is 3.25, which printed 3.2 here against 3.3 on screen.
        /// </summary>
        private static double Round1(double value) =>
            Math.Round(value * 10, MidpointRounding.AwayFromZero) / 10;

        /// <summary>The fields of a section that count toward its score, each as the verdict it scores as.</summary>
        private Dictionary<string, string?> ScorableItems(SectionDef sec, InspectionDetails ins) =>
            sec.Fields.Where(f => f.Scored)
                      .ToDictionary(f => f.Label, f => ScoresAs(f, GetInsValue(ins, f.Key)));

        /// <summary>
        /// An answer rewritten as the verdict it scores as, for the two rules where the
        /// plain reading is backwards. Fluid Leaks: NO is the good answer, so NO scores as
        /// GOOD and YES as NO. Missing Tyres: 0 scores as GOOD and any count above it as NO;
        /// a blank or unreadable count is left out. Everything else scores as written.
        /// </summary>
        private static string? ScoresAs(FieldDef field, string? value)
        {
            var raw = value?.Trim() ?? "";
            switch (field.Scoring)
            {
                case AnswerScoring.NoIsGood:
                    var lower = raw.ToLowerInvariant();
                    if (lower is "no" or "false")  return "GOOD";
                    if (lower is "yes" or "true")  return "NO";
                    return value;
                case AnswerScoring.ZeroIsGood:
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
                        return null;
                    return n == 0 ? "GOOD" : "NO";
                default:
                    return value;
            }
        }

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
                    return Round1(sectionScores.Average());
            }
            return ParseScoreValue(doc.QualityControl?.OverallRating);
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

        // Indian number format: ₹ 14,00,000 instead of ₹ 1,400,000
        private static string FormatIndianCurrency(decimal amount) =>
            amount.ToString("N0", new CultureInfo("en-IN"));

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

        /// <summary>
        /// The top accent bar, full bleed, split teal-to-orange at 62% of the width.
        /// Drawn as two rects rather than a gradient: the template's stops sit hard
        /// against each other, and a gradient would soften the join.
        /// </summary>
        private string TopBarSvg(float w, float h)
        {
            float split = w * 0.62f;
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\">" +
                   $"<rect x=\"0\" y=\"0\" width=\"{F(split)}\" height=\"{F(h)}\" fill=\"{Theme.Primary}\"/>" +
                   $"<rect x=\"{F(split)}\" y=\"0\" width=\"{F(w - split)}\" height=\"{F(h)}\" fill=\"{Orange}\"/></svg>";
        }

        private void ComposeHeader(IContainer container, ValuationDocument doc, string referenceNumber)
        {
            container.Column(col =>
            {
                // Full bleed: this band sits outside the 12mm gutter the rest of the page keeps.
                col.Item().Height(Mm(2.4)).Svg(s => TopBarSvg(s.Width, s.Height));

                col.Item().PaddingTop(Mm(3.2)).PaddingHorizontal(PageGutter)
                   .PaddingBottom(Mm(2)).BorderBottom(1).BorderColor(Line).Row(row =>
                {
                    row.RelativeItem().AlignMiddle().Height(Mm(10)).AlignLeft().Row(logoRow =>
                    {
                        var trimmedPath  = Path.Combine(AppContext.BaseDirectory, "png", Theme.LogoTrimmed);
                        var absolutePath = Path.Combine(AppContext.BaseDirectory, "png", Theme.LogoFull);
                        // Resolved against the output directory only. Working-directory
                        // fallbacks used to hide a missing asset on a dev machine while
                        // the deployed service rendered text instead of the logo.
                        string? logoPath = File.Exists(trimmedPath)  ? trimmedPath
                                         : File.Exists(absolutePath) ? absolutePath
                                         : null;
                        if (logoPath != null)
                            logoRow.AutoItem().Height(Mm(10)).Image(logoPath).FitHeight();
                        else
                            logoRow.AutoItem().AlignMiddle().Text(Theme.Name)
                                .FontFamily(ReportFont).FontSize(24).Bold().FontColor(BrandTeal);
                    });

                    row.AutoItem().AlignMiddle().Column(meta =>
                    {
                        void Line2(string label, string value) =>
                            meta.Item().AlignRight().Text(t =>
                            {
                                t.Span(label).FontFamily(ReportFont).FontSize(8).Bold()
                                    .FontColor(Label).LetterSpacing(Ls(0.2, 8));
                                t.Span(value).FontFamily(ReportFont).FontSize(8).Bold()
                                    .FontColor(Navy).LetterSpacing(Ls(0.2, 8));
                            });

                        Line2("REF NO: ", referenceNumber);
                        Line2("REPORT DATE: ",
                              doc.CreatedAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant());
                        meta.Item().AlignRight().Text(t =>
                        {
                            t.Span("PAGE ").FontFamily(ReportFont).FontSize(8).Bold()
                                .FontColor(Label).LetterSpacing(Ls(0.2, 8));
                            t.CurrentPageNumber().FontFamily(ReportFont).FontSize(8).Bold().FontColor(Navy);
                            t.Span(" OF ").FontFamily(ReportFont).FontSize(8).Bold()
                                .FontColor(Label).LetterSpacing(Ls(0.2, 8));
                            t.TotalPages().FontFamily(ReportFont).FontSize(8).Bold().FontColor(Navy);
                        });
                    });
                });

                col.Item().Height(Mm(3.4));
            });
        }

        // ──────────────────────────────────────────────
        // Footer
        // ──────────────────────────────────────────────

        private void ComposeFooter(IContainer container, ValuationDocument doc, string referenceNumber)
        {
            container.PaddingHorizontal(PageGutter).PaddingBottom(Mm(6))
                     .BorderTop(1).BorderColor(Line).PaddingTop(Mm(2.2)).Row(row =>
            {
                row.RelativeItem().Text(t =>
                {
                    // Tracking pulled in from the template's 0.15mm. That value assumes
                    // Carlito; in Lato this line is wide enough that the full 0.15mm
                    // wraps "VEHGA SECURE CLOUD" onto a second line, which then sits
                    // under the rule and reads as a stray sentence.
                    t.DefaultTextStyle(x => x.FontFamily(ReportFont).FontSize(6f)
                        .FontColor(LabelFaint).LetterSpacing(Ls(0.04, 6)));
                    t.Span("NOTE: THIS IS A DIGITALLY GENERATED REPORT, HENCE NO PHYSICAL SIGNATURE IS REQUIRED. VERIFIED VIA ");
                    t.Span(Theme.Name == "VEHGA" ? "VEHGA SECURE CLOUD" : "PRONTO SECURE CLOUD")
                        .Bold().FontColor(Theme.Primary);
                    t.Span(".");
                });

                row.AutoItem().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontFamily(ReportFont).FontSize(6f)
                        .FontColor(LabelFaint).LetterSpacing(Ls(0.15, 6)));
                    t.Span($"{doc.VehicleDetails?.RegistrationNumber?.ToUpperInvariant() ?? doc.VehicleNumber ?? "-"}   |   {referenceNumber}");
                });
            });
        }

        // ──────────────────────────────────────────────
        // PAGE 1 — Cover Page
        // ──────────────────────────────────────────────

        // PAGE 1 — Cover now lives in PdfReportService.Cover.cs, rebuilt to the
        // approved template. Its design tokens are in PdfReportService.Design.cs.

        // ──────────────────────────────────────────────
        // PAGE 2 — Vahan Details
        // ──────────────────────────────────────────────

        // PAGE 2 — VAHAN details now lives in PdfReportService.Vahan.cs.

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

        // ──────────────────────────────────────────────
        // PAGE 3 — System Scores
        // ──────────────────────────────────────────────

        // ── PDF inspection-field registry (mirrors inspection-field-registry.ts) ──
        // Every field is scored. Where an item does not apply to the vehicle the
        // inspector answers N/A, which MapVerdict excludes from the average — that
        // is the lever for "not a defect", rather than exempting field names here.
        //
        // Matches the 2026-09 checklist (VEHGA_REPORT_ALL_SEGMENTS_UPDATED) section for
        // section, in the portal's order: the sheet's MECHANICAL column, then STRUCTURAL,
        // then FUNCTIONALITY and OTHER SYSTEMS. Page 3 lays the cards out by that order.
        /// <summary>
        /// One row on a system card. <paramref name="Scored"/> false means the value is
        /// printed but excluded from the card's score — mirrors `scored` on
        /// InspectionField in the portal's inspection-field-registry.ts, which must be
        /// changed in the same commit or screen and report will disagree.
        /// <paramref name="Kind"/> and <paramref name="Scoring"/> mirror `type: 'number'`
        /// and `scoring` there.
        /// </summary>
        private record FieldDef(string Label, string Key, bool Scored = true,
            AnswerKind Kind = AnswerKind.Condition, AnswerScoring Scoring = AnswerScoring.Condition);
        private record SectionDef(string Name, FieldDef[] Fields);

        /// <summary>A dropdown answer (GOOD … YES / NO), or a typed count such as Number of Tyres.</summary>
        private enum AnswerKind { Condition, Count }

        /// <summary>
        /// How an answer scores — the portal's answerPoints in inspection-score.ts.
        /// Condition: as MapVerdict reads it (GOOD 8.5 … NO 1.0).
        /// NoIsGood: the question asks about a fault (Fluid Leaks), so NO scores 8.5 and YES 1.0.
        /// ZeroIsGood: a count of faults (Missing Tyres): 0 scores 8.5, 1 or more 1.0.
        /// </summary>
        private enum AnswerScoring { Condition, NoIsGood, ZeroIsGood }

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
                new("ENGINE CONDITION", new FieldDef[] {
                    new("ENGINE CONDITION", "engineCondition"),
                    new("FLUID LEAKS", "fluidLeaks", Scoring: AnswerScoring.NoIsGood),
                    new("RADIATOR", "radiator"),
                    new("ALL HOSE PIPES", "allHosePipes"),
                    new("FUEL SYSTEM", "fuelSystem"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY", "gearBoxAssy"),
                    new("CLUTCH SYSTEM", "clutchSystem"),
                    new("DIFFERENTIAL ASSY", "differentialAssy"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES", "frontBrakes"),
                    new("REAR BRAKES", "rearBrakes"),
                    new("PARKING BRAKE", "parkingBrake"),
                    new("ABS", "abs", Scored: false),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL", "steeringWheel"),
                    new("STEERING COLUMN", "steeringColumn"),
                    new("STEERING BOX", "steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION", "frontSuspension"),
                    new("REAR SUSPENSION", "rearSuspension"),
                    new("FRONT & REAR AXLES", "axles"),
                }),
                new("CABIN ASSEMBLY", new FieldDef[] {
                    new("CABIN", "cabin"),
                    new("DASHBOARD", "dashboard"),
                    new("DOORS", "doors"),
                    new("ALL GLASSES", "allGlasses"),
                    new("SEATS", "seats"),
                }),
                new("LOAD BODY", new FieldDef[] {
                    new("BODY CONDITION", "bodyCondition"),
                    new("RIGHT SIDE GATE", "rightSideGate"),
                    new("LEFT SIDE GATE", "leftSideGate"),
                    new("TAIL GATE", "tailGate"),
                    new("LOAD FLOOR", "loadFloor"),
                    new("CHASSIS / VEHICLE FRAME", "chassisCondition"),
                    new("PAINT WORK", "paintWork"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS", "headLights"),
                    new("TAIL LIGHTS / INDICATORS", "tailLightsIndicators"),
                    new("BATTERY", "batteryCondition"),
                    new("WIRING ASSY", "wiringAssy"),
                    new("CLUSTER UNIT", "clusterUnit"),
                }),
                new("TIRES", new FieldDef[] {
                    new("TYRE CONDITION", "tyreCondition"),
                    new("NUMBER OF TYRES", "numberOfTyres", Scored: false, Kind: AnswerKind.Count),
                    new("MISSING TYRES", "missingTyres", Kind: AnswerKind.Count, Scoring: AnswerScoring.ZeroIsGood),
                }),
                new("FUNCTIONALITY", new FieldDef[] {
                    new("ENGINE STARTED", "engineStarted"),
                    new("TEST DRIVE", "testDrive"),
                    new("VEHICLE MOVED", "vehicleMoved"),
                    new("WARNING LIGHTS", "warningLights"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AUDIO", "audio"),
                    new("UPHOLSTERY", "upholstery"),
                    new("HYDRAULIC LIFT", "hydraulicLift"),
                    new("FRONT CRASH GUARD", "frontCrashGuard"),
                    new("REAR CRASH GUARD", "rearCrashGuard"),
                    new("SIDE UNDER RUN PROTECTION", "sideUnderRunProtection"),
                }),
            },
            ["4w"] = new SectionDef[]
            {
                new("ENGINE CONDITION", new FieldDef[] {
                    new("ENGINE CONDITION", "engineCondition"),
                    new("FLUID LEAKS", "fluidLeaks", Scoring: AnswerScoring.NoIsGood),
                    new("RADIATOR", "radiator"),
                    new("ALL HOSE PIPES", "allHosePipes"),
                    new("FUEL SYSTEM", "fuelSystem"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY", "gearBoxAssy"),
                    new("CLUTCH SYSTEM", "clutchSystem"),
                    new("DRIVE SHAFTS", "driveShafts"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES", "frontBrakes"),
                    new("REAR BRAKES", "rearBrakes"),
                    new("PARKING BRAKE", "parkingBrake"),
                    new("ABS", "abs", Scored: false),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL", "steeringWheel"),
                    new("STEERING COLUMN", "steeringColumn"),
                    new("STEERING BOX", "steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION", "frontSuspension"),
                    new("REAR SUSPENSION", "rearSuspension"),
                    new("FRONT & REAR AXLES", "axles"),
                }),
                new("EXTERIOR", new FieldDef[] {
                    new("BONNET ASSY", "bonnet"),
                    new("BUMPERS", "bumpers"),
                    new("DOORS", "doors"),
                    new("ALL GLASSES", "allGlasses"),
                    new("SIDE FENDERS", "sideFenders"),
                    // Not on the 4W sheet; kept on request (2026-09-17), as in the portal.
                    new("PAINT WORK", "paintWork"),
                }),
                new("INTERIOR", new FieldDef[] {
                    new("DASH BOARD", "dashboard"),
                    new("SEATS & MATS", "seats"),
                    new("INTERIOR TRIMS", "interiorTrims"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS", "headLights"),
                    new("TAIL LIGHTS / INDICATORS", "tailLightsIndicators"),
                    new("BATTERY", "batteryCondition"),
                    new("WIRING ASSY", "wiringAssy"),
                    new("CLUSTER UNIT", "clusterUnit"),
                }),
                new("TIRES", new FieldDef[] {
                    new("TYRE CONDITION", "tyreCondition"),
                    new("NUMBER OF TYRES", "numberOfTyres", Scored: false, Kind: AnswerKind.Count),
                    new("MISSING TYRES", "missingTyres", Kind: AnswerKind.Count, Scoring: AnswerScoring.ZeroIsGood),
                }),
                new("FUNCTIONALITY", new FieldDef[] {
                    new("ENGINE STARTED", "engineStarted"),
                    new("TEST DRIVE", "testDrive"),
                    new("VEHICLE MOVED", "vehicleMoved"),
                    new("WARNING LIGHTS", "warningLights"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AUDIO", "audio"),
                    new("AIR CONDITIONER", "airConditioner"),
                    new("UPHOLSTERY", "upholstery"),
                    new("SUN ROOF", "sunRoof"),
                    new("REAR CRASH GUARD", "rearCrashGuard"),
                    new("FRONT CRASH GUARD", "frontCrashGuard"),
                }),
            },
            ["2w"] = new SectionDef[]
            {
                new("ENGINE CONDITION", new FieldDef[] {
                    new("ENGINE CONDITION", "engineCondition"),
                    new("FLUID LEAKS", "fluidLeaks", Scoring: AnswerScoring.NoIsGood),
                    new("RADIATOR", "radiator"),
                    new("ALL HOSE PIPES", "allHosePipes"),
                    new("FUEL SYSTEM", "fuelSystem"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY", "gearBoxAssy"),
                    new("CLUTCH SYSTEM", "clutchSystem"),
                    new("FINAL DRIVE / CHAIN", "finalDrive"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKE", "frontBrakes"),
                    new("REAR BRAKE", "rearBrakes"),
                    new("BRAKE LEVERS / FLUID", "brakeLeversFluid"),
                    new("ABS", "abs", Scored: false),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("HANDLE BAR", "handleBar"),
                    new("STEERING STEM", "steeringStem"),
                    new("FRONT FORK", "frontForkAssy"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SHOCK ABSORBER", "frontShockAbsorber"),
                    new("REAR SHOCK ABSORBER", "rearShockAbsorber"),
                    new("ALLOY / WHEEL RIM", "alloyWheelRim"),
                }),
                new("EXTERIOR", new FieldDef[] {
                    new("FUEL TANK ASSY", "fuelTankCondition"),
                    new("FRONT SCOOP", "frontScoop"),
                    new("SEAT", "seatCondition"),
                    new("R/V MIRRORS", "rvMirrors"),
                    new("LOCK SET", "lockSet"),
                }),
                new("BODY", new FieldDef[] {
                    new("MUDGUARD - FRONT", "frontMudGuard"),
                    new("MUDGUARD - REAR", "rearMudGuard"),
                    new("SIDE COVERS (LH, RH)", "sideCovers"),
                    new("BELLY / FLOOR PANELS", "bellyPanels"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS", "headLights"),
                    new("TAIL LIGHTS / INDICATORS", "tailLightsIndicators"),
                    new("BATTERY", "batteryCondition"),
                    new("WIRING ASSY", "wiringAssy"),
                    new("SWITCHES", "switches"),
                }),
                new("TIRES", new FieldDef[] {
                    new("TYRE CONDITION", "tyreCondition"),
                    new("NUMBER OF TYRES", "numberOfTyres", Scored: false, Kind: AnswerKind.Count),
                    new("MISSING TYRES", "missingTyres", Kind: AnswerKind.Count, Scoring: AnswerScoring.ZeroIsGood),
                }),
                new("FUNCTIONALITY", new FieldDef[] {
                    new("ENGINE STARTED", "engineStarted"),
                    new("TEST RIDE", "testDrive"),
                    new("VEHICLE MOVED", "vehicleMoved"),
                    new("WARNING LIGHTS", "warningLights"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("MAIN STAND", "mainStand"),
                    new("SIDE STAND", "sideStand"),
                    new("HORN", "horn"),
                    new("KICK PEDAL / FOOT REST", "kickPedalFootRest"),
                    new("CHAIN GUARD", "chainGuard"),
                    new("SELF START", "selfStart"),
                }),
            },
            ["3w"] = new SectionDef[]
            {
                new("ENGINE CONDITION", new FieldDef[] {
                    new("ENGINE CONDITION", "engineCondition"),
                    new("FLUID LEAKS", "fluidLeaks", Scoring: AnswerScoring.NoIsGood),
                    new("RADIATOR", "radiator"),
                    new("ALL HOSE PIPES", "allHosePipes"),
                    new("FUEL SYSTEM", "fuelSystem"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY", "gearBoxAssy"),
                    new("CLUTCH SYSTEM", "clutchSystem"),
                    new("DIFFERENTIAL ASSY", "differentialAssy"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES", "frontBrakes"),
                    new("REAR BRAKES", "rearBrakes"),
                    new("PARKING BRAKE", "parkingBrake"),
                    new("ABS", "abs", Scored: false),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING HANDLE", "steeringHandle"),
                    new("STEERING COLUMN", "steeringColumn"),
                    new("STEERING LINKAGES", "steeringLinkages"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION", "frontSuspension"),
                    new("REAR SUSPENSION", "rearSuspension"),
                    new("FRONT & REAR AXLES", "axles"),
                }),
                new("CABIN ASSEMBLY", new FieldDef[] {
                    new("FRONT PANEL", "frontPanel"),
                    new("FR GLASS FRAME", "frontGlassFrame"),
                    new("DASH BOARD", "dashboard"),
                    new("SEATS & MATS", "seats"),
                    new("MUDGUARDS", "mudguards"),
                }),
                new("LOAD BODY", new FieldDef[] {
                    new("RIGHT SIDE GATE", "rightSideGate"),
                    new("LEFT SIDE GATE", "leftSideGate"),
                    new("TAIL GATE", "tailGate"),
                    new("LOAD FLOOR", "loadFloor"),
                    new("CHASSIS / VEHICLE FRAME", "chassisCondition"),
                    new("PAINT WORK", "paintWork"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("LIGHTS", "headLights"),
                    new("TAIL LIGHTS / INDICATORS", "tailLightsIndicators"),
                    new("BATTERY", "batteryCondition"),
                    new("WIRING ASSY", "wiringAssy"),
                    new("SWITCHES", "switches"),
                }),
                new("TIRES", new FieldDef[] {
                    new("TYRE CONDITION", "tyreCondition"),
                    new("NUMBER OF TYRES", "numberOfTyres", Scored: false, Kind: AnswerKind.Count),
                    new("MISSING TYRES", "missingTyres", Kind: AnswerKind.Count, Scoring: AnswerScoring.ZeroIsGood),
                }),
                new("FUNCTIONALITY", new FieldDef[] {
                    new("ENGINE STARTED", "engineStarted"),
                    new("TEST DRIVE", "testDrive"),
                    new("VEHICLE MOVED", "vehicleMoved"),
                    new("WARNING LIGHTS", "warningLights"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AUDIO", "audio"),
                    new("UPHOLSTERY", "upholstery"),
                    new("LOAD CARRIER", "loadCarrier"),
                    new("FRONT CRASH GUARD", "frontCrashGuard"),
                    new("REAR CRASH GUARD", "rearCrashGuard"),
                    new("SIDE MIRRORS", "sideMirrors"),
                }),
            },
            ["ce"] = new SectionDef[]
            {
                new("ENGINE CONDITION", new FieldDef[] {
                    new("ENGINE CONDITION", "engineCondition"),
                    new("FLUID LEAKS", "fluidLeaks", Scoring: AnswerScoring.NoIsGood),
                    new("RADIATOR", "radiator"),
                    new("HYDRAULIC OIL COOLER", "hydraulicOilCooler"),
                    new("FUEL SYSTEM", "fuelSystem"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY", "gearBoxAssy"),
                    new("TORQUE CONVERTER", "torqueConverter"),
                    new("FINAL DRIVE", "finalDrive"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("SERVICE BRAKE", "serviceBrake"),
                    new("RETARDER", "retarder"),
                    new("PARKING BRAKE", "parkingBrake"),
                    new("EMERGENCY STOP", "emergencyStop"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING / CONTROL LEVERS", "steeringControlLevers"),
                    new("HYDRAULIC STEERING PUMP", "hydraulicSteeringPump"),
                    new("SWIVEL JOINTS", "swivelJoints"),
                }),
                new("HYDRAULIC SYSTEM", new FieldDef[] {
                    new("HYDRAULIC PUMP", "hydraulicPump"),
                    new("CYLINDERS", "hydraulicCylinders"),
                    new("HOSES & FITTINGS", "hosesAndFittings"),
                }),
                new("CABIN ASSEMBLY", new FieldDef[] {
                    new("CABIN STRUCTURE", "cabinStructure"),
                    new("DASH BOARD & CONTROLS", "dashboardControls"),
                    new("DOORS", "doors"),
                    new("GLASS PANELS", "glassPanels"),
                    new("SEAT", "seats"),
                }),
                new("ATTACHMENTS", new FieldDef[] {
                    new("BOOM / ARM", "boomArm"),
                    new("BUCKET / BLADE", "bucketBlade"),
                    new("COUNTER WEIGHT", "counterWeight"),
                    new("PAINT WORK", "paintWork"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("LIGHTS", "headLights"),
                    new("WARNING / INDICATOR LIGHTS", "warningIndicatorLights"),
                    new("BATTERY", "batteryCondition"),
                    new("WIRING ASSY", "wiringAssy"),
                    new("SENSORS", "sensors"),
                }),
                new("TIRE / TRACK", new FieldDef[] {
                    new("TYRE / TRACK CONDITION", "tyreCondition"),
                    new("NUMBER OF TYRES / TRACKS", "numberOfTyres", Scored: false, Kind: AnswerKind.Count),
                    new("MISSING / DAMAGED", "missingTyres", Kind: AnswerKind.Count, Scoring: AnswerScoring.ZeroIsGood),
                }),
                new("FUNCTIONALITY", new FieldDef[] {
                    new("ENGINE STARTED", "engineStarted"),
                    new("FUNCTIONAL TEST", "testDrive"),
                    new("MACHINE MOVED", "vehicleMoved"),
                    new("WARNING LIGHTS", "warningLights"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("SWING MECHANISM", "swingMechanism"),
                    new("TRACK CHAINS", "trackChains"),
                    new("SPROCKETS", "sprockets"),
                    new("ROLLERS", "rollers"),
                    new("HOUR METER", "hourMeter"),
                    new("ROCK BREAKER", "rockBreaker"),
                }),
            },
            ["bus"] = new SectionDef[]
            {
                new("ENGINE CONDITION", new FieldDef[] {
                    new("ENGINE CONDITION", "engineCondition"),
                    new("FLUID LEAKS", "fluidLeaks", Scoring: AnswerScoring.NoIsGood),
                    new("RADIATOR", "radiator"),
                    new("ALL HOSE PIPES", "allHosePipes"),
                    new("FUEL SYSTEM", "fuelSystem"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY", "gearBoxAssy"),
                    new("CLUTCH SYSTEM", "clutchSystem"),
                    new("DIFFERENTIAL ASSY", "differentialAssy"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("FRONT BRAKES", "frontBrakes"),
                    new("REAR BRAKES", "rearBrakes"),
                    new("PARKING BRAKE", "parkingBrake"),
                    new("ABS", "abs", Scored: false),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL", "steeringWheel"),
                    new("STEERING COLUMN", "steeringColumn"),
                    new("STEERING BOX", "steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT SUSPENSION", "frontSuspension"),
                    new("REAR SUSPENSION", "rearSuspension"),
                    new("FRONT & REAR AXLES", "axles"),
                }),
                new("COACH ASSEMBLY", new FieldDef[] {
                    new("DRIVER CABIN", "driverCabin"),
                    new("DASHBOARD", "dashboard"),
                    new("DOORS", "doors"),
                    new("ALL GLASSES", "allGlasses"),
                    new("BUMPERS & GRILLES", "bumpersAndGrilles"),
                }),
                new("BODY ASSEMBLY", new FieldDef[] {
                    new("SEATS & BERTHS", "seatsAndBerths"),
                    new("INTERIOR TRIMS", "interiorTrims"),
                    new("SIDE BODY PANELS", "sideBodyPanels"),
                    new("REAR BODY PANELS", "rearBodyPanels"),
                    new("CHASSIS / BODY FRAME", "chassisCondition"),
                    new("PAINT WORK", "paintWork"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS", "headLights"),
                    new("TAIL LIGHTS / INDICATORS", "tailLightsIndicators"),
                    new("BATTERY", "batteryCondition"),
                    new("WIRING ASSY", "wiringAssy"),
                    new("CLUSTER UNIT", "clusterUnit"),
                }),
                new("TIRES", new FieldDef[] {
                    new("TYRE CONDITION", "tyreCondition"),
                    new("NUMBER OF TYRES", "numberOfTyres", Scored: false, Kind: AnswerKind.Count),
                    new("MISSING TYRES", "missingTyres", Kind: AnswerKind.Count, Scoring: AnswerScoring.ZeroIsGood),
                }),
                new("FUNCTIONALITY", new FieldDef[] {
                    new("ENGINE STARTED", "engineStarted"),
                    new("TEST DRIVE", "testDrive"),
                    new("VEHICLE MOVED", "vehicleMoved"),
                    new("WARNING LIGHTS", "warningLights"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("AIR CONDITIONER", "airConditioner"),
                    new("AUDIO", "audio"),
                    new("UPHOLSTERY", "upholstery"),
                    new("LOAD CARRIER", "loadCarrier"),
                    new("FRONT CRASH GUARD", "frontCrashGuard"),
                    new("REAR CRASH GUARD", "rearCrashGuard"),
                }),
            },
            ["fe"] = new SectionDef[]
            {
                new("ENGINE CONDITION", new FieldDef[] {
                    new("ENGINE CONDITION", "engineCondition"),
                    new("FLUID LEAKS", "fluidLeaks", Scoring: AnswerScoring.NoIsGood),
                    new("RADIATOR", "radiator"),
                    new("ALL HOSE PIPES", "allHosePipes"),
                    new("FUEL SYSTEM", "fuelSystem"),
                }),
                new("TRANSMISSION SYSTEM", new FieldDef[] {
                    new("GEARBOX ASSY", "gearBoxAssy"),
                    new("CLUTCH SYSTEM", "clutchSystem"),
                    new("DIFFERENTIAL ASSY", "differentialAssy"),
                }),
                new("BRAKES", new FieldDef[] {
                    new("RIGHT INDIVIDUAL BRAKES", "rightIndividualBrakes"),
                    new("LEFT INDIVIDUAL BRAKES", "leftIndividualBrakes"),
                    new("PARKING BRAKE", "parkingBrake"),
                    new("BRAKE EQUALIZATION", "brakeEqualization"),
                }),
                new("STEERING SYSTEM", new FieldDef[] {
                    new("STEERING WHEEL", "steeringWheel"),
                    new("STEERING COLUMN", "steeringColumn"),
                    new("STEERING BOX", "steeringBox"),
                }),
                new("SUSPENSION SYSTEM", new FieldDef[] {
                    new("FRONT AXLE", "frontAxleFe"),
                    new("REAR AXLE", "rearAxleFe"),
                    new("TIE RODS & JOINTS", "tieRodsJoints"),
                }),
                new("CABIN ASSEMBLY", new FieldDef[] {
                    new("OPERATOR STATION", "operatorStation"),
                    new("DASH BOARD", "dashboard"),
                    new("CANOPY", "canopy"),
                    new("LOCK SET", "lockSet"),
                    new("SEAT", "seats"),
                }),
                new("BODY ASSEMBLY", new FieldDef[] {
                    new("BONNET", "bonnet"),
                    new("FRONT GRILLES", "frontGrilles"),
                    new("SIDE FENDERS", "sideFenders"),
                    new("FUEL TANK", "fuelTankFe"),
                    new("OPERATOR PLATFORM", "operatorPlatform"),
                    new("PAINT WORK", "paintWork"),
                }),
                new("ELECTRICAL SYSTEM", new FieldDef[] {
                    new("HEAD LIGHTS", "headLights"),
                    new("TAIL LIGHTS / INDICATORS", "tailLightsIndicators"),
                    new("BATTERY", "batteryCondition"),
                    new("WIRING ASSY", "wiringAssy"),
                    new("SWITCHES", "switches"),
                }),
                new("TIRES", new FieldDef[] {
                    new("TYRE CONDITION", "tyreCondition"),
                    new("NUMBER OF TYRES", "numberOfTyres", Scored: false, Kind: AnswerKind.Count),
                    new("MISSING TYRES", "missingTyres", Kind: AnswerKind.Count, Scoring: AnswerScoring.ZeroIsGood),
                }),
                new("FUNCTIONALITY", new FieldDef[] {
                    new("ENGINE STARTED", "engineStarted"),
                    new("FIELD FUNCTION TEST", "testDrive"),
                    new("VEHICLE MOVED", "vehicleMoved"),
                    new("WARNING LIGHTS", "warningLights"),
                }),
                new("OTHER SYSTEMS", new FieldDef[] {
                    new("MUFFLER", "muffler"),
                    new("AIR FILTER", "airFilter"),
                    new("ATTACHMENT HITCH", "attachmentHitch"),
                    new("HYDRAULIC LIFT ARM", "hydraulicLiftFe"),
                    new("DROP ARM", "dropArm"),
                    new("REAR DRAWBAR", "rearDrawbar"),
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

        // ──────────────────────────────────────────────
        // Photo slots
        // ──────────────────────────────────────────────

        // Shared by the PDF gallery pages (PdfReportService.Photos.cs) and the browser
        // gallery HTML below, so both use identical labels.
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
            // Matched case-insensitively for the same reason the PDF's photo map is —
            // and copied key by key rather than through the constructor, which throws
            // when two keys differ only in case.
            var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in doc.PhotoUrls ?? new Dictionary<string, string>())
                urls[kv.Key] = kv.Value;

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

        // PAGES 4+ — photographic evidence now lives in PdfReportService.Photos.cs.

        // ──────────────────────────────────────────────
        // Table Cell Helpers
        // ──────────────────────────────────────────────

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

    }
}