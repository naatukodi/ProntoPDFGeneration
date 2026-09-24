using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using SkiaSharp;
using Valuation.Api.Models;

namespace Valuation.Api.Services
{
    /// <summary>
    /// Page 2 — the VAHAN record, the regulatory documents, and the two chassis
    /// identification photos. Laid out to the approved template.
    /// </summary>
    public partial class PdfReportService
    {
        /// <summary>
        /// Centre-crops to an aspect ratio, for the chassis strips.
        ///
        /// The template's chassis images are wide bands roughly 8:1. A camera photo is
        /// 4:3, and the template's own rule (width 100%, height auto) would render that
        /// at 178mm wide by 133mm tall — taller than the space page 2 has left, which
        /// pushes the second card and the whole disclaimer onto another page. Cropping
        /// to the band the design expects keeps the chassis number legible and the page
        /// intact, where letterboxing would shrink the digits to nothing.
        /// </summary>
        private static byte[] CropToAspect(byte[] imageBytes, float aspect)
        {
            try
            {
                using var originalBitmap = SKBitmap.Decode(imageBytes);
                if (originalBitmap == null) return imageBytes;

                int cropW = originalBitmap.Width, cropH = (int)(originalBitmap.Width / aspect);
                if (cropH > originalBitmap.Height)
                {
                    cropH = originalBitmap.Height;
                    cropW = (int)(originalBitmap.Height * aspect);
                }
                
                int x = (originalBitmap.Width - cropW) / 2;
                int y = (originalBitmap.Height - cropH) / 2;
                var cropRect = new SKRectI(x, y, x + cropW, y + cropH);

                using var originalImage = SKImage.FromBitmap(originalBitmap);
                using var subsetImage = originalImage.Subset(cropRect);
                using var data = subsetImage.Encode(SKEncodedImageFormat.Jpeg, 92);
                
                return data.ToArray();
            }
            catch { return imageBytes; }
        }

        private void ComposeVahanDetailsPage(ColumnDescriptor main, ValuationDocument doc,
                                             Dictionary<string, byte[]> photos)
        {
            var vd = doc.VehicleDetails;

            DrawSectionHeading(main.Item().PaddingBottom(Mm(3)), "file-text", "VAHAN Details");

            // ---------- the record, two columns of label / value
            var rows = new (string Label, string? Value, string? Color, string? Pill)[]
            {
                ("REGISTRATION NUMBER", vd?.RegistrationNumber?.ToUpperInvariant(), Teal, null),
                ("OWNER SERIAL NUMBER", vd?.OwnerSerialNo,                          null, null),
                ("CHASSIS NUMBER",      vd?.ChassisNumber,                          null, null),
                ("YEAR OF MANUFACTURE", ResolveMfgYear(vd),                         null, null),
                ("ENGINE NUMBER",       vd?.EngineNumber,                           null, null),
                ("DATE OF REGISTRATION",vd?.DateOfRegistration?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture), null, null),
                ("VEHICLE MAKE",        vd?.Make?.ToUpperInvariant(),               null, null),
                ("ENGINE CUBIC CAPACITY", vd?.EngineCC is int cc && cc > 0 ? $"{cc} CC" : null, null, null),
                ("VEHICLE MODEL",       vd?.Model?.ToUpperInvariant(),              null, null),
                ("GROSS VEHICLE WEIGHT", vd?.GrossVehicleWeight is double g && g > 0 ? $"{g:0} KG" : null, null, null),
                ("VEHICLE CATEGORY",    vd?.CategoryCode?.ToUpperInvariant(),       null, null),
                ("SEATING CAPACITY",    vd?.SeatingCapacity is int sc && sc > 0 ? $"{sc} SEATS" : null, null, null),
                ("VEHICLE CLASS",       vd?.ClassOfVehicle?.ToUpperInvariant(),     null, null),
                ("FUEL TYPE",           vd?.Fuel?.ToUpperInvariant(),               null, null),
                ("BODY TYPE",           (vd?.BodyType ?? doc.InspectionDetails?.BodyType)?.ToUpperInvariant(), null, null),
                ("FUEL NORMS",          vd?.NormsType?.ToUpperInvariant(),          null, null),
                ("VEHICLE COLOR",       vd?.Colour?.ToUpperInvariant(),             null, null),
                ("RC STATUS",           null,                                       null, ResolveRcStatus(vd)?.ToUpperInvariant() ?? "---"),
                ("REGISTERED AT RTO",   vd?.Rto?.ToUpperInvariant(),                null, null),
                ("PUC VALID UPTO",      FormatPucUpto(vd),                          null, null),
            };

