using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Valuation.Api.Models;

namespace Valuation.Api.Services
{
    /// <summary>
    /// Page 3 — the inspection checklist, grouped into the four systems the cover
    /// rates, with FUNCTIONALITY and OTHER SYSTEMS as full-width cards beneath.
    /// </summary>
    public partial class PdfReportService
    {
        /// <summary>How the checklist banner names each vehicle type.</summary>
        private static string VehicleTypeLabel(string key) => key switch
        {
            "4w"  => "FOUR WHEELER (4W)",
            "2w"  => "TWO WHEELER (2W)",
            "3w"  => "THREE WHEELER (3W)",
            "ce"  => "CONSTRUCTION EQUIPMENT (CE)",
            "bus" => "BUS",
            "fe"  => "FARM EQUIPMENT (FE)",
            _     => "COMMERCIAL VEHICLE (CV)",
        };

        private static readonly (string Key, string Title, string Icon)[] ChecklistSections =
        {
            ("mechanical", "MECHANICAL", "cog"),
            ("structural", "STRUCTURAL", "layers"),
            ("electrical", "ELECTRICAL", "zap"),
            ("tyres",      "TYRES",      "disc-3"),
        };

        /// <summary>
        /// Whether a group's own title is worth printing above its rows.
        ///
        /// The template hides it when it merely repeats the section — TYRES inside
        /// TYRES. The registry spells that section TIRES, so a plain string comparison
        /// would print a redundant heading; normalising the spelling catches it while
        /// still showing a genuinely different name like TIRE / TRACK.
        /// </summary>
        private static bool GroupTitleRepeatsSection(string group, string section)
        {
            static string N(string s) => s.Replace(" ", "").Replace("/", "")
                                          .Replace("TIRE", "TYRE").ToUpperInvariant();
            return N(group) == N(section);
        }

        /// <summary>The registry's sections, folded into the four checklist categories.</summary>
        private Dictionary<string, List<SectionDef>> GroupedSections(ValuationDocument doc, out SectionDef[] all)
        {
            var vk = ResolveVehicleTypeKey(doc);
            if (!PdfFieldRegistry.TryGetValue(vk, out all!) || all.Length == 0)
                all = PdfFieldRegistry["cv"];

            var grouped = new Dictionary<string, List<SectionDef>>();
            foreach (var sec in all)
            {
                var cat = CategoryOf(sec.Name);
                if (cat == null) continue;
                if (!grouped.TryGetValue(cat, out var list))
                    grouped[cat] = list = new List<SectionDef>();
                list.Add(sec);
            }
            return grouped;
        }

        /// <summary>The answer as printed, and the pill tone it takes.</summary>
        private (string Text, string Tone) AnswerTone(FieldDef field, string? value)
        {
            if (field.Kind == AnswerKind.Count)
            {
                var count = value?.Trim();
                if (string.IsNullOrEmpty(count)) return ("NA", "na");
                return ScoresAs(field, count) switch
                {
                    "GOOD" => (count, "good"),
                    "NO"   => (count, "poor"),
                    _      => (count, "neutral"),
                };
            }

            var verdict = MapVerdict(value);
            if (verdict == "NA") return ("NA", "na");
            // Colour follows what the answer scores as; the text stays what the AVO chose,
            // so Fluid Leaks still reads NO while showing green.
            return MapVerdict(ScoresAs(field, value)) switch
            {
                "GOOD" or "YES" => (verdict, "good"),
                "AVERAGE"       => (verdict, "avg"),
                "POOR" or "BAD" or "NO" or "DAMAGED" or "MISSING" => (verdict, "poor"),
                _               => (verdict, "neutral"),
            };
        }

        private void ComposeSystemScoresPage(ColumnDescriptor main, ValuationDocument doc)
        {
            var ins = doc.InspectionDetails;
            if (ins == null)
            {
                main.Item().AlignCenter().AlignMiddle()
                    .Text("No inspection details available.")
                    .FontFamily(ReportFont).FontSize(12).FontColor(Label);
                return;
            }

            var grouped = GroupedSections(doc, out var all);
            var (cats, overall) = CoverScores(doc);
            var vk = ResolveVehicleTypeKey(doc);

            // The whole page is one block that FillPage grows: every question row gains
            // the same extra height until OTHER SYSTEMS reaches the footer — a CV left
            // about 25mm of white there. Floor of -0.5mm per row: only a checklist that
            // would not otherwise fit is ever tightened, and only by that much.
            main.Item().Dynamic(new FillPage((page, rowExtra, _) => page.Column(body =>
                ChecklistBody(body, ins, grouped, all, cats, overall, vk, rowExtra)),
                maxMm: 2.0, minMm: -0.5));
        }

