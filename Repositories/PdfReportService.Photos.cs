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

            // Shapes and backdrops are worked out once, outside the fills: the fills compose
            // their page again at every step of a search, and a backdrop is a full decode.
            var tyreAspects = tyres.Select(PhotoAspect).ToList();
            double tyreH = TyreRowHeightMm(tyreAspects);
            double tyreSlot = TyreSlotMm / Math.Max(tyreH, 1);
            var tyreRow = tyres.Select((t, i) => new GalleryPhoto($"TYRE {i + 1}", t,
                    PanelFit.For(t, tyreAspects[i], tyreSlot)))
                .ToList();

            // The identification shots close the gallery, and they are photographs like
            // any other, so they continue the grid instead of starting a row of their own.
            // On AP39X8199 the last page held five photos and the chassis number went to a
            // page of its own, leaving the half beside the selfie empty.
            var gallery = walkAround.Concat(chassisId)
                .Select(p => new GalleryPhoto(p.Label, p.Image!,
                    PanelFit.For(p.Image!, PhotoAspect(p.Image!), GalleryAspect)))
                .ToList();

            // What follows the photographs, in the order it reads.
            var closing = new List<ClosingPart>();
            if (tyres.Count > 0) closing.Add(ClosingPart.Tyres);
            closing.Add(ClosingPart.Disclaimer);

            int galleryPages = (gallery.Count + PhotosPerPage - 1) / PhotosPerPage;
            var lastSlice = gallery.Skip(Math.Max(0, galleryPages - 1) * PhotosPerPage).ToList();

            // How many closing parts ride up onto the last gallery page, from the front:
            // a last page holding one row of photos used to leave ~156mm of white above
            // the footer, with the tyres alone on the page after.
            int lifted = 0;

            List<ClosingPart> Rest() => closing.Skip(lifted).ToList();
            // A disclaimer left over on its own does not open with the photo heading or
            // count as a photo page. With no gallery at all the heading stays, as before.
            bool ClosingHeading() => Rest().Any(p => p != ClosingPart.Disclaimer) || galleryPages == 0;
            int PhotoPages() => galleryPages + (Rest().Count > 0 && ClosingHeading() ? 1 : 0);

            void GalleryPage(IContainer container, List<GalleryPhoto> slice,
                             int lift, bool measuring, string tag)
            {
                container.Column(col =>
                {
                    DrawSectionHeading(col.Item().PaddingBottom(Mm(3)), "camera", "Photographic Evidence", tag);
                    PhotoGrid(col, slice, GalleryPanelMm, measuring);
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
                var slice = gallery.Skip((pageNo - 1) * PhotosPerPage).Take(PhotosPerPage).ToList();

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

        /// <summary>A photograph for a panel: its caption, and how it fits the panel.</summary>
        private sealed record GalleryPhoto(string Label, byte[] Image, PanelFit Fit);

        /// <summary>
        /// How a photo sits in a panel: filling it, when the two are the same shape to
        /// within 1% (a stretch that small does not show, where the margin it would
        /// otherwise leave does), or whole and centred over a blurred copy of itself.
        /// </summary>
        private sealed record PanelFit(bool Fills, byte[]? Backdrop)
        {
            public static PanelFit For(byte[] image, double photoAspect, double panelAspect) =>
                Math.Abs(photoAspect / panelAspect - 1) <= 0.01
                    ? new PanelFit(true, null)
                    : new PanelFit(false, BlurredBackdrop(image, panelAspect));
        }

        /// <summary>
        /// Gallery panels are 91mm wide and exactly 4:3, the shape of the 1600 x 1200 shots
        /// most cases arrive with, so those fill their panel edge to edge. Six of them fill
        /// a page: three rows end ~21mm above the footer.
        /// </summary>
        private const double GalleryPanelMm = HalfColumnMm * 3 / 4;
        private const double GalleryAspect = HalfColumnMm / GalleryPanelMm;

        /// <summary>Photos two to a row, each over its caption.</summary>
        private void PhotoGrid(ColumnDescriptor col, List<GalleryPhoto> photos,
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
                            row.RelativeItem().Element(x => LabelledPhoto(x, item, panelMm, measuring));
                        }
                        else row.RelativeItem();   // keep the surviving photo at half width
                    }
                });
            }
        }

        /// <summary>
        /// The shape a photo prints at, width over height. Read from the file's header, not
        /// a full decode, and turned by its EXIF orientation as QuestPDF draws it, so a
        /// phone shot stored on its side is measured upright.
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
                return aspect;
            }
            catch { return Fallback; }
        }

        /// <summary>
        /// A photo shown whole, never cropped: filling its box when the shapes match,
        /// otherwise centred over its backdrop.
        ///
        /// Panels used to be filled by cropping the photo to their shape, which suited only
        /// a 4:3 shot. Cases arrive in 4:3, 16:9, 2:1 and portrait, and every other shape
        /// lost its edges: on TG07V8118's 2:1 shots a sixth off each side, and with it the
        /// GPS stamp at the bottom right; portrait shots lost almost half their height. The
        /// cover's photo was stretched instead, and printed that truck a third too narrow.
        /// </summary>
        private static void WholePhoto(IContainer container, byte[] image, PanelFit fit)
        {
            if (fit.Fills)
            {
                container.Image(image).FitUnproportionally();
                return;
            }
            container.Layers(l =>
            {
                l.Layer().Element(b => { if (fit.Backdrop != null) b.Image(fit.Backdrop).FitUnproportionally(); });
                l.PrimaryLayer().AlignCenter().AlignMiddle().Image(image).FitArea();
            });
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
                                  List<GalleryPhoto> tyres, double tyreH, bool measuring)
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
                                if (i > 0) row.ConstantItem(Mm(3));
                                if (i < tyres.Count)
                                {
                                    var tyre = tyres[i];
                                    // Shown whole: cropping a tyre to its panel would cut off
                                    // the tread, which is what the photograph evidences.
                                    row.RelativeItem().Element(x => LabelledPhoto(x, tyre, tyreH, measuring));
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

        /// <summary>The width of a tyre panel: four panels and three 3mm gaps span the page.</summary>
        private const double TyreSlotMm = (2 * HalfColumnMm + 4 - 3 * 3) / 4;

        /// <summary>
        /// One panel height for the whole tyre row, from the shape of the photos in it.
        ///
        /// The tallest of the four relative to its width sets it, so that one fills its
        /// panel exactly and the rest sit on their backdrops no more than they have to; all
        /// four share it so the row stays even. A set from one camera fills every panel.
        /// Capped at 74mm, where the row would crowd the disclaimer off the page. There is
        /// no floor: the 28mm one there was left 2:1 shots in a band of grey without making
        /// the tyre itself any bigger.
        /// </summary>
        private static double TyreRowHeightMm(List<double> aspects) =>
            aspects.Count == 0 ? 0 : Math.Min(TyreSlotMm / aspects.Min(), 74);

        /// <summary>A photo panel with its caption pill beneath, the photo shown whole.</summary>
        private void LabelledPhoto(IContainer container, GalleryPhoto photo,
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
                        WholePhoto(x, photo.Image, photo.Fit);
                    });
                    l.Layer().Svg(s => RoundedPhotoMask(s.Width, s.Height, Mm(2.8), BorderImg));
                });

                // Scaled down rather than overflowing when a caption is wider than its
                // photo: a custom photo's name can be any length, and a caption that
                // overflows inside a page's fill throws the whole report.
                c.Item().PaddingTop(Mm(1.8)).AlignLeft().ScaleToFit().Layers(l =>
                {
                    l.Layer().Svg(s => RoundRect(s.Width, s.Height, Mm(10), Navy));
                    l.PrimaryLayer().PaddingVertical(Mm(0.9)).PaddingHorizontal(Mm(3.2)).Row(r =>
                    {
                        r.AutoItem().AlignMiddle().Element(x => DrawIcon(x, "camera", "#FFFFFF", 3.2));
                        r.ConstantItem(Mm(1.8));
                        r.AutoItem().AlignMiddle().Text(photo.Label)
                            .FontFamily(ReportFont).FontSize(7.4f).Bold().FontColor("#FFFFFF")
                            .LetterSpacing(Ls(0.3, 7.4));
                    });
                });
            });
        }
    }
}
