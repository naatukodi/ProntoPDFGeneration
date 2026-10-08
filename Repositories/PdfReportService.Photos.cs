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

            // Read once, outside the fills: the fills compose their page again at every
            // step of a search, and each shape is a read of the photo's header.
            var tyreRow = tyres.Select(t => (Image: t, Aspect: PhotoAspect(t))).ToList();
            double tyreH = TyreRowHeightMm(tyreRow.Select(t => t.Aspect).ToList());

            // The identification shots close the gallery, and they are photographs like
            // any other, so they continue the grid instead of starting a row of their own.
            // On AP39X8199 the last page held five photos and the chassis number went to a
            // page of its own, leaving the half beside the selfie empty.
            var rows = GalleryRows(walkAround.Concat(chassisId)
                .Select(p => new GalleryPhoto(p.Label, p.Image!, PhotoAspect(p.Image!)))
                .ToList());

            // What follows the photographs, in the order it reads.
            var closing = new List<ClosingPart>();
            if (tyres.Count > 0) closing.Add(ClosingPart.Tyres);
            closing.Add(ClosingPart.Disclaimer);

            // Six photos to every page but the last: three rows of two, which MaxRowMm
            // keeps within a page whatever the photos' shapes.
            var pages = rows.Chunk(PhotosPerPage / 2).Select(p => p.ToList()).ToList();
            int galleryPages = pages.Count;
            var lastSlice = galleryPages > 0 ? pages[^1] : new List<GalleryRow>();

            // How many closing parts ride up onto the last gallery page, from the front:
            // a last page holding one row of photos used to leave ~156mm of white above
            // the footer, with the tyres alone on the page after.
            int lifted = 0;

            List<ClosingPart> Rest() => closing.Skip(lifted).ToList();
            // A disclaimer left over on its own does not open with the photo heading or
            // count as a photo page. With no gallery at all the heading stays, as before.
            bool ClosingHeading() => Rest().Any(p => p != ClosingPart.Disclaimer) || galleryPages == 0;
            int PhotoPages() => galleryPages + (Rest().Count > 0 && ClosingHeading() ? 1 : 0);

            void GalleryPage(IContainer container, List<GalleryRow> slice,
                             int lift, bool measuring, string tag)
            {
                container.Column(col =>
                {
                    DrawSectionHeading(col.Item().PaddingBottom(Mm(3)), "camera", "Photographic Evidence", tag);
                    PhotoGrid(col, slice, measuring);
                    ClosingParts(col, closing.Take(lift).ToList(), afterPhotos: true,
                                 tyreRow, tyreH, measuring);
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
                var slice = pages[pageNo - 1];

                // Nothing stretches (maxMm 0): the fill is kept for its replay of the
                // layout decision, and so the lifted closing parts are composed lazily.
                main.Item().Dynamic(new FillPage((page, _, measuring) =>
                        GalleryPage(page, slice, last ? lifted : 0, measuring, $"{thisPage} / {PhotoPages()}"),
                    maxMm: 0));
            }

            // ---------- closing page: whatever did not fit under the last photos
            main.Item().Dynamic(new FillPage((page, _, measuring) => page.Column(col =>
                {
                    if (ClosingHeading())
                        DrawSectionHeading(col.Item().PaddingBottom(Mm(3)), "camera",
                                           "Photographic Evidence", $"{PhotoPages()} / {PhotoPages()}");
                    ClosingParts(col, Rest(), afterPhotos: false, tyreRow, tyreH, measuring);
                }), maxMm: 0)
            {
                OwnPage = true,
                Present = () => Rest().Count > 0,
            });
        }

        /// <summary>A gallery photograph and the shape it prints at, width over height.</summary>
        private sealed record GalleryPhoto(string Label, byte[] Image, double Aspect);

        /// <summary>A row of the gallery: its photos, and the height they share.</summary>
        private sealed record GalleryRow(List<GalleryPhoto> Photos, double HeightMm);

        /// <summary>Gap between the two photos of a row, and between rows.</summary>
        private const double PhotoGapMm = 4, RowGapMm = 3.6;

        /// <summary>
        /// The tallest a gallery row may be, so three rows always share a page: 3 x 74mm,
        /// plus three 6.8mm captions and two gaps, is 249.6mm of the ~253mm under the
        /// heading. A pair of 4:3 shots comes to 68mm and never reaches it; a pair of
        /// portrait shots would be 121mm uncapped and prints at 55 x 74mm each.
        /// </summary>
        private const double MaxRowMm = 74;

        /// <summary>
        /// The gallery's rows, two photos to a row, each printed whole at its own shape.
        ///
        /// Photos used to be cut to a fixed 91 x 66mm frame, which suited only a 4:3 shot.
        /// The camera apps in use upload 4:3, 16:9, 2:1 and portrait, and every other shape
        /// lost its edges to the crop — on TG07V8118's 2:1 shots, a sixth off each side
        /// and with it the GPS stamp at the bottom right; portrait shots lost almost half
        /// their height. Now the two photos of a row share one height and split the width
        /// in proportion to their shapes, so each fills its own frame exactly. The row is
        /// narrower than the page only when it reaches <see cref="MaxRowMm"/>, and a photo
        /// left alone on the last row keeps to a half column, as it always has.
        /// </summary>
        private static List<GalleryRow> GalleryRows(List<GalleryPhoto> photos)
        {
            // Rounding leaves the widths a hair over the column; a row that is even 0.01pt
            // too wide for the page is a layout failure, so it is built a fraction under.
            const double Usable = 2 * HalfColumnMm - 0.2;

            var rows = new List<GalleryRow>();
            for (int i = 0; i < photos.Count; i += 2)
            {
                var pair = photos.Skip(i).Take(2).ToList();
                double h = pair.Count == 2
                    ? Usable / pair.Sum(p => p.Aspect)
                    : HalfColumnMm / pair[0].Aspect;
                rows.Add(new GalleryRow(pair, Math.Min(h, MaxRowMm)));
            }
            return rows;
        }

        /// <summary>Each row of photos, centred, each photo over its caption.</summary>
        private void PhotoGrid(ColumnDescriptor col, List<GalleryRow> rows, bool measuring)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                if (r > 0) col.Item().Height(Mm(RowGapMm));
                var row = rows[r];
                col.Item().AlignCenter().Row(line =>
                {
                    for (int i = 0; i < row.Photos.Count; i++)
                    {
                        if (i > 0) line.ConstantItem(Mm(PhotoGapMm));
                        var photo = row.Photos[i];
                        line.ConstantItem(Mm(photo.Aspect * row.HeightMm)).Element(x =>
                            LabelledPhoto(x, photo.Image, photo.Label, row.HeightMm, measuring));
                    }
                });
            }
        }

        /// <summary>
        /// The shape a photo prints at, width over height. Read from the file's header, not
        /// a full decode, and turned by its EXIF orientation as QuestPDF draws it, so a
        /// phone shot stored on its side is framed upright.
        ///
        /// Held between 1:2 and 3:1: a sliver of an image would make its row-mate a sliver
        /// too. A photo outside that is shown whole in its frame, with a margin of panel.
        /// </summary>
        private static double PhotoAspect(byte[] image)
        {
            const double Fallback = 4.0 / 3;
            try
            {
                using var data = SKData.CreateCopy(image);
                using var codec = SKCodec.Create(data);
                if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return Fallback;

                double aspect = (double)codec.Info.Width / codec.Info.Height;
                if (codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                                        or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom)
                    aspect = 1 / aspect;
                return Math.Clamp(aspect, 0.5, 3.0);
            }
            catch { return Fallback; }
        }

        /// <summary>
        /// A soft, blurred copy of a photo that fills a box of the given shape: what a whole
        /// photo is laid over in a box it does not match, in place of bands of grey. Null if
        /// the photo cannot be read; the box then shows its own background.
        /// </summary>
        private static byte[]? BlurredBackdrop(byte[] image, double boxAspect)
        {
            try
            {
                using var data = SKData.CreateCopy(image);
                using var codec = SKCodec.Create(data);
                if (codec == null) return null;
                using var raw = SKBitmap.Decode(codec);
                if (raw == null) return null;
                using var photo = Upright(raw, codec.EncodedOrigin);

                // Small is enough for something this blurred, and cheap to encode.
                const int W = 480;
                int h = Math.Max(1, (int)Math.Round(W / boxAspect));
                using var surface = SKSurface.Create(new SKImageInfo(W, h));
                var canvas = surface.Canvas;

                float scale = Math.Max((float)W / photo.Width, (float)h / photo.Height);
                float dw = photo.Width * scale, dh = photo.Height * scale;
                using var src = SKImage.FromBitmap(photo);
                using var blur = new SKPaint
                {
                    ImageFilter = SKImageFilter.CreateBlur(18, 18, SKShaderTileMode.Clamp),
                };
                canvas.DrawImage(src, SKRect.Create((W - dw) / 2, (h - dh) / 2, dw, dh),
                                 new SKSamplingOptions(SKFilterMode.Linear), blur);
                // A white wash, so the sharp photo on top reads as the subject.
                canvas.DrawColor(new SKColor(255, 255, 255, 90), SKBlendMode.SrcOver);

                using var shot = surface.Snapshot();
                using var jpeg = shot.Encode(SKEncodedImageFormat.Jpeg, 80);
                return jpeg.ToArray();
            }
            catch { return null; }
        }

        /// <summary>
        /// A decoded photo turned the way its EXIF orientation says, as QuestPDF draws the
        /// file itself. SkiaSharp decodes the pixels as stored, which for many phone shots
        /// is on their side.
        /// </summary>
        private static SKBitmap Upright(SKBitmap bmp, SKEncodedOrigin origin)
        {
            float w = bmp.Width, h = bmp.Height;
            // Maps each stored pixel to where it is seen: x' = a·x + b·y + c, y' = d·x + e·y + f.
            (float a, float b, float c, float d, float e, float f) = origin switch
            {
                SKEncodedOrigin.TopRight    => (-1, 0, w,  0, 1, 0),   // mirrored
                SKEncodedOrigin.BottomRight => (-1, 0, w,  0, -1, h),  // upside down
                SKEncodedOrigin.BottomLeft  => (1, 0, 0,   0, -1, h),  // flipped
                SKEncodedOrigin.LeftTop     => (0, 1, 0,   1, 0, 0),   // transposed
                SKEncodedOrigin.RightTop    => (0, -1, h,  1, 0, 0),   // turned a quarter clockwise
                SKEncodedOrigin.RightBottom => (0, -1, h, -1, 0, w),   // transversed
                SKEncodedOrigin.LeftBottom  => (0, 1, 0,  -1, 0, w),   // turned a quarter anticlockwise
                _                           => (1, 0, 0,   0, 1, 0),
            };
            bool sideways = a == 0;
            var upright = new SKBitmap(sideways ? bmp.Height : bmp.Width, sideways ? bmp.Width : bmp.Height);
            using var canvas = new SKCanvas(upright);
            canvas.SetMatrix(new SKMatrix(a, b, c, d, e, f, 0, 0, 1));
            canvas.DrawBitmap(bmp, 0, 0);
            return upright;
        }

        private enum ClosingPart { Tyres, Disclaimer }

        /// <summary>
        /// What follows the photographs — the tyres, the disclaimer — or the part of it
        /// given. <paramref name="afterPhotos"/> says it continues a gallery page, so the
        /// first part is spaced off the grid above.
        /// </summary>
        private void ClosingParts(ColumnDescriptor col, List<ClosingPart> parts, bool afterPhotos,
                                  List<(byte[] Image, double Aspect)> tyres, double tyreH, bool measuring)
        {
            bool first = !afterPhotos;
            foreach (var part in parts)
            {
                switch (part)
                {
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
                                if (i > 0) row.ConstantItem(Mm(TyreGapMm));
                                if (i < tyres.Count)
                                {
                                    var img = tyres[i].Image;
                                    int n = i + 1;
                                    // Shown whole: cropping a tyre to its panel would cut off
                                    // the tread, which is what the photograph evidences.
                                    row.RelativeItem().Element(x =>
                                        LabelledPhoto(x, img, $"TYRE {n}", tyreH, measuring));
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
                                c.Item().Text(DisclaimerText + Theme.LegalNameShort + ".")
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
            // Closed with the issuing company's name, which depends on the brand.
            "report is issued without prejudice by ";

        /// <summary>The gap between tyre panels; four panels and three gaps span the page.</summary>
        private const double TyreGapMm = 3;

        /// <summary>
        /// One panel height for the whole tyre row, from the shape of the photos in it.
        ///
        /// The tallest of the four relative to its width sets it, so that one fills its
        /// panel exactly and the rest are letterboxed no more than they have to be; all four
        /// share it so the row stays even. A set from one camera fills every panel. Capped
        /// at 74mm, where the row would crowd the disclaimer off the page. There is no floor:
        /// the 28mm one there was left 2:1 shots in a band of grey without making the tyre
        /// itself any bigger.
        /// </summary>
        private static double TyreRowHeightMm(List<double> aspects)
        {
            if (aspects.Count == 0) return 0;
            double slotMm = (2 * HalfColumnMm + PhotoGapMm - 3 * TyreGapMm) / 4;
            return Math.Min(slotMm / aspects.Min(), 74);
        }

        /// <summary>
        /// A photo panel with its caption pill beneath. The photo is shown whole, never
        /// cropped or stretched: the gallery sizes each panel to its photo, so it fills it;
        /// anywhere else it sits centred, with a margin of panel where the shapes differ.
        /// </summary>
        private void LabelledPhoto(IContainer container, byte[] image, string label,
                                   double heightMm, bool measuring)
        {
            container.Column(c =>
            {
                c.Item().Height(Mm(heightMm)).Layers(l =>
                {
                    l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(2.8), "#EEF2F6"));
                    l.PrimaryLayer().Element(x =>
                    {
                        // While FillPage measures, the photo is only a height — the panel's
                        // is fixed above — so the decode is skipped rather than repeated at
                        // every step of the search.
                        if (measuring) return;
                        x.AlignCenter().AlignMiddle().Image(image).FitArea();
                    });
                    l.Layer().Svg(s => RoundedPhotoMask(s.Width, s.Height, Mm(2.8), BorderImg));
                });

                // Scaled down rather than overflowing when the caption is wider than its
                // photo: a portrait shot beside a panorama can be under 30mm wide, and a
                // caption that overflows inside a page's fill throws the whole report.
                c.Item().PaddingTop(Mm(1.8)).AlignLeft().ScaleToFit().Layers(l =>
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
