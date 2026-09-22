using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Valuation.Api.Models;

namespace Valuation.Api.Services
{
    /// <summary>
    /// Page 1, laid out to the approved template. Widths here are the template's own
    /// millimetre values, not proportions: the content column is 186mm (A4 less the
    /// 12mm gutters) and the blocks divide it as the design states.
    /// </summary>
    public partial class PdfReportService
    {
        /// <summary>
        /// The four systems the cover rates, and the checklist sections that feed each.
        ///
        /// The inspection registry carries eleven sections in the same order for every
        /// vehicle type; this folds them into the four the design shows. Matched by
        /// name rather than by index so a registry edit cannot silently re-file a
        /// section — anything unrecognised lands in STRUCTURAL, which is where the
        /// body/cabin/attachment sections all differ by vehicle type.
        /// </summary>
        private static string? CategoryOf(string sectionName)
        {
            var n = sectionName.Trim().ToUpperInvariant();
            if (n is "FUNCTIONALITY" or "OTHER SYSTEMS") return null;   // listed, never rated
            if (n == "ELECTRICAL SYSTEM") return "electrical";
            if (n.StartsWith("TIRE") || n.StartsWith("TYRE")) return "tyres";
            if (n is "ENGINE CONDITION" or "TRANSMISSION SYSTEM" or "BRAKES"
                  or "STEERING SYSTEM" or "SUSPENSION SYSTEM" or "HYDRAULIC SYSTEM") return "mechanical";
            return "structural";
        }

        private static readonly (string Key, string Name, string Icon)[] CoverCategories =
        {
            ("mechanical", "MECHANICAL\nSYSTEMS", "cog"),
            ("structural", "STRUCTURAL\nSYSTEMS", "layers"),
            ("electrical", "ELECTRICAL",          "zap"),
            ("tyres",      "TYRES",               "disc-3"),
        };

        /// <summary>
        /// Score per cover category, plus the overall.
        ///
        /// Item scoring is the report's existing arithmetic (<see cref="CalculateSystemScoreOrNull"/>),
        /// which the portal's inspection-score.ts also implements — the template's own
        /// GOOD/AVERAGE/POOR-only scale was a simplification of the sample data and would
        /// have silently rescored Fluid Leaks and Missing Tyres. Items are pooled across
        /// the category's sections before averaging, so a category is not skewed by a
        /// section that happens to hold three fields against another's seven.
        ///
        /// The overall is NOT the mean of these four. It stays
        /// <see cref="CalculateOverallVehicleScore"/> — the mean across every scored
        /// section — because that is the figure the portal shows on the QC screen and
        /// the one the team reads back. Averaging the four categories instead quietly
        /// reweights the report: it gives TYRES, a two-question section, the same say as
        /// MECHANICAL's eighteen, and printed 8.1 against the portal's 8.4 on TG08U7014.
        /// </summary>
        private (Dictionary<string, double> Categories, double Overall) CoverScores(ValuationDocument doc)
        {
            var result = new Dictionary<string, double>();
            var ins = doc.InspectionDetails;
            if (ins == null) return (result, ParseScoreValue(doc.QualityControl?.OverallRating));

            var vk = ResolveVehicleTypeKey(doc);
            if (!PdfFieldRegistry.TryGetValue(vk, out var sections) || sections.Length == 0)
                sections = PdfFieldRegistry["cv"];

            var pooled = new Dictionary<string, Dictionary<string, string?>>();
            foreach (var sec in sections)
            {
                if (!IsScored(sec)) continue;
                var cat = CategoryOf(sec.Name);
                if (cat == null) continue;
                if (!pooled.TryGetValue(cat, out var bag))
                    pooled[cat] = bag = new Dictionary<string, string?>();
                foreach (var kv in ScorableItems(sec, ins))
                    bag[$"{sec.Name}|{kv.Key}"] = kv.Value;   // section-qualified: two sections may share a label
            }

            foreach (var (key, _, _) in CoverCategories)
                if (pooled.TryGetValue(key, out var bag))
                {
                    var s = CalculateSystemScoreOrNull(bag);
                    if (s.HasValue) result[key] = s.Value;
                }

            return (result, CalculateOverallVehicleScore(doc));
        }

        /// <summary>
        /// Age, and distance per year. Both are stated on the cover's fact strip and
        /// neither is stored, so they are derived the way the template derives them:
        /// whole months between manufacture and inspection.
        ///
        /// Returns nulls rather than zeros when the dates are missing — "0 yrs 0 mo"
        /// on a report reads as a fact, where a dash reads as the absence of one.
        /// </summary>
        private static (int? Years, int? Months, long? KmPerYear) DeriveAge(ValuationDocument doc)
        {
            var vd = doc.VehicleDetails;
            int? mfgYear = vd?.YearOfMfg ?? vd?.ManufacturedDate?.Year;
            int? mfgMonth = vd?.MonthOfMfg ?? vd?.ManufacturedDate?.Month;
            if (mfgYear is null or <= 0) return (null, null, null);
            mfgMonth ??= 1;

            var asOf = doc.InspectionDetails?.DateOfInspection ?? doc.CreatedAt;
            if (asOf == default) asOf = DateTime.UtcNow;

            int months = (asOf.Year - mfgYear.Value) * 12 + (asOf.Month - mfgMonth.Value);
            if (months < 0) months = 0;

            long? odo = doc.VehicleDetails?.Odometer ?? doc.InspectionDetails?.Odometer;
            long? perYear = null;
            if (odo is > 0 && months > 0)
                perYear = (long)(Math.Round(odo.Value / (months / 12.0) / 100.0) * 100);

            return (months / 12, months % 12, perYear);
        }

