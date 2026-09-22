using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using SkiaSharp;
using Valuation.Api.Models;

namespace Valuation.Api.Services
{
    /// <summary>
    /// Pages 4 onward — the photographic evidence, then the chassis identification
    /// shots, the tyres and the disclaimer on the last page.
    /// </summary>
    public partial class PdfReportService
    {
        /// <summary>Six to a page, as the template lays them out.</summary>
        private const int PhotosPerPage = 6;

        private static readonly string[] TyreSlotKeys =
            { "TireFrontLeft", "TireFrontRight", "TireRearLeft", "TireRearRight" };

        /// <summary>The two identification shots that close the report, beside the tyres.</summary>
        private static readonly (string Label, string[] Keys)[] ChassisIdSlots =
        {
            ("CHASSIS NUMBER", new[] { "ChassisNumberPlate", "ChassisNumber", "Chassis", "ChassisImprint" }),
            ("VIN PLATE",      new[] { "VinPlate", "VIN" }),
        };

        private void ComposePhotoGalleryAndDisclaimer(ColumnDescriptor main, ValuationDocument doc,
                                                      Dictionary<string, byte[]> photos)
        {
            // QC's chosen subset. Null or empty (never set, or everything unticked) keeps
            // the original behaviour of including whatever was uploaded.
            var selected = doc.SelectedGalleryPhotos is { Count: > 0 }
                ? new HashSet<string>(doc.SelectedGalleryPhotos, StringComparer.OrdinalIgnoreCase)
                : null;

            byte[]? Resolve(string[] keys)
            {
                foreach (var k in keys)
                    if (photos.TryGetValue(k, out var b) && (selected == null || selected.Contains(k)))
                        return b;
                return null;
            }

            // The named walk-around slots, minus the two identification shots, which the
            // template holds back for the last page.
            var chassisIdKeys = new HashSet<string>(ChassisIdSlots.SelectMany(s => s.Keys), StringComparer.OrdinalIgnoreCase);
            var walkAround = GalleryPhotoSlots
                .Where(s => !s.Keys.Any(chassisIdKeys.Contains))
                .Select(s => (s.Label, Image: Resolve(s.Keys)))
                .Where(s => s.Image != null)
                .ToList();

            // Photos outside the named slots: Underbody, seats, working/operation shots,
            // custom uploads. Included unless QC has explicitly deselected them.
            //
            // This used to require a stored selection (`selected != null`) before it
            // would run at all. The QC page saves an EMPTY list when every tile is
            // ticked — its way of saying "standard, and keep including whatever is
            // uploaded later" — which the report read as "no selection" and skipped.
            // The effect was that unnamed photos never reached any report: a survey of
            // the live container found 0 of 88 cases with an explicit selection, so
            // this branch had never once executed, while 22.7% of cases were carrying
            // photos it would have printed.
            //
            // The reserved set is case-insensitive, or a differently-cased tyre or
            // chassis key would escape the exclusion and print twice: once in the
            // walk-around grid and again in its own row on the closing page.
            var reserved = new HashSet<string>(GalleryPhotoSlots.SelectMany(s => s.Keys)
                .Concat(TyreSlotKeys)
                .Concat(new[] { "ChassisVerification", "ChassisStencilTrace" }),
                StringComparer.OrdinalIgnoreCase);
            walkAround.AddRange(photos.Keys
                .Where(k => !reserved.Contains(k))
                .Where(k => selected == null || selected.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => (Label: HumanizePhotoKey(k), Image: (byte[]?)photos[k])));

            var tyres = TyreSlotKeys
                .Where(k => photos.ContainsKey(k) && (selected == null || selected.Contains(k)))
                .Select(k => photos[k]).ToList();

            var chassisId = ChassisIdSlots
                .Select(s => (s.Label, Image: Resolve(s.Keys)))
                .Where(s => s.Image != null)
                .ToList();

            // Worked out once, outside the fills: it decodes every tyre photo, and a fill
            // would otherwise repeat that at each step of its search.
            double tyreH = tyres.Count > 0 ? TyrePanelHeightMm(tyres, 43.5) : 0;

            // The closing material, in the order it reads.
            var closing = new List<ClosingPart>();
            if (chassisId.Count > 0) closing.Add(ClosingPart.Identification);
            if (tyres.Count > 0) closing.Add(ClosingPart.Tyres);
            closing.Add(ClosingPart.Disclaimer);

            int galleryPages = (walkAround.Count + PhotosPerPage - 1) / PhotosPerPage;
            var lastSlice = walkAround.Skip(Math.Max(0, galleryPages - 1) * PhotosPerPage).ToList();

            // How many closing parts ride up onto the last gallery page, from the front.
            // A last page holding one row of photos left ~156mm of white above the
            // footer, with the identification shots and tyres alone on the page after.
            // Now they fill it: under one row, the identification shots, the tyres and
            // (room permitting) the disclaimer come up; under two rows, the
            // identification shots make the third row and the tyres start the next page.
            int lifted = 0;

            List<ClosingPart> Rest() => closing.Skip(lifted).ToList();
            // A disclaimer left over on its own does not open with the photo heading or
            // count as a photo page. With no gallery at all the heading stays, as before.
            bool ClosingHeading() => Rest().Any(p => p != ClosingPart.Disclaimer) || galleryPages == 0;
            int PhotoPages() => galleryPages + (Rest().Count > 0 && ClosingHeading() ? 1 : 0);

            // Photos are a fixed 91 x 66mm — 4:3 near enough, the approved size. They used
            // to grow to fill the page (to 74.5mm, 1.22:1), which trimmed the sides off
            // every shot, and shrink to 56mm to make room for the disclaimer; a 16:9 GPS
            // camera shot lost its watermark's edges both ways. A page that does not fill
            // ends in white above the footer instead.
            const double GalleryPanelMm = 66;

            void GalleryPage(IContainer container, List<(string Label, byte[]? Image)> slice,
                             int lift, bool measuring, string tag)
            {
                container.Column(col =>
                {
                    DrawSectionHeading(col.Item().PaddingBottom(Mm(3)), "camera", "Photographic Evidence", tag);
                    PhotoGrid(col, slice, GalleryPanelMm, measuring);
                    ClosingParts(col, closing.Take(lift).ToList(), afterPhotos: true,
                                 chassisId, tyres, tyreH, GalleryPanelMm, measuring);
                });
            }

            if (galleryPages > 0)
            {
                // Decided at the top of the first gallery page: a full page, the height
                // the last one will be offered too, and ahead of any "n / N" being drawn.
                main.Item().Dynamic(new DecideOnFreshPage(ctx =>
                {
                    float width = ctx.AvailableSize.Width;
                    float room = ctx.AvailableSize.Height - 0.5f;
                    bool Fits(int lift) => ctx.CreateElement(c =>
                            GalleryPage(c.Width(width), lastSlice, lift, true, "0 / 0"))
                        .Size.Height <= room;
                    while (lifted < closing.Count && Fits(lifted + 1)) lifted++;

                    // The tyres came up but the disclaimer could not — portrait tyre shots
                    // make a 58mm row — so it would sit alone on an otherwise empty last
                    // page. The tyres stay down with it instead.
                    if (lifted == closing.Count - 1 && lifted > 0 && closing[lifted - 1] == ClosingPart.Tyres)
                        lifted--;
                }));
            }

            for (int pageNo = 1; pageNo <= galleryPages; pageNo++)
            {
                if (pageNo > 1) main.Item().PageBreak();
                int thisPage = pageNo;
                bool last = pageNo == galleryPages;
                var slice = walkAround.Skip((pageNo - 1) * PhotosPerPage).Take(PhotosPerPage).ToList();

                // Nothing stretches (maxMm 0): the fill is kept for its replay of the
                // layout decision, and so the lifted closing parts are composed lazily.
                main.Item().Dynamic(new FillPage((page, _, measuring) =>
                        GalleryPage(page, slice, last ? lifted : 0, measuring, $"{thisPage} / {PhotoPages()}"),
                    maxMm: 0));
            }

            // ---------- closing page: whatever did not fit under the last photos
            //
            // The identification shots keep their approved 91 x 68mm (4:3). They grew to
            // fill this page too, up to 90mm, cropping the sides off a chassis number.
            main.Item().Dynamic(new FillPage((page, _, measuring) => page.Column(col =>
                {
                    if (ClosingHeading())
                        DrawSectionHeading(col.Item().PaddingBottom(Mm(3)), "camera",
                                           "Photographic Evidence", $"{PhotoPages()} / {PhotoPages()}");
                    ClosingParts(col, Rest(), afterPhotos: false, chassisId, tyres, tyreH, 68, measuring);
                }), maxMm: 0)
            {
                OwnPage = true,
                Present = () => Rest().Count > 0,
            });
        }

