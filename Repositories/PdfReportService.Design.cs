using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using QuestPDF.Elements;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Valuation.Api.Services
{
    /// <summary>
    /// The report's design system, ported from the approved WeasyPrint template
    /// (build.py + its CSS). That template is the specification: every number in
    /// here is the millimetre or point value it declares, converted once by
    /// <see cref="Mm"/> rather than re-derived by eye from a rendered page.
    ///
    /// Kept apart from the composition code so the tokens stay readable as a set,
    /// and so a future design revision has one obvious place to land.
    /// </summary>
    public partial class PdfReportService
    {
        // ──────────────────────────────────────────────
        // Units
        // ──────────────────────────────────────────────

        /// <summary>Millimetres to PDF points. The template lays everything out in
        /// mm; QuestPDF works in points, so every dimension goes through here.</summary>
        private static float Mm(double mm) => (float)(mm * 2.8346456692913385);

        /// <summary>
        /// The template asks for Carlito, falling back to Lato then Calibri.
        ///
        /// Carlito is not installed here and is not redistributable from this repo
        /// without adding the font binary, so we render the designer's own declared
        /// fallback. Lato ships with QuestPDF, so it is guaranteed present on the
        /// Linux host as well as here. Carlito is metrically Calibri, which is
        /// narrower than Lato, so text-dense rows sit a little wider than the
        /// sample PDF — swap this one constant if the Carlito TTF is ever added.
        /// </summary>
        private const string ReportFont = "Lato";

        // ──────────────────────────────────────────────
        // Palette
        // ──────────────────────────────────────────────

        private const string Teal      = "#037076";   // brand, darkened from the old #009688
        private const string TealLight = "#0A8F94";   // section headings
        private const string Navy      = "#0F2A43";   // headings, values, dark pills
        private const string Orange    = "#F26B1D";   // the accent introduced by this design
        private const string AccentL   = "#FFF0E5";   // icon-chip tile

        private const string Tint      = "#EAF6F5";   // score card wash
        private const string TintB     = "#CDE7E5";

        // Market-value card gradient, 135deg over three stops.
        private const string BigA = "#025156", BigB = "#037076", BigC = "#0A8F92";
        private const string BigL = "#BFE5E3";       // its label / note ink

        private const string Green = "#15803D", Amber = "#B45309", Red = "#B91C1C";
        private const string RingGreen = "#16A34A", RingAmber = "#D97706", RingRed = "#DC2626";

        private const string Ink       = "#1E293B";  // body
        private const string InkSoft   = "#334155";  // remarks, checklist rows
        private const string Label     = "#64748B";  // field labels
        private const string LabelFaint= "#94A3B8";  // footer

        private const string Line      = "#E5EAF0";  // header/footer rules, section rule
        private const string Border    = "#E2E8F0";  // card borders
        private const string BorderIn  = "#EDF1F5";  // inner dividers
        private const string BorderImg = "#D9E1EA";  // photo borders
        private const string RingTrack = "#E6ECF2";  // gauge track

        /// <summary>Page geometry: A4 with a 12mm side gutter, footer 6mm off the foot.</summary>
        private static float PageGutter => Mm(12);

        /// <summary>A half-width column: the 186mm content width less a 4mm gap, halved.</summary>
        private const double HalfColumnMm = 91;

        // ──────────────────────────────────────────────
        // Filling the page
        // ──────────────────────────────────────────────

        /// <summary>
        /// Grows a block until it reaches the foot of the page, so pages do not end in
        /// a band of white above the footer.
        ///
        /// The block is drawn at a "stretch" — extra millimetres of photo height, row
        /// padding, whatever that page grows by — and QuestPDF measures it, rather than a
        /// model predicting it, so wrapping names and long addresses are accounted for. A
        /// binary search keeps the largest stretch that fits, up to the block's cap.
        ///
        /// Two QuestPDF rules shape this, both found by experiment:
        ///  • Dynamic content that does not fit where it lands THROWS; it is not moved
        ///    on the way a plain element is. So a block too tall for the space left
        ///    defers itself — empty content, HasMoreContent — and QuestPDF calls it again
        ///    at the top of the next page. That reproduces the old spill, not a crash.
        ///  • The draw pass calls Compose again, offering exactly the height the layout
        ///    pass's content measured — not the space that was left. Searching again
        ///    from that offer, less the headroom, is not the same question: a block laid
        ///    out with under half a point to spare could not fit its own minimum, deferred
        ///    during drawing, and vanished from the report without an error. So the
        ///    layout decision is kept and replayed when its own height is offered back.
        /// </summary>
        /// <summary>Offered at least this, a block is already at the top of a page: the
        /// page body is 751pt tall on A4 under the report's header, either brand.</summary>
        private const float FreshPagePt = 700f;

        /// <summary>
        /// Runs a layout decision once, at the top of a page, and takes no space. For
        /// choices later blocks depend on — how the photo pages split — which need the
        /// real page height to measure against but must be settled before the first of
        /// those blocks is drawn.
        /// </summary>
        private sealed class DecideOnFreshPage : IDynamicComponent
        {
            private readonly Action<DynamicContext> _decide;
            private bool _done;

            public DecideOnFreshPage(Action<DynamicContext> decide) => _decide = decide;

            public DynamicComponentComposeResult Compose(DynamicContext context)
            {
                // Later calls — the draw pass offers this block its own height, 0 —
                // keep the first decision.
                if (!_done && context.AvailableSize.Height >= FreshPagePt)
                {
                    _decide(context);
                    _done = true;
                }
                return new DynamicComponentComposeResult
                {
                    Content = context.CreateElement(_ => { }),
                    HasMoreContent = false,
                };
            }
        }

        private sealed class FillPage : IDynamicComponent
        {
            private readonly Action<IContainer, double, bool> _compose;
            private readonly Func<double> _minOf;
            private readonly double _max;

            /// <summary>Starts the block on a page of its own: offered anything less than a
            /// fresh page, it defers to the next one. Stands in for a PageBreak where
            /// whether the block exists at all is only known once the pages are laid out.</summary>
            public bool OwnPage { get; init; }

            /// <summary>Asked at layout time; false and the block takes no space at all.</summary>
            public Func<bool>? Present { get; init; }

            /// <param name="compose">Draws the block at a stretch in mm. The flag is true
            /// while measuring, so images can be stood in for by same-sized placeholders
            /// instead of being decoded and cropped at every step of the search.</param>
            public FillPage(Action<IContainer, double, bool> compose, double maxMm, double minMm = 0)
                : this(compose, maxMm, () => minMm) { }

            /// <param name="minMm">Read at layout time, not when the block is built: for a
            /// floor that depends on a decision taken while earlier pages were laid out.</param>
            public FillPage(Action<IContainer, double, bool> compose, double maxMm, Func<double> minMm)
            {
                _compose = compose;
                _max = maxMm;
                _minOf = minMm;
            }

            /// <summary>
            /// Headroom left below the offer. QuestPDF re-measures the returned content
            /// against the offer and insists on a clean full fit, and a long column of
            /// rows collects enough floating-point error that content measured at exactly
            /// the offered height came back as not fitting — the Pronto checklist did,
            /// at 751.57pt of 751.57. Half a point, 0.2mm, is not visible on paper.
            /// </summary>
            private const float HeadroomPt = 0.5f;

            /// <summary>
            /// White left between the grown block and the footer, so a page reads as
            /// finished rather than crammed against it. It is padding inside the block,
            /// not a reserve below it, so the draw pass measures what layout measured.
            /// </summary>
            private const double BottomGapMm = 3;

            /// <summary>The last block handed back — its stretch, gap and measured height —
            /// for the draw pass to replay.</summary>
            private (double Stretch, double Gap, float Height)? _chosen;

            public DynamicComponentComposeResult Compose(DynamicContext context)
            {
                // Pinned to the real width. CreateElement measures with no width limit —
                // it reported 14,400pt — so nothing wrapped in the trial, and a line that
                // wraps on the actual 527pt page came out one line taller than measured.
                // The Pronto checklist banner threw on exactly that; a long QC remark on
                // the cover would have too.
                float width = context.AvailableSize.Width;
                float offered = context.AvailableSize.Height;

                DynamicComponentComposeResult Block(double stretch, double gapMm)
                {
                    var content = context.CreateElement(c =>
                        _compose(c.Width(width).PaddingBottom(Mm(gapMm)), stretch, false));
                    _chosen = (stretch, gapMm, content.Size.Height);
                    return new DynamicComponentComposeResult { Content = content, HasMoreContent = false };
                }

                if (Present?.Invoke() == false)
                    return new DynamicComponentComposeResult
                    {
                        Content = context.CreateElement(_ => { }),
                        HasMoreContent = false,
                    };

                if (_chosen is { } chosen && Math.Abs(offered - chosen.Height) < 0.01f)
                    return Block(chosen.Stretch, chosen.Gap);

                if (OwnPage && offered < FreshPagePt)
                    return new DynamicComponentComposeResult
                    {
                        Content = context.CreateElement(_ => { }),
                        HasMoreContent = true,
                    };

                double _min = _minOf();
                float target = offered - HeadroomPt;
                double gap = BottomGapMm;
                float Height(double s) =>
                    context.CreateElement(c => _compose(c.Width(width).PaddingBottom(Mm(gap)), s, true))
                        .Size.Height;
                bool Fits(double s) => Height(s) <= target;

                // The gap is the first thing given up: a page too full for all of it keeps
                // what room there is — it is plain padding, so that is a subtraction — and
                // its content stays where it is rather than moving to the next page.
                if (!Fits(_min))
                {
                    gap = 0;
                    double roomMm = (target - Height(_min)) / Mm(1) - 0.05;
                    if (roomMm > 0) gap = Math.Min(BottomGapMm, roomMm);
                }

                if (!Fits(_min) && offered < FreshPagePt)
                    return new DynamicComponentComposeResult
                    {
                        Content = context.CreateElement(_ => { }),
                        HasMoreContent = true,
                    };

                double best = _min;
                if (Fits(_max)) best = _max;
                else
                {
                    double lo = _min, hi = _max;
                    for (int i = 0; i < 14; i++)
                    {
                        double mid = (lo + hi) / 2;
                        if (Fits(mid)) lo = mid; else hi = mid;
                    }
                    best = lo;
                }

                return Block(best, gap);
            }
        }

        // ──────────────────────────────────────────────
        // Score bands
        // ──────────────────────────────────────────────

        /// <summary>
        /// Ring colour, ink, word and pill tone for a score. Drives the cover's
        /// "Good condition" verdict and every gauge ring.
        ///
        /// Deliberately NOT the template's thresholds. build.py bands at 8 and 6;
        /// the portal's scoreBand() in inspection-score.ts bands at 7 and 4, and the
        /// QC screen has always coloured scores that way. Keeping the template's
        /// numbers would have printed "Average condition" in amber on a 7.5 that the
        /// team sees as green on screen, and "Poor" in red on a 5.0 they see as
        /// average — the same contradiction the overall score had. The report follows
        /// the portal; if these ever move, move scoreBand() with them.
        /// </summary>
        private static (string Ring, string Ink, string Word, string Tone) Band(double score) =>
            score >= 7 ? (RingGreen, Green, "GOOD", "good")
          : score >= 4 ? (RingAmber, Amber, "AVERAGE", "avg")
          :              (RingRed,   Red,   "POOR", "poor");

        /// <summary>Background and foreground for a pill tone.</summary>
        private static (string Bg, string Fg) PillColors(string tone) => tone switch
        {
            "good"   => ("#E3F5E9", Green),
            "avg"    => ("#FEF0CC", Amber),
            "poor"   => ("#FDE2E2", Red),
            "na"     => ("#F1F5F9", LabelFaint),
            "brand"  => (Teal,      "#FFFFFF"),
            "orange" => (Orange,    "#FFFFFF"),
            _        => ("#EAF0F6", "#475569"),   // neutral
        };

        /// <summary>
        /// Fill, border, icon-ring, icon and text colour for the VALUATION &amp; CHECKS
        /// cards and pills. Sampled from the 2026-09-21 mockup for the good and link
        /// states; the others follow the report's amber / red / neutral scales.
        /// </summary>
        private static (string Bg, string Border, string Ring, string Glyph, string Ink) CheckColors(string tone) => tone switch
        {
            "good" => ("#F3F9F5", "#BCDEC8", "#BCDEC8", "#1E693E", "#14603A"),
            "avg"  => ("#FFFBF1", "#F1D08A", "#F1D08A", "#B45309", "#92400E"),
            "poor" => ("#FEF2F2", "#F5B5B5", "#F5B5B5", "#B91C1C", "#991B1B"),
            "link" => ("#EAF0FE", "#C5D5FA", "#C5D5FA", "#2A3F77", "#243E71"),
            _      => ("#F8FAFC", "#E2E8F0", "#E2E8F0", "#64748B", "#475569"),   // neutral
        };

        /// <summary>The small caption above a check card's verdict.</summary>
        private const string CheckLabelInk = "#5B6470";

        // ──────────────────────────────────────────────
        // Icons
        // ──────────────────────────────────────────────

        private static readonly Dictionary<string, string> IconCache = new();

        /// <summary>
        /// A Lucide icon, recoloured and stroked to order. The same SVG files the
        /// template inlined, shipped in icons/ beside png/.
        ///
        /// Falls back to an empty SVG rather than throwing: a missing icon should
        /// cost the report a glyph, not the whole render.
        /// </summary>
        private static string Icon(string name, string? color = null, double strokeWidth = 2.0)
        {
            color ??= Orange;
            string raw;
            lock (IconCache)
            {
                if (!IconCache.TryGetValue(name, out raw!))
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "icons", $"{name}.svg");
                    raw = File.Exists(path) ? File.ReadAllText(path) : "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"></svg>";
                    IconCache[name] = raw;
                }
            }

            var svg = raw.Replace("currentColor", color);
            svg = Regex.Replace(svg, "stroke-width=\"[^\"]*\"",
                $"stroke-width=\"{strokeWidth.ToString("0.##", CultureInfo.InvariantCulture)}\"");
            return svg;
        }

        /// <summary>Draws an icon at a given size, in millimetres.</summary>
        private static void DrawIcon(IContainer container, string name, string? color = null,
                                     double sizeMm = 4.2, double strokeWidth = 2.0) =>
            container.Width(Mm(sizeMm)).Height(Mm(sizeMm)).Svg(Icon(name, color, strokeWidth));

        /// <summary>
        /// The orange-on-soft-orange rounded tile that carries an icon. Used for
        /// every field row, the client strip and the section headings.
        /// </summary>
        private static void DrawChip(IContainer container, string name, double sizeMm = 7.0,
                                     double innerMm = 3.9, string? color = null, string? bg = null)
        {
            container.Width(Mm(sizeMm)).Height(Mm(sizeMm)).Layers(layers =>
            {
                layers.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2), bg ?? AccentL));
                layers.PrimaryLayer().AlignCenter().AlignMiddle()
                      .Element(c => DrawIcon(c, name, color ?? Orange, innerMm));
            });
        }

        // ──────────────────────────────────────────────
        // Primitives
        // ──────────────────────────────────────────────

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Letter spacing, converted. The template states it in millimetres, while
        /// QuestPDF takes a multiple of the font size — passing the raw number
        /// spaces a 7pt label as if it were tracked for a headline.
        /// </summary>
        private static float Ls(double mm, double fontPt) => Mm(mm) / (float)fontPt;

        /// <summary>
        /// A corner radius that stays circular.
        ///
        /// CSS clamps an oversized border-radius against BOTH axes, so `border-radius:
        /// 10mm` on a 30mm x 7mm pill draws 3.5mm circular ends — a stadium. SVG clamps
        /// rx and ry independently, so the same numbers give a 10mm x 3.5mm ELLIPTICAL
        /// corner, which is why the RETAIL pill came out as a lozenge rather than the
        /// rounded rectangle the design shows.
        /// </summary>
        private static float ClampRadius(float radius, float w, float h) =>
            Math.Max(0, Math.Min(radius, Math.Min(w, h) / 2f));

        /// <summary>A filled, optionally stroked rounded rectangle sized to its container.</summary>
        private static string RoundRect(float w, float h, float radius, string fill,
                                        string? stroke = null, float strokeWidth = 1f)
        {
            // Inset by half the stroke so the border sits inside the box, the way
            // a CSS border does -- an SVG stroke otherwise straddles the edge and
            // reads half a line thick against the neighbouring cell.
            radius = ClampRadius(radius, w, h);
            float inset = stroke != null ? strokeWidth / 2f : 0f;
            string rect =
                $"<rect x=\"{F(inset)}\" y=\"{F(inset)}\" width=\"{F(Math.Max(0, w - strokeWidth * (stroke != null ? 1 : 0)))}\" " +
                $"height=\"{F(Math.Max(0, h - strokeWidth * (stroke != null ? 1 : 0)))}\" rx=\"{F(radius)}\" fill=\"{fill}\"" +
                (stroke != null ? $" stroke=\"{stroke}\" stroke-width=\"{F(strokeWidth)}\"" : "") + "/>";
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\">{rect}</svg>";
        }

        /// <summary>A rounded rectangle filled with a 135-degree linear gradient.</summary>
        private static string RoundRectGradient(float w, float h, float radius,
                                                (string Color, double Offset)[] stops,
                                                string? stroke = null, float strokeWidth = 1f)
        {
            radius = ClampRadius(radius, w, h);
            var defs = string.Join("", Array.ConvertAll(stops,
                s => $"<stop offset=\"{F(s.Offset)}%\" stop-color=\"{s.Color}\"/>"));
            float inset = stroke != null ? strokeWidth / 2f : 0f;
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\">" +
                   $"<defs><linearGradient id=\"g\" x1=\"0%\" y1=\"0%\" x2=\"100%\" y2=\"100%\">{defs}</linearGradient></defs>" +
                   $"<rect x=\"{F(inset)}\" y=\"{F(inset)}\" width=\"{F(Math.Max(0, w - (stroke != null ? strokeWidth : 0)))}\" " +
                   $"height=\"{F(Math.Max(0, h - (stroke != null ? strokeWidth : 0)))}\" rx=\"{F(radius)}\" fill=\"url(#g)\"" +
                   (stroke != null ? $" stroke=\"{stroke}\" stroke-width=\"{F(strokeWidth)}\"" : "") + "/></svg>";
        }

        /// <summary>A filled circle sized to its container, optionally ringed.</summary>
        private static string CircleSvg(float w, float h, string fill,
                                        string? stroke = null, float strokeWidth = 1f)
        {
            float r = Math.Max(0, Math.Min(w, h) / 2f - (stroke != null ? strokeWidth / 2f : 0f));
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\">" +
                   $"<circle cx=\"{F(w / 2)}\" cy=\"{F(h / 2)}\" r=\"{F(r)}\" fill=\"{fill}\"" +
                   (stroke != null ? $" stroke=\"{stroke}\" stroke-width=\"{F(strokeWidth)}\"" : "") + "/></svg>";
        }

        /// <summary>
        /// The progress ring. Drawn as SVG arcs, but the number over it is laid in
        /// as QuestPDF text: SVG text picks up a host-dependent typeface, which is
        /// what made the old gauge render differently on the server than here.
        /// </summary>
        private static string RingSvg(double score, double strokeWidth, string color, string track)
        {
            const double R = 42, Cx = 50, Cy = 50;
            double circ = 2 * Math.PI * R;
            double dash = circ * Math.Clamp(score, 0, 10) / 10.0;
            return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\">" +
                   $"<circle cx=\"{F(Cx)}\" cy=\"{F(Cy)}\" r=\"{F(R)}\" fill=\"none\" stroke=\"{track}\" stroke-width=\"{F(strokeWidth)}\"/>" +
                   $"<circle cx=\"{F(Cx)}\" cy=\"{F(Cy)}\" r=\"{F(R)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{F(strokeWidth)}\" " +
                   $"stroke-linecap=\"round\" stroke-dasharray=\"{F(dash)} {F(circ)}\" transform=\"rotate(-90 {F(Cx)} {F(Cy)})\"/></svg>";
        }

        /// <summary>
        /// A score ring with its figure centred. <paramref name="sub"/> adds the
        /// "/ 10" beneath, which only the large cover gauges carry.
        /// </summary>
        private void DrawRing(IContainer container, double score, double diameterMm,
                              double strokeWidth = 9, double numberPt = 12, bool sub = false,
                              string? color = null, string? track = null,
                              string? numberColor = null, string? subColor = null)
        {
            color ??= Band(score).Ring;
            track ??= RingTrack;
            numberColor ??= Navy;
            subColor ??= Label;

            container.Width(Mm(diameterMm)).Height(Mm(diameterMm)).Layers(layers =>
            {
                layers.Layer().Svg(RingSvg(score, strokeWidth, color, track));
                layers.PrimaryLayer().AlignCenter().AlignMiddle().Column(c =>
                {
                    c.Item().AlignCenter().Text(score.ToString("F1", CultureInfo.InvariantCulture))
                        .FontFamily(ReportFont).FontSize((float)numberPt).Bold().FontColor(numberColor)
                        .LineHeight(1f);
                    if (sub)
                        c.Item().AlignCenter().PaddingTop(Mm(-0.6)).Text("/ 10")
                            .FontFamily(ReportFont).FontSize(6.5f).Bold().FontColor(subColor);
                });
            });
        }

        /// <summary>A rounded status pill. Sizes follow the template's .pill rule.</summary>
        private void DrawPill(IContainer container, string text, string tone = "neutral",
                              double fontPt = 7.6, double padX = 3, double padY = 0.9,
                              double letterSpacing = 0.25, string? iconName = null,
                              string? iconColor = null, double iconMm = 3.4)
        {
            var (bg, fg) = PillColors(tone);
            container.Layers(layers =>
            {
                layers.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(10), bg));
                layers.PrimaryLayer().PaddingVertical(Mm(padY)).PaddingHorizontal(Mm(padX)).Row(r =>
                {
                    if (iconName != null)
                    {
                        r.AutoItem().AlignMiddle().Element(c => DrawIcon(c, iconName, iconColor ?? fg, iconMm));
                        r.ConstantItem(Mm(1.6));
                    }
                    r.AutoItem().AlignMiddle().Text(text)
                        .FontFamily(ReportFont).FontSize((float)fontPt).Bold().FontColor(fg)
                        .LetterSpacing(Ls(letterSpacing, fontPt));
                });
            });
        }

        /// <summary>
        /// A section heading: icon chip, teal title, then a rule running to the
        /// right margin. <paramref name="tag"/> is the small teal note some
        /// headings carry (the photo pages' "1 / 3").
        /// </summary>
        private void DrawSectionHeading(IContainer container, string iconName, string title,
                                        string? tag = null, double fontPt = 13.5, double chipMm = 8)
        {
            container.Row(row =>
            {
                row.AutoItem().AlignMiddle().Element(c => DrawChip(c, iconName, chipMm, chipMm * 0.575));
                row.ConstantItem(Mm(2.6));
                row.AutoItem().AlignMiddle().Text(title.ToUpperInvariant())
                    .FontFamily(ReportFont).FontSize((float)fontPt).Bold().FontColor(TealLight)
                    .LetterSpacing(Ls(0.35, fontPt));
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    row.ConstantItem(Mm(3));
                    row.AutoItem().AlignMiddle().Text(tag)
                        .FontFamily(ReportFont).FontSize(7.5f).Bold().FontColor(Teal)
                        .LetterSpacing(Ls(0.3, 7.5));
                }
                row.ConstantItem(Mm(3.5));
                row.RelativeItem().AlignMiddle().Height(Mm(0.3)).Background(Line);
            });
        }

        /// <summary>A label over its value, the report's standard field rendering.</summary>
        private void DrawField(IContainer container, string label, string? value,
                               double labelPt = 6.6, double valuePt = 9.6)
        {
            container.Column(c =>
            {
                c.Item().Text(label.ToUpperInvariant())
                    .FontFamily(ReportFont).FontSize((float)labelPt).Bold().FontColor(Label)
                    .LetterSpacing(Ls(0.25, labelPt));
                c.Item().Text(string.IsNullOrWhiteSpace(value) ? "-" : value)
                    .FontFamily(ReportFont).FontSize((float)valuePt).Bold().FontColor(Navy)
                    .LineHeight(1.15f);
            });
        }
    }
}