        /// <summary>1st / 2nd / 3rd / 4th owner, from the RC's owner serial.</summary>
        private static string OwnerOrdinal(string? serial)
        {
            if (!int.TryParse(serial?.Trim(), out var n) || n <= 0) return "-";
            string suffix = (n % 100 is >= 11 and <= 13) ? "th"
                          : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return $"{n}{suffix} owner";
        }

        /// <summary>Digits grouped the Indian way: 1,36,862.</summary>
        private static string Indian(long n)
        {
            var s = Math.Abs(n).ToString(CultureInfo.InvariantCulture);
            if (s.Length <= 3) return (n < 0 ? "-" : "") + s;
            var head = s[..^3];
            var parts = new List<string>();
            while (head.Length > 2) { parts.Insert(0, head[^2..]); head = head[..^2]; }
            if (head.Length > 0) parts.Insert(0, head);
            parts.Add(s[^3..]);
            return (n < 0 ? "-" : "") + string.Join(",", parts);
        }

        // ──────────────────────────────────────────────
        // Card chrome
        // ──────────────────────────────────────────────

        /// <summary>
        /// The fact-strip card: a thick coloured top edge over a bordered, rounded box.
        /// Drawn as one SVG because QuestPDF's own borders do not follow a corner radius —
        /// a bordered element with a radius prints square corners with a rounded fill
        /// showing through them.
        /// </summary>
        private static string TopEdgeCard(float w, float h, float radius, float topThickness,
                                          string topColor, string bg, string border)
        {
            radius = ClampRadius(radius, w, h);
            const float bw = 1f;   // 0.3mm side border, in points, near enough at this scale
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\">" +
                   $"<rect x=\"0\" y=\"0\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"{F(radius)}\" fill=\"{topColor}\"/>" +
                   $"<rect x=\"{F(bw)}\" y=\"{F(topThickness)}\" width=\"{F(Math.Max(0, w - bw * 2))}\" " +
                   $"height=\"{F(Math.Max(0, h - topThickness - bw))}\" rx=\"{F(Math.Max(0, radius - bw))}\" fill=\"{bg}\"/>" +
                   $"<rect x=\"0.5\" y=\"0.5\" width=\"{F(Math.Max(0, w - 1))}\" height=\"{F(Math.Max(0, h - 1))}\" " +
                   $"rx=\"{F(radius)}\" fill=\"none\" stroke=\"{border}\" stroke-width=\"1\"/></svg>";
        }

        // ──────────────────────────────────────────────
        // PAGE 1 — Cover
        // ──────────────────────────────────────────────

        private void ComposeCoverPage(ColumnDescriptor main, ValuationDocument doc,
            Dictionary<string, byte[]> photos, byte[] qrCode, string referenceNumber)
        {
            var vd  = doc.VehicleDetails;
            var ins = doc.InspectionDetails;
            var (cats, overall) = CoverScores(doc);
            var band = Band(overall);

            // ---------- title: reg number, make/model, segment pill
            main.Item().PaddingBottom(Mm(3)).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Row(t =>
                    {
                        t.AutoItem().AlignMiddle().Width(Mm(1.8)).Height(Mm(8.5))
                            .Svg(s => RoundRect(s.Width, s.Height, Mm(1), Orange));
                        t.ConstantItem(Mm(3));
                        t.AutoItem().AlignMiddle()
                            .Text(vd?.RegistrationNumber?.ToUpperInvariant() ?? "REGISTRATION PENDING")
                            .FontFamily(ReportFont).FontSize(26).Bold().FontColor(Navy)
                            .LetterSpacing(Ls(0.5, 26)).LineHeight(1f);
                    });
                    col.Item().PaddingTop(Mm(1.6)).PaddingLeft(Mm(4.8))
                        .Text($"{vd?.Make} {vd?.Model}".Trim().ToUpperInvariant())
                        .FontFamily(ReportFont).FontSize(8.8f).Bold().FontColor(Teal)
                        .LetterSpacing(Ls(0.3, 8.8));
                });