        /// <summary>Photos two to a row, each over its caption.</summary>
        private void PhotoGrid(ColumnDescriptor col, List<(string Label, byte[]? Image)> photos,
                               double panelMm, bool measuring)
        {
            for (int r = 0; r < photos.Count; r += 2)
            {
                if (r > 0) col.Item().Height(Mm(3.6));
                col.Item().Row(row =>
                {
                    for (int c = 0; c < 2; c++)
                    {
                        if (c > 0) row.ConstantItem(Mm(4));
                        int idx = r + c;
                        if (idx < photos.Count)
                        {
                            var item = photos[idx];
                            row.RelativeItem().Element(x =>
                                LabelledPhoto(x, item.Image!, item.Label, HalfColumnMm, panelMm, measuring: measuring));
                        }
                        else row.RelativeItem();   // keep the surviving photo at half width
                    }
                });
            }
        }

        private enum ClosingPart { Identification, Tyres, Disclaimer }

        /// <summary>
        /// The report's closing material — the identification shots, the tyres, the
        /// disclaimer — or the part of it given. <paramref name="afterPhotos"/> says it
        /// continues a gallery page, so the first part is spaced off the grid above.
        /// </summary>
        private void ClosingParts(ColumnDescriptor col, List<ClosingPart> parts, bool afterPhotos,
                                  List<(string Label, byte[]? Image)> chassisId, List<byte[]> tyres,
                                  double tyreH, double idPanelMm, bool measuring)
        {
            bool first = !afterPhotos;
            foreach (var part in parts)
            {
                switch (part)
                {
                    case ClosingPart.Identification:
                        // Spaced as one more row of the grid when it follows one.
                        col.Item().PaddingTop(first ? 0 : Mm(3.6)).Row(row =>
                        {
                            for (int c = 0; c < 2; c++)
                            {
                                if (c > 0) row.ConstantItem(Mm(4));
                                if (c < chassisId.Count)
                                {
                                    var item = chassisId[c];
                                    row.RelativeItem().Element(x =>
                                        LabelledPhoto(x, item.Image!, item.Label, HalfColumnMm, idPanelMm, measuring: measuring));
                                }
                                else row.RelativeItem();
                            }
                        });
                        break;

                    case ClosingPart.Tyres:
                        // The template's tyre panels are 74mm tall because its sample shots
                        // were portrait. Real AVO tyre photos are landscape 4:3, and
                        // containing one of those in a tall panel left 55% of it empty grey.
                        // Sizing the row to the photos keeps them filling the panel whichever
                        // way they were shot.
                        col.Item().PaddingTop(first ? 0 : Mm(6)).Row(row =>
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                if (i > 0) row.ConstantItem(Mm(3));
                                if (i < tyres.Count)
                                {
                                    var img = tyres[i];
                                    int n = i + 1;
                                    // Contained, not cropped: cropping a tyre to its panel would
                                    // cut off the tread, which is what the photograph evidences.
                                    row.RelativeItem().Element(x =>
                                        LabelledPhoto(x, img, $"TYRE {n}", 43.5, tyreH, contain: true, measuring: measuring));
                                }
                                else row.RelativeItem();
                            }
                        });
                        break;

                    case ClosingPart.Disclaimer:
                        col.Item().PaddingTop(first ? 0 : Mm(7)).Layers(l =>
                        {
                            l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(3), "#F6F8FB", Border));
                            l.PrimaryLayer().PaddingVertical(Mm(6)).PaddingHorizontal(Mm(7)).Column(c =>
                            {
                                c.Item().PaddingBottom(Mm(1.8)).Text("DISCLAIMER")
                                    .FontFamily(ReportFont).FontSize(9.6f).Bold().FontColor(Navy)
                                    .LetterSpacing(Ls(0.5, 9.6));
                                c.Item().Text(DisclaimerText)
                                    .FontFamily(ReportFont).FontSize(8.6f).FontColor(Label)
                                    .LineHeight(1.5f).Justify();
                            });
                        });
                        break;
                }
                first = false;
            }
        }

        private const string DisclaimerText =
            "This Valuation Report is based on a physical, visual inspection of the vehicle carried out on the " +
            "date of inspection and represents our professional opinion as on that date. The inspection is " +
            "non-intrusive, and hidden, latent, or intermittent defects may not be identified. We do not verify " +
            "or authenticate the genuineness of vehicle documents or odometer readings and assume no " +
            "responsibility thereof. As there is no standard price list for used vehicles, the valuation stated " +
            "is an estimated market value derived using our standard valuation methodology and prevailing " +
            "market conditions. Actual realization may vary. This report is issued solely for the use of the " +
            "addressee and shall not be relied upon by any third party. The company shall not be liable for any " +
            "direct, indirect, incidental, or consequential losses arising from reliance on this report. This " +
            "report is issued without prejudice.";

        /// <summary>
        /// One panel height for the whole tyre row, from the shape of the photos in it.
        ///
        /// Uses the tallest of the four relative to its width, so no photo is letterboxed
        /// more than it has to be, and all four share a height so the row stays even.
        /// Clamped: below 28mm a tyre is too small to judge tread, and above 74mm the row
        /// crowds the disclaimer off the page.
        /// </summary>
        private static double TyrePanelHeightMm(List<byte[]> images, double widthMm)
        {
            double tallest = 0;
            foreach (var bytes in images)
            {
                try
                {
                    using var bmp = SKBitmap.Decode(bytes);
                    if (bmp == null || bmp.Width <= 0) continue;
                    tallest = Math.Max(tallest, widthMm * bmp.Height / bmp.Width);
                }
                catch { /* unreadable frame: it just does not vote on the height */ }
            }
            return tallest <= 0 ? 74 : Math.Clamp(tallest, 28, 74);
        }

        /// <summary>
        /// A photo panel with its caption pill beneath. <paramref name="contain"/> fits
        /// the whole frame inside the panel instead of filling it.
        /// </summary>
        private void LabelledPhoto(IContainer container, byte[] image, string label,
                                   double widthMm, double heightMm, bool contain = false,
                                   bool measuring = false)
        {
            container.Column(c =>
            {
                c.Item().Height(Mm(heightMm)).Layers(l =>
                {
                    l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.8), "#EEF2F6"));
                    l.PrimaryLayer().Element(x =>
                    {
                        // While FillPage measures, the photo is only a height — the panel's
                        // is fixed above — so the decode and crop are skipped rather than
                        // repeated at every step of the search.
                        if (measuring) return;
                        if (contain) x.AlignCenter().AlignMiddle().Image(image).FitArea();
                        // Cropping to the panel's own aspect first means the fill does not
                        // stretch the vehicle — QuestPDF has no object-fit: cover.
                        else x.Image(CropToAspect(image, (float)(widthMm / heightMm))).FitUnproportionally();
                    });
                    l.Layer().Svg(s => RoundedPhotoMask(s.Width, s.Height, Mm(2.8), BorderImg));
                });

                c.Item().PaddingTop(Mm(1.8)).AlignLeft().Layers(l =>
                {
                    l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(10), Navy));
                    l.PrimaryLayer().PaddingVertical(Mm(0.9)).PaddingHorizontal(Mm(3.2)).Row(r =>
                    {
                        r.AutoItem().AlignMiddle().Element(x => DrawIcon(x, "camera", "#FFFFFF", 3.2));
                        r.ConstantItem(Mm(1.8));
                        r.AutoItem().AlignMiddle().Text(label)
                            .FontFamily(ReportFont).FontSize(7.4f).Bold().FontColor("#FFFFFF")
                            .LetterSpacing(Ls(0.3, 7.4));
                    });
                });
            });
        }
    }
}