        private void ChecklistBody(ColumnDescriptor main, InspectionDetails ins,
                                   Dictionary<string, List<SectionDef>> grouped, SectionDef[] all,
                                   Dictionary<string, double> cats, double overall, string vk,
                                   double rowExtra)
        {
            // ---------- banner
            main.Item().PaddingBottom(Mm(3.4)).Layers(l =>
            {
                l.Layer().Svg(s => RoundRectGradient(s.Width, s.Height, Mm(2.4),
                    new[] { (Navy, 0.0), ("#17405F", 100.0) }));
                l.PrimaryLayer().PaddingVertical(Mm(2.6)).PaddingHorizontal(Mm(4)).Row(r =>
                {
                    r.RelativeItem().AlignMiddle()
                        .Text($"{Theme.Name} VEHICLE INSPECTION CHECKLIST   |   {VehicleTypeLabel(vk)}")
                        // Tracking pulled in from the template's 0.5mm, which assumes
                        // Carlito; at that width in Lato the banner title wraps and the
                        // overall score slides under it.
                        .FontFamily(ReportFont).FontSize(10.6f).Bold().FontColor("#FFFFFF")
                        .LetterSpacing(Ls(0.12, 10.6));
                    r.AutoItem().AlignMiddle()
                        .Text($"OVERALL {overall.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} / 10")
                        .FontFamily(ReportFont).FontSize(7.6f).Bold().FontColor("#FFD2B3")
                        .LetterSpacing(Ls(0.3, 7.6));
                });
            });

            // ---------- two columns: MECHANICAL left, the rest right
            //
            // Both columns end flush. Left alone they do not — MECHANICAL is one tall
            // card against three stacked on the right, and on a CV the right side runs
            // about 25mm longer, leaving a ragged gap above FUNCTIONALITY.
            //
            // The shorter column's last card is grown to close that gap. QuestPDF cannot
            // do this for us: ExtendVertical measures against the remaining PAGE, not the
            // sibling column, so both a Row and a Table cell swallowed everything below
            // and pushed FUNCTIONALITY onto a page of its own.
            var left  = ChecklistSections.Where(x => x.Key == "mechanical" && grouped.ContainsKey(x.Key)).ToList();
            var right = ChecklistSections.Where(x => x.Key != "mechanical" && grouped.ContainsKey(x.Key)).ToList();

            double leftH  = ChecklistColumnHeightMm(left,  grouped, rowExtra);
            double rightH = ChecklistColumnHeightMm(right, grouped, rowExtra);

            void DrawColumn(ColumnDescriptor col,
                            List<(string Key, string Title, string Icon)> cards, double deficitMm)
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    var (key, title, icon) = cards[i];
                    bool last = i == cards.Count - 1;
                    double? score = cats.TryGetValue(key, out var sc) ? sc : null;

                    var item = col.Item();
                    if (!last) item = item.PaddingBottom(Mm(2.8));

                    // The slack is handed to the groups rather than left under them.
                    // Growing the card alone left ~25mm of blank white below the last
                    // group box; spreading it across the boxes fills the card and reads
                    // as breathing room instead of a gap.
                    double extra = last ? Math.Max(0, deficitMm) : 0;
                    item.Element(c => SystemSection(c, title, icon, grouped[key], ins, score, extra, rowExtra));
                }
            }

            main.Item().Row(row =>
            {
                row.RelativeItem().Column(col => DrawColumn(col, left,  Math.Max(0, rightH - leftH)));
                row.ConstantItem(Mm(3.6));
                row.RelativeItem().Column(col => DrawColumn(col, right, Math.Max(0, leftH - rightH)));
            });

            main.Item().Height(Mm(2.8));

            // ---------- the two unscored blocks, full width, two columns of rows
            //
            // Spaced by padding above rather than below, so the last one ends where the
            // page's content does — FillPage adds the gap above the footer itself.
            var wide = new[] { "FUNCTIONALITY", "OTHER SYSTEMS" }
                .Select(name => all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .Where(sec => sec != null)
                .ToList();
            for (int i = 0; i < wide.Count; i++)
            {
                var sec = wide[i]!;
                var icon = sec.Name.Equals("FUNCTIONALITY", StringComparison.OrdinalIgnoreCase) ? "activity" : "boxes";
                main.Item().PaddingTop(i > 0 ? Mm(3.2) : 0)
                    .Element(c => WideSection(c, sec.Name, icon, sec, ins, rowExtra));
            }
        }