                row.AutoItem().AlignMiddle().Element(c => DrawPill(c,
                    (doc.Stakeholder?.ValuationType ?? "RETAIL").ToUpperInvariant(), "orange",
                    fontPt: 10.5, padX: 6, padY: 1.7, letterSpacing: 0.6));
            });

            // ---------- hero: photo | score card over the four rating tiles
            main.Item().Row(row =>
            {
                row.ConstantItem(Mm(100)).Height(Mm(75)).Layers(l =>
                {
                    l.PrimaryLayer().Element(c =>
                    {
                        byte[]? img = photos.GetValueOrDefault("FrontViewGrille")
                                   ?? photos.GetValueOrDefault("FrontView")
                                   ?? photos.GetValueOrDefault("FrontLeftSide")
                                   ?? photos.Values.FirstOrDefault();
                        if (img != null) c.Image(img).FitUnproportionally();
                        else c.Background("#F8FAFC").AlignCenter().AlignMiddle()
                              .Text("NO IMAGE AVAILABLE").FontFamily(ReportFont).FontSize(10).FontColor(Label);
                    });
                    // Rounded mask plus border, over the photo: the same trick the old
                    // cover used, kept because QuestPDF cannot clip an image to a radius.
                    l.Layer().Svg(s => RoundedPhotoMask(s.Width, s.Height, Mm(3), BorderImg));
                });

                row.ConstantItem(Mm(4));

                row.RelativeItem().Column(side =>
                {
                    // score card
                    side.Item().Height(Mm(30)).Layers(l =>
                    {
                        l.Layer().Svg(s => RoundRectGradient(s.Width, s.Height, Mm(3),
                            new[] { (Tint, 0.0), ("#FFFFFF", 100.0) }, TintB));
                        l.PrimaryLayer().PaddingVertical(Mm(3)).PaddingHorizontal(Mm(3.5)).Row(r =>
                        {
                            r.AutoItem().AlignMiddle().Element(c =>
                                DrawRing(c, overall, 24, 9, 21, sub: true));
                            r.ConstantItem(Mm(3.5));
                            r.RelativeItem().AlignMiddle().Column(c =>
                            {
                                c.Item().Text("OVERALL VEHICLE SCORE")
                                    .FontFamily(ReportFont).FontSize(7.6f).Bold().FontColor(Teal)
                                    .LetterSpacing(Ls(0.3, 7.6));
                                // No dedupe pill here. It said the same thing as the
                                // DEDUPE tab in the market-value row a few inches below,
                                // and the score card reads as a score without it.
                                c.Item().PaddingTop(Mm(0.8))
                                    .Text($"{Capitalise(band.Word)} condition")
                                    .FontFamily(ReportFont).FontSize(15).Bold().FontColor(Navy).LineHeight(1.1f);
                            });
                        });
                    });

                    // "CONDITION VERDICT — INDIVIDUAL RATINGS"
                    side.Item().PaddingTop(Mm(3)).PaddingBottom(Mm(1.8)).Row(r =>
                    {
                        r.AutoItem().AlignMiddle().Element(c => DrawIcon(c, "shield-check", Orange, 3.6));
                        r.ConstantItem(Mm(1.6));
                        r.RelativeItem().AlignMiddle().Text("CONDITION VERDICT — INDIVIDUAL RATINGS")
                            .FontFamily(ReportFont).FontSize(7.4f).Bold().FontColor(Navy)
                            .LetterSpacing(Ls(0.25, 7.4));
                    });

                    // 2 x 2 rating tiles
                    for (int i = 0; i < 2; i++)
                    {
                        int rowStart = i * 2;
                        if (i > 0) side.Item().Height(Mm(2.2));
                        side.Item().Row(tr =>
                        {
                            for (int j = 0; j < 2; j++)
                            {
                                if (j > 0) tr.ConstantItem(Mm(2.2));
                                var (key, name, icon) = CoverCategories[rowStart + j];
                                double? score = cats.TryGetValue(key, out var sc) ? sc : null;
                                tr.RelativeItem().Element(c => RatingTileV2(c, name, icon, score));
                            }
                        });
                    }
                });
            });

            // ---------- client strip
            main.Item().PaddingTop(Mm(3)).Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.6), "#FFFFFF", Border));
                l.PrimaryLayer().Row(r =>
                {
                    // BRANCH is the client's branch from the stakeholder page; PLACE OF
                    // INSPECTION is where the AVO inspected it, from the AVO page. BRANCH
                    // used to print the inspection location as well, so both boxes always
                    // read the same.
                    var place = ins?.InspectionLocation?.ToUpperInvariant();
                    var cells = new (string Icon, string Label, string? Value)[]
                    {
                        ("building-2", "CLIENT",              doc.Stakeholder?.Name?.ToUpperInvariant()),
                        ("map-pin",    "BRANCH",              doc.Stakeholder?.Branch?.Trim().ToUpperInvariant()),
                        ("calendar",   "DATE OF INSPECTION",  ins?.DateOfInspection?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                        ("map-pin",    "PLACE OF INSPECTION", place),
                    };
                    for (int i = 0; i < cells.Length; i++)
                    {
                        var (icon, label, value) = cells[i];
                        r.RelativeItem()
                         .BorderRight(i < cells.Length - 1 ? 1 : 0).BorderColor(BorderIn)
                         .PaddingVertical(Mm(2.4)).PaddingHorizontal(Mm(3))
                         .Row(cell =>
                         {
                             cell.AutoItem().AlignMiddle().Element(c => DrawChip(c, icon, 6.6, 3.6));
                             cell.ConstantItem(Mm(2.4));
                             cell.RelativeItem().AlignMiddle().Element(c => DrawField(c, label, value));
                         });
                    }
                });
            });

            // ---------- five-fact strip
            var (ageY, ageM, kmYear) = DeriveAge(doc);
            bool lien = vd?.Hypothecation == true;
            var (insStatus, insExpiry, insWarn) = InsuranceFact(vd);

            main.Item().PaddingTop(Mm(3)).PaddingBottom(Mm(3.6)).Row(r =>
            {
                void Fact(string label, string value, string? sub, string topColor, string bg)
                {
                    r.RelativeItem().Layers(l =>
                    {
                        l.Layer().Svg(s => TopEdgeCard(s.Width, s.Height, Mm(2.4), Mm(0.9), topColor, bg, Border));
                        l.PrimaryLayer().PaddingVertical(Mm(2.2)).PaddingHorizontal(Mm(2.6)).Column(c =>
                        {
                            c.Item().Text(label.ToUpperInvariant())
                                .FontFamily(ReportFont).FontSize(6.6f).Bold().FontColor(Label)
                                .LetterSpacing(Ls(0.25, 6.6));
                            c.Item().PaddingTop(Mm(0.5)).Text(value)
                                .FontFamily(ReportFont).FontSize(10.4f).Bold().FontColor(Navy);
                            c.Item().Text(sub ?? "").FontFamily(ReportFont).FontSize(7).FontColor(Label);
                        });
                    });
                }

                Fact("Vehicle age",
                     ageY.HasValue ? $"{ageY} {(ageY == 1 ? "yr" : "yrs")} {ageM} mo" : "-",
                     $"Mfg {ResolveMfgYear(vd)}", Teal, "#FFFFFF");
                r.ConstantItem(Mm(2.4));

                long? odo = vd?.Odometer ?? ins?.Odometer;
                Fact("Odometer",
                     odo.HasValue ? $"{Indian(odo.Value)} km" : "-",
                     kmYear.HasValue ? $"≈ {Indian(kmYear.Value)} km / year" : null, Teal, "#FFFFFF");
                r.ConstantItem(Mm(2.4));

                Fact("Ownership", OwnerOrdinal(vd?.OwnerSerialNo),
                     $"RC status: {Capitalise(ResolveRcStatus(vd) ?? "-")}", Teal, "#FFFFFF");
                r.ConstantItem(Mm(2.4));

                Fact("Hypothecation", lien ? "Yes" : "No",
                     lien ? "Verify closure / NOC" : "No charge on record",
                     lien ? RingAmber : RingGreen, lien ? "#FFFBF1" : "#FFFFFF");
                r.ConstantItem(Mm(2.4));

                Fact("Insurance", insStatus, insExpiry,
                     insWarn ? RingAmber : RingGreen, insWarn ? "#FFFBF1" : "#FFFFFF");
            });

            // ---------- asset identity
            DrawSectionHeading(main.Item().PaddingBottom(Mm(3)), "fingerprint", "Asset Identity");

            var ident = new (string Icon, string Label, string? Value)[]
            {
                // Names upper-cased like every other value here: the applicant is printed
                // as typed on the stakeholder page ("B Prabhakar Reddy"), and VAHAN
                // sometimes returns the owner in mixed case too ("Ch Arun Kumar").
                ("user",         "OWNER",            vd?.OwnerName?.Trim().ToUpperInvariant()),
                ("user-check",   "APPLICANT",        doc.Stakeholder?.Applicant?.Name?.Trim().ToUpperInvariant()),
                ("shield-check", "CHASSIS NUMBER",   vd?.ChassisNumber),
                ("cog",          "ENGINE NUMBER",    vd?.EngineNumber),
                ("calendar",     "MANUFACTURE YEAR", ResolveMfgYear(vd)),
                ("calendar-check","REGISTERED ON",   vd?.DateOfRegistration?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                ("fuel",         "FUEL TYPE",        vd?.Fuel),
                ("settings-2",   "TRANSMISSION",     ins?.TransmissionType),
                ("palette",      "COLOUR",           vd?.Colour),
                ("gauge",        "ODO METER",        (vd?.Odometer ?? ins?.Odometer) is long o && o > 0 ? $"{Indian(o)} km" : null),
                // Upper-cased to match the VAHAN page, which already does. VAHAN returns
                // this field in mixed case, so the same vehicle read "Goods Carrier(LGV)"
                // on the cover and "GOODS CARRIER(LGV)" two pages later.
                ("truck",        "VEHICLE TYPE",     vd?.ClassOfVehicle?.ToUpperInvariant()),
                ("users",        "OWNERSHIP NUMBER", vd?.OwnerSerialNo),
            };

            // Rows sit 1.5mm apart below their underlines; the padding inside each row
            // gave up 0.25mm a side to pay for most of it, so a cover with two-line
            // owner and applicant names still keeps its sign-off on page one.
            for (int rowIdx = 0; rowIdx < 4; rowIdx++)
            {
                main.Item().PaddingTop(rowIdx > 0 ? Mm(1.5) : 0).Row(r =>
                {
                    for (int col = 0; col < 3; col++)
                    {
                        if (col > 0) r.ConstantItem(Mm(5));
                        var f = ident[rowIdx * 3 + col];
                        r.RelativeItem().BorderBottom(1).BorderColor(BorderIn)
                         .PaddingVertical(Mm(0.8)).Row(cell =>
                         {
                             cell.AutoItem().AlignMiddle().Element(c => DrawChip(c, f.Icon, 6.6, 3.6));
                             cell.ConstantItem(Mm(2.4));
                             cell.RelativeItem().AlignMiddle().Element(c => DrawField(c, f.Label, f.Value));
                         });
                    }
                });
            }

            // ---------- valuation & checks
            // "Valuation & Checks", not "Estimated Market Value": the row carries the
            // figure AND the chassis punch, dedupe and blacklist outcomes, and the old
            // name described only the first of the three.
            DrawSectionHeading(main.Item().PaddingTop(Mm(3.6)).PaddingBottom(Mm(3)),
                               "indian-rupee", "Valuation & Checks");

            // Laid out to the 2026-09-21 mockup: the value card; the chassis punch and
            // dedupe verdicts as two stacked cards; blacklist and the two media links as
            // a column of pills. Widths 3 : 3 : 2, the mockup's own proportions — the
            // verdict cards need the room for "VERIFIED CLEAN" at 13pt, the pills do not.
            const double RowMm = 34, GapMm = 2;
            var punch  = ChassisPunchCheck(doc);
            var dedupe = DedupeCheck(doc);
            var black  = BlacklistCheck(doc);

            string? galleryUrl = (_blobContainer != null && !string.IsNullOrWhiteSpace(_blobBaseUrl))
                ? $"{_blobBaseUrl.TrimEnd('/')}/{_blobContainer.Name}/{referenceNumber}-gallery.html"
                : null;
            string? videoUrl = null;
            doc.VideoUrls?.TryGetValue("VehicleVideo", out videoUrl);

            main.Item().Row(r =>
            {
                // the value card
                r.RelativeItem(3).Height(Mm(RowMm)).Layers(l =>
                {
                    l.Layer().Svg(s => RoundRectGradient(s.Width, s.Height, Mm(3.2),
                        new[] { (BigA, 0.0), (BigB, 55.0), (BigC, 100.0) }));
                    l.PrimaryLayer().PaddingVertical(Mm(4.5)).PaddingHorizontal(Mm(5)).Column(c =>
                    {
                        // The card names the figure: the section heading no longer does,
                        // and without this the cover shows a sum and never says what it is.
                        c.Item().Text("ESTIMATED MARKET VALUE")
                            .FontFamily(ReportFont).FontSize(8).Bold().FontColor(ValueLabelInk)
                            .LetterSpacing(Ls(0.3, 8));

                        // The roundel sits in the row rather than floating over it, so a
                        // longer figure pushes against it instead of printing under it.
                        var amount = $"₹ {FormatIndianCurrency(doc.QualityControl?.ValuationAmount ?? 0)}";
                        c.Item().Height(Mm(16.5)).Row(a =>
                        {
                            a.RelativeItem().AlignMiddle().Text(amount)
                                .FontFamily(ReportFont).FontSize(AmountFontSize(amount))
                                .Bold().FontColor(Colors.White);
                            a.ConstantItem(Mm(2));
                            a.AutoItem().AlignMiddle().Width(Mm(9.5)).Height(Mm(9.5)).Layers(b =>
                            {
                                b.Layer().Svg(s => CircleSvg(s.Width, s.Height, Orange));
                                b.PrimaryLayer().AlignCenter().AlignMiddle()
                                 .Element(x => DrawIcon(x, "indian-rupee", "#FFFFFF", 5));
                            });
                        });

                        c.Item().Text("Calculated based on current market trends")
                            .FontFamily(ReportFont).FontSize(7.4f).Italic().FontColor(BigL);
                    });
                });

                r.ConstantItem(Mm(4));

                // chassis punch over dedupe
                double cardMm = (RowMm - GapMm) / 2;
                r.RelativeItem(3).Column(c =>
                {
                    c.Item().Height(Mm(cardMm)).Element(x => CheckCard(x, "CHASSIS PUNCH", punch));
                    c.Item().Height(Mm(GapMm));
                    c.Item().Height(Mm(cardMm)).Element(x => CheckCard(x, "DEDUPE", dedupe));
                });

                r.ConstantItem(Mm(4));

                // blacklist, then the two links
                double pillMm = (RowMm - 2 * GapMm) / 3;
                r.RelativeItem(2).Column(c =>
                {
                    c.Item().Height(Mm(pillMm)).Element(x => StatusPill(x, black.Icon, black.Text, black.Tone));
                    c.Item().Height(Mm(GapMm));
                    c.Item().Height(Mm(pillMm)).Element(x => LinkPill(x, "camera", "IMAGE LINK", galleryUrl));
                    c.Item().Height(Mm(GapMm));
                    c.Item().Height(Mm(pillMm)).Element(x => LinkPill(x, "video", "VIDEO LINK", videoUrl));
                });
            });

            // ---------- remarks, QR, sign-off
            //
            // The cover's last row takes whatever the page has left, in the remarks box —
            // between 4 and 15mm depending on how much the names and addresses above it
            // wrap. The QR and the signature settle to the foot of the row. Capped at
            // 40mm: if long data pushes this row onto a page of its own, it should not
            // swell to fill that one.
            main.Item().Dynamic(new FillPage((block, extra, _) => block.PaddingTop(Mm(3.4)).Row(r =>
            {
                r.RelativeItem().Height(Mm(25 + extra)).Layers(l =>
                {
                    l.Layer().Svg(s => LeftBarCard(s.Width, s.Height, Mm(2.6), Mm(1.2), Orange, "#F6F8FB", Border));
                    l.PrimaryLayer().PaddingVertical(Mm(3.2)).PaddingHorizontal(Mm(4)).Column(c =>
                    {
                        c.Item().Text("REMARKS")
                            .FontFamily(ReportFont).FontSize(6.6f).Bold().FontColor(Orange)
                            .LetterSpacing(Ls(0.25, 6.6));
                        var (remarkText, remarkPt) =
                            RemarksStyle(doc.QualityControl?.Remarks ?? "Vehicle found in good road worthy condition.");
                        c.Item().PaddingTop(Mm(1.2))
                            .Text($"“{remarkText}”")
                            .FontFamily(ReportFont).FontSize(remarkPt).Italic().FontColor(InkSoft).LineHeight(1.35f);
                    });
                });

                r.ConstantItem(Mm(4));

                r.ConstantItem(Mm(30)).AlignBottom().Column(c =>
                {
                    if (qrCode.Length > 0)
                        c.Item().AlignCenter().Width(Mm(20.5)).Height(Mm(20.5))
                            .Hyperlink($"https://prontofirebase.web.app/verify/{referenceNumber}")
                            .Image(qrCode).FitArea();
                    c.Item().PaddingTop(Mm(1)).AlignCenter().Text("VERIFY ONLINE")
                        .FontFamily(ReportFont).FontSize(6.4f).Bold().FontColor(Teal)
                        .LetterSpacing(Ls(0.25, 6.4));
                });

                r.ConstantItem(Mm(4));

                r.ConstantItem(Mm(60)).AlignBottom().Column(c =>
                {
                    c.Item().AlignRight().Text("Approved by")
                        .FontFamily(ReportFont).FontSize(7.6f).FontColor(Label);
                    c.Item().PaddingTop(Mm(0.6)).AlignRight().Text(ApproverName)
                        .FontFamily(ReportFont).FontSize(15).Bold().Italic().FontColor(Navy).LineHeight(1.1f);
                    c.Item().AlignRight().Text(ApproverDesignation)
                        .FontFamily(ReportFont).FontSize(7.6f).FontColor(Label);
                    c.Item().PaddingTop(Mm(1.4)).AlignRight().Element(x =>
                        DrawPill(x, $"AUDIT STATUS: {(doc.QualityControl != null ? "CERTIFIED" : "PENDING")}",
                                 "good", fontPt: 7, padX: 3, padY: 1.1, letterSpacing: 0.2,
                                 iconName: "badge-check", iconColor: Green, iconMm: 3.6));
                    c.Item().PaddingTop(Mm(0.8)).AlignRight().Text($"License No: {ApproverLicenseNo}")
                        .FontFamily(ReportFont).FontSize(7.6f).FontColor(Label);
                });
            }), maxMm: 40));
        }

        // ──────────────────────────────────────────────
        // Cover parts
        // ──────────────────────────────────────────────

        /// <summary>
        /// Remarks, sized to the box the design gives them.
        ///
        /// The template fixes this card at 25mm and lets the page clip whatever does
        /// not fit — which in QuestPDF is not clipping but a page break, stranding the
        /// tail of a sentence on a second cover page. Roughly three lines fit at the
        /// specified 8.6pt across the 80mm text column, so longer remarks step down a
        /// size before anything is cut, and only a remark beyond about 330 characters
        /// is truncated. QC remarks are usually a sentence; this is for the ones that
        /// are not.
        /// </summary>
        private static (string Text, float FontSize) RemarksStyle(string remarks)
        {
            var text = remarks.Trim();
            if (text.Length <= 160) return (text, 8.6f);
            if (text.Length <= 240) return (text, 7.4f);
            if (text.Length <= 330) return (text, 6.6f);
            return (text[..327].TrimEnd() + "…", 6.6f);
        }

        /// <summary>
        /// How large the market value can be set.
        ///
        /// The figure shares its row with the rupee roundel, leaving ~128pt of the
        /// card's width. Indian amounts are 8, 10, 11, 13 or 14 characters with their
        /// grouping commas, and each length steps down so the figure stays on one line
        /// — a wrap pushes the card past its fixed height and the sign-off block onto
        /// a second page. Measured, not estimated: see the probe's --amount flag.
        /// </summary>
        private static float AmountFontSize(string amount) =>
            amount.Length <= 10 ? 24f
          : amount.Length <= 11 ? 21f
          : amount.Length <= 13 ? 18f
          :                       16f;

        /// <summary>A rating tile: ring, icon, and the system's name on two lines.</summary>
        private void RatingTileV2(IContainer container, string name, string iconName, double? score)
        {
            container.Height(Mm(18)).Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.4), "#FBFCFE", Border));
                l.PrimaryLayer().PaddingVertical(Mm(2.2)).PaddingHorizontal(Mm(2.4)).Row(r =>
                {
                    r.AutoItem().AlignMiddle().Element(c =>
                    {
                        // No inspection data for this system: an empty grey ring, rather
                        // than a 0.0 that reads as a failed system.
                        if (score.HasValue) DrawRing(c, score.Value, 11.5, 10, 9.5);
                        else c.Width(Mm(11.5)).Height(Mm(11.5)).Layers(b =>
                        {
                            b.Layer().Svg(RingSvg(0, 10, RingTrack, RingTrack));
                            b.PrimaryLayer().AlignCenter().AlignMiddle().Text("–")
                                .FontFamily(ReportFont).FontSize(9.5f).Bold().FontColor(LabelFaint);
                        });
                    });
                    r.ConstantItem(Mm(2.2));
                    r.RelativeItem().AlignMiddle().Column(c =>
                    {
                        c.Item().Element(x => DrawIcon(x, iconName, Orange, 3.8));
                        c.Item().PaddingTop(Mm(0.8)).Text(name)
                            .FontFamily(ReportFont).FontSize(7.6f).Bold().FontColor(Navy)
                            .LetterSpacing(Ls(0.15, 7.6)).LineHeight(1.12f);
                    });
                });
            });
        }

        /// <summary>The "ESTIMATED MARKET VALUE" caption on the value card, sampled from the mockup.</summary>
        private const string ValueLabelInk = "#E6F6F6";

        /// <summary>
        /// A verdict card: a ringed icon, a small caption, and the verdict in the tone's
        /// colour. The caption sits on one line — in the mockup "CHASSIS PUNCH" broke
        /// onto two and ran into the verdict beneath it.
        /// </summary>
        private void CheckCard(IContainer container, string caption,
                               (string Value, string Tone, string Icon) check)
        {
            var (bg, border, ring, glyph, ink) = CheckColors(check.Tone);
            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.6), bg, border));
                l.PrimaryLayer().PaddingLeft(Mm(3.7)).PaddingRight(Mm(3)).Row(r =>
                {
                    r.AutoItem().AlignMiddle().Width(Mm(8.4)).Height(Mm(8.4)).Layers(b =>
                    {
                        b.Layer().Svg(s => CircleSvg(s.Width, s.Height, "#FFFFFF", ring, 1.2f));
                        b.PrimaryLayer().AlignCenter().AlignMiddle()
                         .Element(x => DrawIcon(x, check.Icon, glyph, 4.2));
                    });
                    r.ConstantItem(Mm(2.9));
                    r.RelativeItem().AlignMiddle().Column(c =>
                    {
                        c.Item().Text(caption)
                            .FontFamily(ReportFont).FontSize(7).Bold().FontColor(CheckLabelInk)
                            .LetterSpacing(Ls(0.3, 7));
                        c.Item().PaddingTop(Mm(0.4)).Text(check.Value)
                            .FontFamily(ReportFont).FontSize(13).Bold().FontColor(ink)
                            .LetterSpacing(Ls(0.3, 13)).LineHeight(1f);
                    });
                });
            });
        }

        /// <summary>A one-line status pill: icon, then text, in the tone's colours.</summary>
        private void StatusPill(IContainer container, string icon, string text, string tone)
        {
            var (bg, border, _, glyph, ink) = CheckColors(tone);
            float size = PillFontSize(text);
            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.6), bg, border));
                l.PrimaryLayer().PaddingHorizontal(Mm(3.3)).Row(r =>
                {
                    r.AutoItem().AlignMiddle().Element(x => DrawIcon(x, icon, glyph, 4.2, 2.2));
                    r.ConstantItem(Mm(2.6));
                    r.RelativeItem().AlignMiddle().Text(text)
                        .FontFamily(ReportFont).FontSize(size).Bold().FontColor(ink)
                        .LetterSpacing(Ls(0.2, size));
                });
            });
        }

        /// <summary>
        /// A media link pill: blue with an arrow when the link exists. When it does not,
        /// grey with N/A — the old tabs stayed blue either way, so a missing video
        /// looked like a live link that simply did nothing when clicked.
        /// </summary>
        private void LinkPill(IContainer container, string icon, string text, string? url)
        {
            bool live = !string.IsNullOrWhiteSpace(url);
            var (bg, border, _, glyph, ink) = CheckColors(live ? "link" : "neutral");
            if (live) container = container.Hyperlink(url!);
            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.6), bg, border));
                l.PrimaryLayer().PaddingHorizontal(Mm(3.3)).Row(r =>
                {
                    r.AutoItem().AlignMiddle().Element(x => DrawIcon(x, icon, glyph, 4.2, 2.2));
                    r.ConstantItem(Mm(2.6));
                    r.RelativeItem().AlignMiddle().Text(text)
                        .FontFamily(ReportFont).FontSize(8).Bold().FontColor(ink)
                        .LetterSpacing(Ls(0.2, 8));
                    if (live)
                        r.AutoItem().AlignMiddle().Element(x => DrawIcon(x, "arrow-up-right", glyph, 3.4, 2.4));
                    else
                        r.AutoItem().AlignMiddle().Text("N/A")
                            .FontFamily(ReportFont).FontSize(6.5f).Bold().FontColor(LabelFaint);
                });
            });
        }

        /// <summary>
        /// Pill text size. The pill column is the narrowest on the row, and
        /// "BLACKLIST : PENDING" is the one label long enough to need stepping down.
        /// </summary>
        private static float PillFontSize(string text) => text.Length <= 15 ? 8f : 6.8f;

        /// <summary>The dedupe verdict: clean, a count of prior cases, or not yet run.</summary>
        private static (string Value, string Tone, string Icon) DedupeCheck(ValuationDocument doc)
        {
            var rec = doc.DedupeCheck;
            if (rec == null) return ("PENDING", "neutral", "circle-alert");
            if (rec.MatchCount <= 0) return ("VERIFIED CLEAN", "good", "circle-check");
            return ($"{rec.MatchCount} PRIOR CASE{(rec.MatchCount == 1 ? "" : "S")}", "avg", "circle-alert");
        }

        /// <summary>Chassis punch, as recorded at QC and confirmed at final report.</summary>
        private static (string Value, string Tone, string Icon) ChassisPunchCheck(ValuationDocument doc)
        {
            var raw = doc.QualityControl?.ChassisPunch?.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return ("NOT RECORDED", "neutral", "circle-alert");
            var v = raw.ToUpperInvariant();
            if (v is "OK" or "ORIGINAL" or "GOOD" or "TRUE" or "YES")
                return ("ORIGINAL", "good", "shield-check");
            if (v is "TAMPERED" or "FAKE" or "ALTERED" or "FALSE" or "NO")
                return ("TAMPERED", "poor", "triangle-alert");
            return (v, "avg", "circle-alert");
        }

        /// <summary>
        /// Insurance as the fact strip states it: the cover wants "Active" over
        /// "Valid till JUL 2027", where <see cref="DocumentStatus"/> returns a status
        /// already prefixed with "EXP:" for the regulatory cards on page 2.
        /// </summary>
        private static (string Status, string? Sub, bool Warn) InsuranceFact(VehicleDetailsDto? vd)
        {
            var upto = vd?.InsuranceValidUpTo;
            if (upto.HasValue)
            {
                var month = upto.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
                return upto.Value.Date >= DateTime.UtcNow.Date
                    ? ("Active",  $"Valid till {month}", false)
                    : ("Expired", $"Expired {month}",    true);
            }
            return string.IsNullOrWhiteSpace(vd?.InsurancePolicyNo)
                ? ("Not on record", null, true)
                : ("On record", "Expiry not stated", true);
        }

        /// <summary>
        /// Blacklist has three states, not two. PENDING means the check has not run —
        /// colouring that as a failure would put a red flag on a vehicle nothing is
        /// known against.
        /// </summary>
        private static (string Text, string Tone, string Icon) BlacklistCheck(ValuationDocument doc)
        {
            var label = BlacklistChip(doc).Label.ToUpperInvariant();
            return label switch
            {
                "NO"  => ("BLACKLIST : NO",  "good", "circle-check"),
                "YES" => ("BLACKLIST : YES", "poor", "circle-alert"),
                _     => ($"BLACKLIST : {label}", "neutral", "circle-alert"),
            };
        }

        /// <summary>Sentence case: "GOOD" becomes "Good", for the verdict line.</summary>
        private static string Capitalise(string? s) =>
            string.IsNullOrWhiteSpace(s) ? "-"
            : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

        /// <summary>White surround that rounds an image's corners, plus its border.</summary>
        private static string RoundedPhotoMask(float w, float h, float radius, string border)
        {
            radius = ClampRadius(radius, w, h);
            string wS = F(w), hS = F(h), rS = F(radius);
            // Clockwise from just right of the top-left corner. The previous path ran
            // anticlockwise from the same point, so its closing arc cut across the
            // top-right corner and left a notch there.
            string inner =
                $"M{rS},0 " +
                $"h{F(w - radius * 2)} a{rS},{rS} 0 0 1 {rS},{rS} " +
                $"v{F(h - radius * 2)} a{rS},{rS} 0 0 1 -{rS},{rS} " +
                $"h-{F(w - radius * 2)} a{rS},{rS} 0 0 1 -{rS},-{rS} " +
                $"v-{F(h - radius * 2)} a{rS},{rS} 0 0 1 {rS},-{rS} Z";
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{wS}\" height=\"{hS}\">" +
                   $"<path fill-rule=\"evenodd\" d=\"M0,0 h{wS} v{hS} h-{wS} Z {inner}\" fill=\"#FFFFFF\"/>" +
                   $"<rect x=\"0.5\" y=\"0.5\" width=\"{F(w - 1)}\" height=\"{F(h - 1)}\" rx=\"{rS}\" " +
                   $"fill=\"none\" stroke=\"{border}\" stroke-width=\"1\"/></svg>";
        }

        /// <summary>A card with a thick coloured left edge — the remarks box.</summary>
        private static string LeftBarCard(float w, float h, float radius, float barWidth,
                                          string barColor, string bg, string border)
        {
            radius = ClampRadius(radius, w, h);
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\">" +
                   $"<rect x=\"0\" y=\"0\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"{F(radius)}\" fill=\"{barColor}\"/>" +
                   $"<rect x=\"{F(barWidth)}\" y=\"0.5\" width=\"{F(Math.Max(0, w - barWidth - 0.5f))}\" " +
                   $"height=\"{F(Math.Max(0, h - 1))}\" rx=\"{F(Math.Max(0, radius - 1))}\" fill=\"{bg}\"/>" +
                   $"<rect x=\"0.5\" y=\"0.5\" width=\"{F(w - 1)}\" height=\"{F(h - 1)}\" rx=\"{F(radius)}\" " +
                   $"fill=\"none\" stroke=\"{border}\" stroke-width=\"1\"/></svg>";
        }
    }
}