            main.Item().Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.6), "#FFFFFF", Border));
                l.PrimaryLayer().PaddingTop(Mm(1)).PaddingBottom(Mm(0.6)).PaddingHorizontal(Mm(4)).Column(c =>
                {
                    for (int i = 0; i < rows.Length; i += 2)
                    {
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Element(x => VahanRow(x, rows[i]));
                            r.ConstantItem(Mm(6));
                            
                            if (i + 1 < rows.Length)
                                r.RelativeItem().Element(x => VahanRow(x, rows[i + 1]));
                            else
                                r.RelativeItem(); // prevents crash if row count becomes odd
                        });
                    }

                    // Addresses run the full width: they are long enough that a half
                    // column would wrap them to three lines apiece.
                    c.Item().Element(x => VahanFullRow(x, "PRESENT ADDRESS", vd?.PresentAddress, false));
                    c.Item().Element(x => VahanFullRow(x, "PERMANENT ADDRESS", vd?.PermanentAddress, true));
                });
            });

            // ---------- regulatory documents
            DrawSectionHeading(main.Item().PaddingTop(Mm(7)).PaddingBottom(Mm(3)),
                               "shield-check", "Regulatory Documents & Insurance");

            var ins    = DocumentStatus(vd?.InsurancePolicyNo, vd?.InsuranceValidUpTo);
            var permit = DocumentStatus(vd?.PermitNo, vd?.PermitValidUpTo);
            var fit    = DocumentStatus(vd?.FitnessNo, vd?.FitnessValidTo);
            var tax    = TaxStatus(vd);
            var puc    = DocumentStatus(vd?.PollutionCertificateNumber, vd?.PollutionCertificateUpto);
            bool lien  = vd?.Hypothecation == true;

            var insurerLine = string.Join("\n", new[]
            {
                string.IsNullOrWhiteSpace(vd?.Insurer) ? null : vd!.Insurer,
                string.IsNullOrWhiteSpace(vd?.InsurancePolicyNo) ? null : $"Policy: {vd!.InsurancePolicyNo}",
            }.Where(x => x != null)!);

            var financierLine = lien 
                ? (string.IsNullOrWhiteSpace(vd?.Lender) ? "Lien detected" : vd!.Lender) 
                : "No lien on record";

            var cards = new (string Icon, string Name, string? Sub, string Tone, string Pill, string? Exp)[]
            {
                ("shield-check", "COMPREHENSIVE INSURANCE", insurerLine,
                 ins.Warn ? "avg" : "good", ins.Status, ins.Expiry),
                 
                ("landmark", "HYPOTHECATION (STATUS)", financierLine,
                 lien ? "avg" : "good", lien ? "YES" : "NO", null),
                 
                ("file-badge", "NATIONAL PERMIT",
                 permit.Status == "---" ? "Not available on record" : vd?.PermitType,
                 permit.Warn ? "avg" : "good", permit.Status == "---" ? "NOT FOUND" : permit.Status,
                 permit.Status == "---" ? null : permit.Expiry),
                 
                ("badge-check", "FITNESS CERTIFICATE", null,
                 fit.Warn ? "avg" : "good", fit.Status == "---" ? "NOT FOUND" : fit.Status,
                 fit.Status == "---" ? null : fit.Expiry),
                 
                ("receipt-text", "TAX VALIDITY", "Road tax / LTT status",
                 tax.Warn ? "avg" : "good", tax.Status, string.IsNullOrWhiteSpace(tax.Expiry) ? null : tax.Expiry),
                 
                // "(PUCC)" dropped from the title only because it wrapped to a second
                // line, which none of the other five cards do. The subtitle carries the
                // certificate number.
                ("clipboard-check", "POLLUTION CERTIFICATE",
                 string.IsNullOrWhiteSpace(vd?.PollutionCertificateNumber)
                     ? "Certificate number not on record"
                     : $"Certificate: {vd!.PollutionCertificateNumber}",
                 puc.Warn ? "avg" : "good", puc.Status == "---" ? "NOT FOUND" : puc.Status,
                 puc.Status == "---" ? null : puc.Expiry),
            };

            for (int i = 0; i < cards.Length; i += 2)
            {
                if (i > 0) main.Item().Height(Mm(4));
                // Whole rows only. A row that lands too near the foot of the page moves to
                // the next one intact; QuestPDF would otherwise split it, and a card torn
                // in two strands its last line alone at the top of a page.
                main.Item().ShowEntire().Row(r =>
                {
                    r.RelativeItem().Element(x => RegulatoryCard(x, cards[i]));
                    r.ConstantItem(Mm(4));
                    if (i + 1 < cards.Length)
                        r.RelativeItem().Element(x => RegulatoryCard(x, cards[i + 1]));
                    else
                        r.RelativeItem();   // odd card out keeps its column width
                });
            }

            // ---------- chassis identification
            var chassisCards = new (string Title, string Key)[]
            {
                ("CHASSIS VERIFICATION",  "ChassisVerification"),
                ("CHASSIS STENCIL TRACE", "ChassisStencilTrace"),
            };

            var present = chassisCards
                .Where(c => photos.ContainsKey(c.Key))
                .Select(c => (c.Title, Image: photos[c.Key]))
                .ToList();
            if (present.Count == 0) return;

            // Each strip starts at the height its own photograph asks for, and the crop
            // follows that height, so the whole width of the photograph survives. They
            // used to be a flat 14mm grown to fill the page: a stencil trace shot as a
            // 12.4:1 band was cropped to 10.3:1 and lost the first and last characters of
            // the chassis number (AP39X8199 printed MAT541170K1C05422 as AT541170K1C054).
            //
            // Where page 2 has no room for both at their natural height, they give height
            // back together — which crops them top and bottom, never at the ends — rather
            // than moving to a page of their own. Heights are worked out once; the fill
            // would otherwise decode both photographs at every step of its search.
            var strips = present.Select(c => (c.Title, c.Image, Natural: StripHeightMm(c.Image))).ToList();
            double tallest = strips.Max(s => s.Natural);

            main.Item().Dynamic(new FillPage((block, give, measuring) => block.Column(col =>
            {
                foreach (var (title, img, natural) in strips)
                    col.Item().PaddingTop(Mm(4.5))
                       .Element(x => ChassisCard(x, title, img, Math.Max(MinStripMm, natural + give), measuring));
            }), maxMm: 0, minMm: MinStripMm - tallest));
        }

        // ──────────────────────────────────────────────
        // Page 2 parts
        // ──────────────────────────────────────────────

        private void VahanRow(IContainer container, (string Label, string? Value, string? Color, string? Pill) row)
        {
            // The label takes its natural width and the value everything left. It was the
            // other way round: the value sat in an auto-width slot, so a long enough VAHAN
            // value — a full RTO name, a long model — was laid out wider than the half
            // column, left the label a negative width, and QuestPDF threw rather than
            // render the report. Labels are fixed strings of bounded length; values are
            // not, so the value is the one that must wrap.
            container.BorderBottom(1).BorderColor(BorderIn).PaddingVertical(Mm(2)).Row(r =>
            {
                r.AutoItem().AlignMiddle().Text(row.Label)
                    .FontFamily(ReportFont).FontSize(7.2f).Bold().FontColor(Label)
                    .LetterSpacing(Ls(0.25, 7.2));
                r.ConstantItem(Mm(2));

                if (row.Pill != null)
                    // VAHAN's RC status is free text; held to a pill's worth so an unusual
                    // one cannot overrun the column the same way.
                    r.RelativeItem().AlignMiddle().AlignRight().Element(c =>
                        DrawPill(c, row.Pill.Length > 22 ? row.Pill[..21] + "…" : row.Pill,
                                 row.Pill == "ACTIVE" ? "good" : "neutral", fontPt: 7.4, padX: 2.6, padY: 0.6));
                else
                    r.RelativeItem().AlignMiddle().AlignRight()
                        .Text(string.IsNullOrWhiteSpace(row.Value) ? "-" : row.Value)
                        .FontFamily(ReportFont).FontSize(9).Bold().FontColor(row.Color ?? Navy)
                        .AlignRight();
            });
        }

        private void VahanFullRow(IContainer container, string label, string? value, bool last)
        {
            container.BorderBottom(last ? 0 : 1).BorderColor(BorderIn).PaddingVertical(Mm(2)).Row(r =>
            {
                r.AutoItem().AlignMiddle().Text(label)
                    .FontFamily(ReportFont).FontSize(7.2f).Bold().FontColor(Label)
                    .LetterSpacing(Ls(0.25, 7.2));
                r.ConstantItem(Mm(6));
                r.RelativeItem().AlignMiddle().AlignRight()
                    .Text(string.IsNullOrWhiteSpace(value) ? "-" : value)
                    .FontFamily(ReportFont).FontSize(8.4f).Bold().FontColor(Navy);
            });
        }

        /// <summary>A regulatory document card: coloured left edge, icon, name, status.</summary>
        private void RegulatoryCard(IContainer container,
            (string Icon, string Name, string? Sub, string Tone, string Pill, string? Exp) card)
        {
            var edge = card.Tone == "good" ? RingGreen : RingAmber;
            // 19mm is a floor, not a fixed height. A title that wraps -- COMPREHENSIVE
            // INSURANCE beside the wider ON RECORD pill does -- makes the card ~1mm taller
            // than that, and a fixed Height() does not clip in QuestPDF: it continued the
            // card on the next page (PM-758104-K: page 2 half empty, "Policy: …" alone on
            // page 3). The Row hands both cards of a pair its full height, so they stay level.
            container.MinHeight(Mm(19)).Layers(l =>
            {
                l.Layer().Svg(s => LeftBarCard(s.Width, s.Height, Mm(2.6), Mm(1.3), edge, "#FFFFFF", Border));
                l.PrimaryLayer().PaddingVertical(Mm(2.8)).PaddingLeft(Mm(3.6)).PaddingRight(Mm(3.6)).Row(r =>
                {
                    r.AutoItem().AlignMiddle().Element(c => DrawChip(c, card.Icon, 8.2, 4.6));
                    r.ConstantItem(Mm(3));
                    r.RelativeItem().AlignMiddle().Column(c =>
                    {
                        c.Item().Text(card.Name)
                            .FontFamily(ReportFont).FontSize(9.4f).Bold().FontColor(Navy)
                            .LetterSpacing(Ls(0.15, 9.4));
                        if (!string.IsNullOrWhiteSpace(card.Sub))
                            c.Item().Text(card.Sub)
                                .FontFamily(ReportFont).FontSize(7.2f).FontColor(Label).LineHeight(1.25f);
                    });
                    r.ConstantItem(Mm(2));
                    r.AutoItem().AlignMiddle().Column(c =>
                    {
                        c.Item().AlignRight().Element(x =>
                            DrawPill(x, card.Pill, card.Tone, fontPt: 7.4, padX: 2.8, padY: 0.8));
                        if (!string.IsNullOrWhiteSpace(card.Exp))
                            c.Item().PaddingTop(Mm(1)).AlignRight().Text(card.Exp)
                                .FontFamily(ReportFont).FontSize(7).Bold().FontColor(Label);
                    });
                });
            });
        }

        /// <summary>A chassis identification card: title, then the photo strip.</summary>
        /// <summary>The card's inner width: what a strip's crop is measured against.</summary>
        private const double ChassisStripWidthMm = 178.8;

        /// <summary>A strip never goes below this, however little room the page has left.</summary>
        private const double MinStripMm = 8;

        /// <summary>
        /// The height a chassis strip asks for — the card's inner width over the
        /// photograph's own shape, so the whole width of the photograph survives the crop.
        /// Capped at 26mm, which crops a 4:3 photo of the chassis area down to a band, as
        /// the template intends; cropping at that point only takes off the top and bottom.
        /// </summary>
        private static double StripHeightMm(byte[] image)
        {
            try
            {
                using var bmp = SKBitmap.Decode(image);
                if (bmp is { Width: > 0, Height: > 0 })
                    return Math.Clamp(ChassisStripWidthMm * bmp.Height / bmp.Width, MinStripMm, 26);
            }
            catch { /* unreadable frame keeps the template's own band */ }
            return 14;
        }

        private void ChassisCard(IContainer container, string title, byte[] image,
                                 double stripMm, bool measuring)
        {
            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.8), "#FFFFFF", Border));
                l.PrimaryLayer().PaddingVertical(Mm(2.4)).PaddingHorizontal(Mm(3.6)).Column(c =>
                {
                    c.Item().PaddingBottom(Mm(2.2)).Row(r =>
                    {
                        r.AutoItem().AlignMiddle().Element(x => DrawChip(x, "scan-search", 7.4, 4.2));
                        r.ConstantItem(Mm(2.4));
                        r.RelativeItem().AlignMiddle().Column(t =>
                        {
                            // No VERIFIED pill. The report does not verify the punch —
                            // it reproduces the photograph and lets the reader judge.
                            t.Item().Text(title)
                                .FontFamily(ReportFont).FontSize(9.4f).Bold().FontColor(Navy);
                        });
                    });

                    // The crop follows the strip's shape, which follows the photo's.
                    c.Item().Height(Mm(stripMm)).Layers(img =>
                    {
                        img.PrimaryLayer().Element(x =>
                        {
                            if (measuring) return;   // only its height matters while measuring
                            x.Image(CropToAspect(image, (float)(ChassisStripWidthMm / stripMm))).FitUnproportionally();
                        });
                        img.Layer().Svg(s => RoundedPhotoMask(s.Width, s.Height, Mm(1.6), Border));
                    });
                });
            });
        }
    }
}