        // ──────────────────────────────────────────────
        // Column height model
        // ──────────────────────────────────────────────
        //
        // Solved from a rendered report rather than guessed: the y-positions of the
        // STRUCTURAL, ELECTRICAL, TYRES and FUNCTIONALITY headings give three equations
        // in these three unknowns. Used only to decide how far to grow the shorter
        // column, so a small drift shows as a few points of misalignment, never as
        // clipped content or an extra page.
        private const double ClHeaderMm   = 10.24;   // a section header
        private const double ClTitleMm    = 5.86;    // a group title row
        private const double ClRowMm      = 5.18;    // one question row
        private const double ClGroupPadMm = 1.8;     // padding around each group box
        private const double ClTrailMm    = 0.9;     // the card's trailing space
        private const double ClCardGapMm  = 2.8;     // between stacked cards

        // rowExtra is the per-row growth FillPage is trying; the model has to follow it,
        // or the columns it balances would come apart as the rows grow.
        private static double ChecklistGroupBoxHeightMm(SectionDef g, bool hideTitle, double rowExtra) =>
            (hideTitle ? 0 : ClTitleMm) + g.Fields.Length * (ClRowMm + rowExtra);

        private static double ChecklistCardHeightMm(string sectionTitle, List<SectionDef> groups, double rowExtra)
        {
            double h = ClHeaderMm + ClTrailMm;
            foreach (var g in groups)
                h += ClGroupPadMm
                   + ChecklistGroupBoxHeightMm(g, GroupTitleRepeatsSection(g.Name, sectionTitle), rowExtra);
            return h;
        }

        private static double ChecklistColumnHeightMm(
            List<(string Key, string Title, string Icon)> cards,
            Dictionary<string, List<SectionDef>> grouped, double rowExtra)
        {
            double h = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                if (i > 0) h += ClCardGapMm;
                h += ChecklistCardHeightMm(cards[i].Title, grouped[cards[i].Key], rowExtra);
            }
            return h;
        }

        // ──────────────────────────────────────────────
        // Page 3 parts
        // ──────────────────────────────────────────────

        /// <summary>A rated system card: header with score pill, then one box per group.</summary>
        private void SystemSection(IContainer container, string title, string icon,
                                   List<SectionDef> groups, InspectionDetails ins, double? score,
                                   double extraMm = 0, double rowExtra = 0)
        {
            // Slack shared equally, so no single box balloons.
            double share = groups.Count > 0 ? extraMm / groups.Count : 0;

            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(3), "#FBFCFE", Border));
                l.PrimaryLayer().Column(c =>
                {
                    SectionHeader(c, title, icon, score);

                    foreach (var g in groups)
                    {
                        bool hide = GroupTitleRepeatsSection(g.Name, title);
                        var box = c.Item().PaddingHorizontal(Mm(2.4)).PaddingVertical(Mm(0.9));
                        if (share > 0.1)
                            box = box.MinHeight(Mm(ChecklistGroupBoxHeightMm(g, hide, rowExtra) + share));
                        box.Element(x => GroupBox(x, g, ins, hideTitle: hide, rowExtra));
                    }

                    c.Item().Height(Mm(0.9));
                });
            });
        }

        /// <summary>An unscored block: header, then its fields across two columns.</summary>
        private void WideSection(IContainer container, string title, string icon,
                                 SectionDef sec, InspectionDetails ins, double rowExtra = 0)
        {
            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(3), "#FBFCFE", Border));
                l.PrimaryLayer().Column(c =>
                {
                    SectionHeader(c, title, icon, null);

                    // Across, then down — a two-column CSS grid fills row-wise, and the
                    // registry is ordered on that assumption. Splitting the list in half
                    // instead would pair the wrong questions side by side.
                    var fields = sec.Fields;
                    c.Item().PaddingTop(Mm(1.2)).PaddingBottom(Mm(1.6)).PaddingHorizontal(Mm(2.6)).Row(r =>
                    {
                        void Column(int offset)
                        {
                            r.RelativeItem().Column(col =>
                            {
                                for (int i = offset; i < fields.Length; i += 2)
                                {
                                    var f = fields[i];
                                    bool last = i + 2 >= fields.Length;
                                    col.Item().Element(x => ChecklistRow(x, f, ins, last, rowExtra));
                                }
                            });
                        }
                        Column(0);
                        r.ConstantItem(Mm(5));
                        Column(1);
                    });
                });
            });
        }

        private void SectionHeader(ColumnDescriptor c, string title, string icon, double? score)
        {
            c.Item().Background("#F1F5F9").BorderBottom(1).BorderColor(Border)
             .PaddingVertical(Mm(1.7)).PaddingHorizontal(Mm(3.2)).Row(r =>
            {
                r.AutoItem().AlignMiddle().Element(x => DrawChip(x, icon, 7, 3.9));
                r.ConstantItem(Mm(2.4));
                r.RelativeItem().AlignMiddle().Text(title)
                    .FontFamily(ReportFont).FontSize(11).Bold().FontColor(Navy)
                    .LetterSpacing(Ls(0.4, 11));
                if (score.HasValue)
                    r.AutoItem().AlignMiddle().Layers(p =>
                    {
                        p.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(10), Navy));
                        p.PrimaryLayer().PaddingVertical(Mm(0.9)).PaddingHorizontal(Mm(2.8))
                         .Text($"SCORE: {score.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}/10")
                         .FontFamily(ReportFont).FontSize(7.4f).Bold().FontColor("#FFFFFF")
                         .LetterSpacing(Ls(0.2, 7.4));
                    });
            });
        }

        /// <summary>One group of checklist rows, in its own bordered box.</summary>
        private void GroupBox(IContainer container, SectionDef sec, InspectionDetails ins, bool hideTitle,
                              double rowExtra = 0)
        {
            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.2), "#FFFFFF", "#E7ECF2"));
                l.PrimaryLayer().Column(c =>
                {
                    if (!hideTitle)
                        c.Item().BorderBottom(1).BorderColor(BorderIn)
                         .PaddingTop(Mm(1.1)).PaddingBottom(Mm(0.9)).PaddingHorizontal(Mm(3)).Row(r =>
                        {
                            r.AutoItem().AlignMiddle().Width(Mm(1.6)).Height(Mm(1.6))
                                .Svg(s => $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(s.Width)}\" height=\"{F(s.Height)}\">" +
                                          $"<circle cx=\"{F(s.Width / 2)}\" cy=\"{F(s.Height / 2)}\" r=\"{F(s.Width / 2)}\" fill=\"{Orange}\"/></svg>");
                            r.ConstantItem(Mm(1.8));
                            r.RelativeItem().AlignMiddle().Text(sec.Name)
                                .FontFamily(ReportFont).FontSize(7.8f).Bold().FontColor(Teal)
                                .LetterSpacing(Ls(0.3, 7.8));
                        });

                    for (int i = 0; i < sec.Fields.Length; i++)
                    {
                        var f = sec.Fields[i];
                        c.Item().Element(x => ChecklistRow(x, f, ins, last: i == sec.Fields.Length - 1, rowExtra));
                    }
                });
            });
        }

        /// <summary>A checklist row: the question on the left, the answer pill on the right.</summary>
        private void ChecklistRow(IContainer container, FieldDef field, InspectionDetails ins, bool last,
                                  double rowExtra = 0)
        {
            var (text, tone) = AnswerTone(field, GetInsValue(ins, field.Key));
            // rowExtra is shared above and below the text, so the row grows around it
            // rather than pushing the question towards its top edge.
            container.BorderBottom(last ? 0 : 1).BorderColor("#F3F6F9")
                     .PaddingVertical(Mm(Math.Max(0.1, 0.6 + rowExtra / 2))).PaddingHorizontal(Mm(3)).Row(r =>
            {
                r.RelativeItem().AlignMiddle().Text(field.Label)
                    .FontFamily(ReportFont).FontSize(8.2f).FontColor(InkSoft)
                    .LetterSpacing(Ls(0.1, 8.2));
                r.ConstantItem(Mm(2));
                r.AutoItem().MinWidth(Mm(12)).AlignMiddle().Element(x =>
                    CentredPill(x, text, tone));
            });
        }

        /// <summary>An answer pill of fixed width, so a column of them lines up.</summary>
        private void CentredPill(IContainer container, string text, string tone)
        {
            var (bg, fg) = PillColors(tone);
            container.Layers(l =>
            {
                l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(10), bg));
                l.PrimaryLayer().PaddingVertical(Mm(0.55)).PaddingHorizontal(Mm(2.6))
                 .AlignCenter().Text(text)
                 .FontFamily(ReportFont).FontSize(7).Bold().FontColor(fg)
                 .LetterSpacing(Ls(0.25, 7));
            });
        }
    }
}
