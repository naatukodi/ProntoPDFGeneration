using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF.Companion;
using Valuation.Api.Models;

namespace Valuation.Api.Services
{
    public class PdfReportService
    {
        private readonly HttpClient _httpClient;

        public PdfReportService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        [Obsolete]
        public async Task GenerateAndShowPdf(ValuationDocument doc)
        {
            // 1) Download all photos into memory so we can embed thumbnails
            var photoStreams = new Dictionary<string, byte[]>();
            foreach (var kvp in doc.PhotoUrls)
            {
                if (string.IsNullOrEmpty(kvp.Value))
                    continue;

                var response = await _httpClient.GetAsync(kvp.Value);
                response.EnsureSuccessStatusCode();
                photoStreams[kvp.Key] = await response.Content.ReadAsByteArrayAsync();
            }

            // 2) Build the QuestPDF document
            var pdfDoc = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.Background(Colors.White);
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(12).FontColor(Colors.Black));

                    // ————————————————————————————————————————————————————————
                    // HEADER SECTION
                    // ————————————————————————————————————————————————————————
                    page.Header().Row(headerRow =>
                    {
                        // Column 1: “PRONTO MOTO SERVICES” centered
                        headerRow.RelativeColumn().AlignCenter().AlignMiddle().Row(textRow =>
                        {
                            textRow.AutoItem()
                                .Text("PRONTO")
                                .FontSize(24)
                                .FontColor(Colors.Green.Darken1)
                                .SemiBold();

                            textRow.AutoItem()
                                .Text(" MOTO SERVICES")
                                .FontSize(24)
                                .FontColor(Colors.Red.Darken1)
                                .SemiBold();
                        });

                        // Column 2: Red pill with “VALUATION REPORT” + registration number
                        headerRow.ConstantColumn(180).AlignCenter().AlignMiddle().Stack(stack =>
                        {
                            // Red pill background
                            stack.Item()
                                 .Padding(5)
                                 .Background(Colors.Red.Medium)
                                 .AlignCenter()
                                 .Text("VALUATION REPORT")
                                 .FontColor(Colors.White)
                                 .FontSize(12)
                                 .Bold();

                            // Spacer
                            stack.Item().Height(5);

                            // Registration number below
                            stack.Item()
                                 .Text(doc.VehicleDetails.RegistrationNumber ?? "-")
                                 .FontSize(14)
                                 .FontColor(Colors.Blue.Darken1)
                                 .Bold()
                                 .AlignCenter();
                        });
                    });

                    // ————————————————————————————————————————————————————————
                    // SINGLE CONTENT LAYER: PART 1 + PART 2 + PART 3
                    // ————————————————————————————————————————————————————————
                    page.Content().PaddingVertical(10).Column(main =>
                    {
                        //
                        // ───────── Part 1: Four‐row, four‐column table ─────────
                        //
                        main.Item()
                            .Border(1)
                            .BorderColor(Colors.Blue.Darken2)
                            .Padding(5)
                            .Table(table =>
                            {
                                // Define 4 columns: label, value, label, value
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.ConstantColumn(140);  // left‐label
                                    columns.RelativeColumn();     // left‐value
                                    columns.ConstantColumn(140);  // right‐label
                                    columns.RelativeColumn();     // right‐value
                                });

                                // Helper to write a "label" cell
                                void AddLabel(string text)
                                {
                                    table.Cell()
                                         .Padding(5)
                                         .Text(text)
                                         .SemiBold()
                                         .FontSize(11);
                                }

                                // Helper to write a "value" cell (pale‐blue background)
                                void AddValue(string text, bool includeCalendarIcon = false)
                                {
                                    var paleBlue = Colors.Blue.Lighten4;
                                    var cell = table.Cell()
                                                    .Background(paleBlue)
                                                    .Padding(5);

                                    if (includeCalendarIcon)
                                    {
                                        // Show a tiny calendar emoji next to date
                                        cell.Row(r =>
                                        {
                                            r.ConstantColumn(12).Text("📅");
                                            r.ConstantColumn(5).Text(""); // spacer
                                            r.RelativeColumn().Text(text);
                                        });
                                    }
                                    else
                                    {
                                        cell.Text(text);
                                    }
                                }

                                // ───────── Row 1 ─────────
                                AddLabel("TYPE OF VAL");
                                AddValue(doc.TypeOfVal ?? "-");
                                AddLabel("DATE");
                                AddValue(
                                    doc.dateOfValuation ??
                                         "-",
                                    includeCalendarIcon: true);

                                // ───────── Row 2 ─────────
                                AddLabel("REPORT REQUESTED BY");
                                AddValue(doc.ReportRequestedBy ?? "-");
                                AddLabel("BRANCH");
                                AddValue(doc.VehicleDetails.Lender ?? "-");

                                // ───────── Row 3 ─────────
                                AddLabel("INSPECTION DATE");
                                AddValue(
                                    doc.InspectionDetails.DateOfInspection.HasValue
                                        ? doc.InspectionDetails.DateOfInspection.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                        : "-",
                                    includeCalendarIcon: true);
                                AddLabel("REF NO");
                                AddValue(doc.ReferenceNumber ?? "-");

                                // ───────── Row 4 ─────────
                                AddLabel("INSPECTION LOCATION");
                                AddValue(doc.InspectionDetails.InspectionLocation ?? "-");
                                AddLabel("REGN NO");
                                AddValue(doc.VehicleDetails.RegistrationNumber ?? "-");
                            });

                        //
                        // ───────── Part 2: Two‐column PHOTO + VALUES block ─────────
                        //
                        main.Item()
                            .Border(1)
                            .BorderColor(Colors.Blue.Darken2)
                            .Padding(5)
                            .Row(row =>
                            {
                                //
                                // ── LEFT COLUMN: Additional label/value table ──
                                //
                                row.RelativeColumn(2).Padding(5).Column(col =>
                                {
                                    void AddTableRow(string labelText, string valueText)
                                    {
                                        col.Item().Table(table2 =>
                                        {
                                            table2.ColumnsDefinition(cd =>
                                            {
                                                cd.ConstantColumn(140);   // Label column
                                                cd.RelativeColumn();      // Value column
                                            });

                                            table2.Cell()
                                                  .Background(Colors.Blue.Lighten4)
                                                  .Padding(3)
                                                  .Text(labelText)
                                                  .SemiBold()
                                                  .FontSize(11);

                                            table2.Cell()
                                                  .Background(Colors.Grey.Lighten3)
                                                  .Padding(3)
                                                  .Text(valueText ?? "-")
                                                  .FontSize(11);
                                        });
                                    }

                                    AddTableRow("REGISTERED OWNER  👤", doc.VehicleDetails.OwnerName);
                                    AddTableRow("APPLICANT NAME  👥", doc.Stakeholder.Applicant.Name);
                                    AddTableRow("VEHICLE CATEGORY", doc.VehicleDetails.Model);
                                    AddTableRow("MAKE", doc.VehicleDetails.Make);
                                    AddTableRow("MODEL", doc.VehicleDetails.Model);
                                    AddTableRow("CHASSIS NO  ⚙️", doc.VehicleDetails.ChassisNumber);
                                    AddTableRow("ENGINE NO  ⚙️", doc.VehicleDetails.EngineNumber);
                                    AddTableRow("YEAR OF MFG", doc.VehicleDetails.ManufacturedDate
                                                                                      .HasValue
                                                                                  ? doc.VehicleDetails.ManufacturedDate.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                                                                  : "-");
                                    AddTableRow("REGISTRATION DATE", doc.VehicleDetails.DateOfRegistration
                                                                                 .HasValue
                                                                             ? doc.VehicleDetails.DateOfRegistration.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                                                             : "-");
                                    AddTableRow("CLASS OF VEHICLE", doc.VehicleDetails.ClassOfVehicle);
                                    AddTableRow("BODY TYPE", doc.VehicleDetails.BodyType);
                                    AddTableRow("OWNER SR NO", doc.VehicleDetails.OwnerSerialNo?.ToString());
                                    AddTableRow("HYPOTHECATION", doc.VehicleDetails.Hypothecation.ToString());
                                    // … add more rows if needed …
                                });

                                //
                                // ── RIGHT COLUMN: PHOTO + TIMESTAMP + CAPTION ──
                                //
                                row.RelativeColumn(3).Padding(5).Column(col =>
                                {
                                    // Photo with overlay
                                    col.Item().Border(1).BorderColor(Colors.Blue.Darken2).Stack(stack =>
                                    {
                                        // a) Vehicle photo (use first photo from dictionary as example)
                                        var firstPhotoData = photoStreams.Values.FirstOrDefault();
                                        if (firstPhotoData != null)
                                        {
                                            stack.Item().Image(firstPhotoData, ImageScaling.FitWidth);
                                        }

                                        // b) Overlay timestamp & location text at bottom-left
                                        stack.Item().Padding(5).Background(Colors.Transparent).Column(textCol =>
                                        {
                                            textCol.Item()
                                                .Text(doc.InspectionDetails.DateOfInspection.HasValue
                                                    ? doc.InspectionDetails.DateOfInspection.Value.ToString("d MMM yyyy h:mm:ss tt", CultureInfo.InvariantCulture)
                                                    : "")
                                                .FontSize(9)
                                                .FontColor(Colors.White)
                                                .SemiBold();

                                            textCol.Item()
                                                .Text(doc.InspectionDetails.InspectionLocation ?? "")
                                                .FontSize(9)
                                                .FontColor(Colors.White);
                                        });
                                    });

                                    // Caption bar below the photo
                                    col.Item()
                                       .Border(1)
                                       .BorderColor(Colors.Blue.Darken2)
                                       .Background(Colors.White)
                                       .Padding(5)
                                       .AlignCenter()
                                       .Text($"{doc.VehicleDetails.Model} – {(doc.VehicleDetails.ManufacturedDate.HasValue
                                                                                  ? doc.VehicleDetails.ManufacturedDate.Value.Year.ToString()
                                                                                  : "")}")
                                       .FontSize(14)
                                       .FontColor(Colors.Red.Darken4)
                                       .Bold();
                                });
                            });

                        //
                        // ───────── Part 3: BOTTOM SECTION ─────────
                        //
                        // 3.1) Valuation Price Box
                        main.Item()
                            .Border(2)
                            .BorderColor(Colors.Green.Darken1)
                            .Background(Colors.Transparent)
                            .Padding(5)
                            .Row(row =>
                            {
                                // Left: TEXT "VALUATION PRICE" + CALCULATOR ICON
                                row.AutoItem().AlignMiddle().Row(iconRow =>
                                {
                                    iconRow.AutoItem()
                                        .Text("VALUATION PRICE")
                                        .FontSize(18)
                                        .SemiBold()
                                        .FontColor(Colors.Green.Darken1);

                                    iconRow.AutoItem().Width(24).Height(24).PaddingLeft(5)
                                        .Image("./png/calculator.png", ImageScaling.FitArea);
                                });

                                // Spacer
                                row.RelativeColumn();

                                // Right: Value in large green text
                                row.AutoItem().AlignMiddle()
                                    .Text($"RS. {doc.QualityControl.ValuationAmount:N0}/-")
                                    .FontSize(20)
                                    .Bold()
                                    .FontColor(Colors.Green.Darken1);
                            });

                        // 3.2) Icon Row (Fuel, Odometer, Colour)
                        main.Item().Row(iconRow =>
                        {
                            // Fuel
                            iconRow.RelativeColumn().AlignCenter().Column(col =>
                            {
                                // First set the container size, then call Image(...)
                                col.Item()
                                .Width(30)    // <— set container to 30px wide
                                .Height(30)   // <— set container to 30px tall
                                .Image("./png/fuel.png", ImageScaling.FitArea);

                                col.Item()
                                .Text("FUEL")
                                .FontSize(10)
                                .SemiBold();

                                col.Item()
                                .Text(doc.VehicleDetails.Fuel?.ToUpper() ?? "N/A")
                                .FontSize(10)
                                .FontColor(Colors.Brown.Darken1);
                            });

                            // Odo Meter
                            iconRow.RelativeColumn().AlignCenter().Column(col =>
                            {
                                col.Item()
                                .Width(30)
                                .Height(30)
                                .Image("./png/odometer.jpg", ImageScaling.FitArea);

                                col.Item()
                                .Text("ODO METER")
                                .FontSize(10)
                                .SemiBold();

                                col.Item()
                                .Text(doc.VehicleDetails.Odometer.HasValue
                                    ? doc.VehicleDetails.Odometer.Value.ToString("N0", CultureInfo.InvariantCulture)
                                    : "NOT-WORKING")
                                .FontSize(10)
                                .FontColor(Colors.Green.Darken1);
                            });

                            // Colour
                            iconRow.RelativeColumn().AlignCenter().Column(col =>
                            {
                                col.Item()
                                .Width(30)
                                .Height(30)
                                .Image("./png/colour.jpg", ImageScaling.FitArea);

                                col.Item()
                                .Text("COLOUR")
                                .FontSize(10)
                                .SemiBold();

                                col.Item()
                                .Text(doc.VehicleDetails.Colour?.ToUpper() ?? "NIL")
                                .FontSize(10)
                                .FontColor(Colors.Purple.Darken1);
                            });
                        });

                        // 3.3) “DOCUMENT DETAILS” HEADER BAR
                        main.Item()
                            .Background(Colors.Orange.Lighten3)
                            .Padding(5)
                            .Text("DOCUMENT DETAILS")
                            .FontSize(12)
                            .SemiBold()
                            .FontColor(Colors.White);

                        // 3.4) Permit + Insurance + Fitness + Tax sections
                        main.Item().Table(table =>
                        {
                            // Two columns (Permit/Insurance on top row, Fitness/Tax on bottom row)
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                            });

                            // ─── Row 1: Permit (left) | Insurance (right) ───
                            table.Cell().Column(col =>
                            {
                                // Section header
                                col.Item().Background(Colors.Blue.Medium).Padding(3)
                                   .Text("PERMIT")
                                   .FontSize(10)
                                   .SemiBold()
                                   .FontColor(Colors.White);

                                // Permit fields
                                void AddDocDetail(string label, string value)
                                {
                                    col.Item().Table(inner =>
                                    {
                                        inner.ColumnsDefinition(c =>
                                        {
                                            c.ConstantColumn(100);
                                            c.RelativeColumn();
                                        });
                                        inner.Cell()
                                             .Background(Colors.Blue.Lighten4)
                                             .Padding(3)
                                             .Text(label)
                                             .SemiBold()
                                             .FontSize(10);
                                        inner.Cell()
                                             .Background(Colors.Grey.Lighten3)
                                             .Padding(3)
                                             .Text(value ?? "-")
                                             .FontSize(10);
                                    });
                                }

                                AddDocDetail("PERMIT NO", doc.VehicleDetails.PermitNo ?? "NA");
                                AddDocDetail("PERMIT VALID UP TO",
                                    doc.VehicleDetails.PermitValidUpTo.HasValue
                                        ? doc.VehicleDetails.PermitValidUpTo.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                        : "NA");
                            });

                            table.Cell().Column(col =>
                            {
                                col.Item().Background(Colors.Blue.Medium).Padding(3)
                                   .Text("INSURANCE")
                                   .FontSize(10)
                                   .SemiBold()
                                   .FontColor(Colors.White);

                                void AddInsDetail(string label, string value)
                                {
                                    col.Item().Table(inner =>
                                    {
                                        inner.ColumnsDefinition(c =>
                                        {
                                            c.ConstantColumn(100);
                                            c.RelativeColumn();
                                        });
                                        inner.Cell()
                                             .Background(Colors.Blue.Lighten4)
                                             .Padding(3)
                                             .Text(label)
                                             .SemiBold()
                                             .FontSize(10);
                                        inner.Cell()
                                             .Background(Colors.Grey.Lighten3)
                                             .Padding(3)
                                             .Text(value ?? "-")
                                             .FontSize(10);
                                    });
                                }

                                AddInsDetail("POLICY NO", doc.VehicleDetails.InsurancePolicyNo ?? "NA");
                                AddInsDetail("VALID UP TO",
                                    doc.VehicleDetails.InsuranceValidUpTo.HasValue
                                        ? doc.VehicleDetails.InsuranceValidUpTo.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                        : "NA");
                                AddInsDetail("IDV",
                                    doc.VehicleDetails.IDV.HasValue
                                        ? $"₹{doc.VehicleDetails.IDV.Value:N0}"
                                        : "NA");
                            });

                            // ─── Row 2: Fitness (left) | Tax (right) ───
                            table.Cell().Column(col =>
                            {
                                col.Item().Background(Colors.Blue.Medium).Padding(3)
                                   .Text("FITNESS")
                                   .FontSize(10)
                                   .SemiBold()
                                   .FontColor(Colors.White);

                                col.Item().Table(inner =>
                                {
                                    inner.ColumnsDefinition(c =>
                                    {
                                        c.ConstantColumn(120);
                                        c.RelativeColumn();
                                    });
                                    inner.Cell()
                                         .Background(Colors.Blue.Lighten4)
                                         .Padding(3)
                                         .Text("FITNESS VALID UP TO")
                                         .SemiBold()
                                         .FontSize(10);
                                    inner.Cell()
                                         .Background(Colors.Grey.Lighten3)
                                         .Padding(3)
                                         .Text(doc.VehicleDetails.FitnessValidTo.HasValue
                                             ? doc.VehicleDetails.FitnessValidTo.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                             : "NA")
                                         .FontSize(10);
                                });
                            });

                            table.Cell().Column(col =>
                            {
                                col.Item().Background(Colors.Blue.Medium).Padding(3)
                                   .Text("TAX")
                                   .FontSize(10)
                                   .SemiBold()
                                   .FontColor(Colors.White);

                                col.Item().Table(inner =>
                                {
                                    inner.ColumnsDefinition(c =>
                                    {
                                        c.ConstantColumn(120);
                                        c.RelativeColumn();
                                    });
                                    inner.Cell()
                                         .Background(Colors.Blue.Lighten4)
                                         .Padding(3)
                                         .Text("VALID UP TO")
                                         .SemiBold()
                                         .FontSize(10);
                                    inner.Cell()
                                         .Background(Colors.Grey.Lighten3)
                                         .Padding(3)
                                         .Text(doc.VehicleDetails.TaxUpto.HasValue
                                             ? doc.VehicleDetails.TaxUpto.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                             : "NA")
                                         .FontSize(10);
                                });
                            });
                        });

                        // 3.5) Rating Bars: Chassis Punch, Overall Rating, Valuation Amount
                        main.Item().Row(ratingRow =>
                        {
                            // Chassis Punch (orange bar)
                            ratingRow.RelativeColumn()
                                      .Background(Colors.Orange.Darken1)
                                      .Padding(5)
                                      .AlignCenter()
                                      .Text(doc.QualityControl.ChassisPunch ?? "-")
                                      .FontSize(10)
                                      .FontColor(Colors.White)
                                      .Bold();

                            // Overall Rating (yellow bar)
                            ratingRow.RelativeColumn()
                                      .Background(Colors.Yellow.Medium)
                                      .Padding(5)
                                      .AlignCenter()
                                      .Text(doc.QualityControl.OverallRating ?? "-")
                                      .FontSize(10)
                                      .FontColor(Colors.Black)
                                      .Bold();

                            // Valuation Amount (green bar)
                            ratingRow.RelativeColumn()
                                      .Background(Colors.Green.Darken1)
                                      .Padding(5)
                                      .AlignCenter()
                                      .Text($"RS. {doc.QualityControl.ValuationAmount:N0}/-")
                                      .FontSize(10)
                                      .FontColor(Colors.White)
                                      .Bold();
                        });

                        // 3.6) Remarks Box
                        main.Item()
                            .Border(1)
                            .BorderColor(Colors.Grey.Darken2)
                            .Padding(5)
                            .Column(col =>
                            {
                                col.Item()
                                   .Text("REMARKS :")
                                   .FontSize(10)
                                   .Bold();

                                col.Item()
                                   .Text(doc.QualityControl.Remarks ?? "No remarks")
                                   .FontSize(10);
                            });

                        // 3.7) Stamp + Signature Row
                        main.Item().Row(row =>
                        {
                            // Stamp (left)
                            row.RelativeColumn()
                               .AlignCenter()
                               .Width(60)
                               .Height(60)
                               .Image("./png/stamp.png", ImageScaling.FitArea);

                            // Signature + “Approved by” text (right)
                            row.RelativeColumn().Column(col =>
                            {
                                col.Item()
                                   .AlignLeft()
                                   .Text("Approved by")
                                   .FontSize(10)
                                   .FontColor(Colors.Black);

                                col.Item()
                                    .Width(60)
                                    .Height(60)
                                   .Image("./png/signature.png", ImageScaling.FitArea);

                                col.Item()
                                   .Text("Mahesh Garikina\nLicense No : 74183\nPronto Moto Services")
                                   .FontSize(9)
                                   .FontColor(Colors.Black)
                                   .AlignLeft();
                            });
                        });

                        // ──────────────────────────────────────────────────────────────────────────────
                        // PART X: SYSTEMS INSPECTION SECTION
                        // ──────────────────────────────────────────────────────────────────────────────
                        main.Item().Row(row =>
                        {
                            // LEFT HALF: BASIC, TRANSMISSION, COOLING, STEERING
                            row.RelativeColumn().Padding(5).Column(col =>
                            {
                                // Helper to draw a category header
                                void CategoryHeader(string title)
                                {
                                    col.Item()
                                    .Background(Colors.Blue.Medium)
                                    .Padding(5)
                                    .Text(title)
                                    .FontColor(Colors.White)
                                    .FontSize(12)
                                    .SemiBold()
                                    .AlignCenter();
                                }

                                // Helper to draw a two‐column table for one category
                                void CategoryTable(IEnumerable<(string Label, string Value)> rowsData)
                                {
                                    col.Item().Table(table =>
                                    {
                                        table.ColumnsDefinition(cd =>
                                        {
                                            cd.ConstantColumn(140);
                                            cd.RelativeColumn();
                                        });

                                        foreach (var (label, value) in rowsData)
                                        {
                                            table.Cell().Background(Colors.Grey.Lighten3).Padding(3)
                                                .Text(label).FontSize(10).SemiBold();
                                            table.Cell().Background(Colors.Grey.Lighten4).Padding(3)
                                                .Text(value ?? "-").FontSize(10);
                                        }
                                    });
                                }

                                // 1) BASIC SYSTEMS
                                CategoryHeader("BASIC SYSTEMS");
                                CategoryTable(new[]
                                {
                                    ("ENGINE CONDITION",        doc.InspectionDetails.EngineCondition),
                                    ("CHASSIS CONDITION",       doc.InspectionDetails.ChassisCondition),
                                    ("CABIN ASSY",              doc.InspectionDetails.Cabin),
                                    ("LOAD BODY ASSY",          doc.InspectionDetails.BodyCondition),
                                    ("STEERING SYSTEM",         doc.InspectionDetails.SteeringAssy),
                                    ("BRAKE SYSTEM",            doc.InspectionDetails.BrakeSystem),
                                    ("ELECTRICAL SYSTEM",       doc.InspectionDetails.ElectricAssembly),
                                    ("SUSPENSION SYSTEM",       doc.InspectionDetails.SuspensionSystem),
                                    ("FUEL SYSTEM",             doc.VehicleDetails.Fuel),      // or if you have FuelSystem field
                                    ("TYRE CONDITION",          doc.InspectionDetails.OverallTyreCondition)
                                });

                                // 2) TRANSMISSION SYSTEM
                                CategoryHeader("TRANSMISSION SYSTEM");
                                CategoryTable(new[]
                                {
                                    ("GEARBOX ASSY",            doc.InspectionDetails.GearBoxAssy),
                                    ("CLUTCH SYSTEM",           doc.InspectionDetails.ClutchSystem),
                                    ("DIFFERENTIAL ASSY",       doc.InspectionDetails.DifferentialAssy)
                                });

                                // 3) COOLING SYSTEM
                                CategoryHeader("COOLING SYSTEM");
                                CategoryTable(new[]
                                {
                                    ("RADIATOR",                doc.InspectionDetails.Radiator),
                                    ("INTER COOLER",            doc.InspectionDetails.Intercooler),
                                    ("ALL HOSE PIPES",          doc.InspectionDetails.AllHosePipes)
                                });

                                // 4) STEERING SYSTEM
                                CategoryHeader("STEERING SYSTEM");
                                CategoryTable(new[]
                                {
                                    ("STEERING COLUMN",         doc.InspectionDetails.SteeringAssy),    // if separate
                                    ("BRAKE SYSTEM",            doc.InspectionDetails.BrakeSystem),
                                    ("SUSPENSION SYSTEM",       doc.InspectionDetails.SuspensionSystem)
                                });
                            });

                            // RIGHT HALF: CABIN, LOAD BODY, ELECTRICAL, SUSPENSION, OTHER
                            row.RelativeColumn().Padding(5).Column(col =>
                            {
                                // Re-use the same helpers
                                void CategoryHeader(string title)
                                {
                                    col.Item()
                                    .Background(Colors.Blue.Medium)
                                    .Padding(5)
                                    .Text(title)
                                    .FontColor(Colors.White)
                                    .FontSize(12)
                                    .SemiBold()
                                    .AlignCenter();
                                }

                                void CategoryTable(IEnumerable<(string Label, string Value)> rowsData)
                                {
                                    col.Item().Table(table =>
                                    {
                                        table.ColumnsDefinition(cd =>
                                        {
                                            cd.ConstantColumn(140);
                                            cd.RelativeColumn();
                                        });

                                        foreach (var (label, value) in rowsData)
                                        {
                                            table.Cell().Background(Colors.Grey.Lighten3).Padding(3)
                                                .Text(label).FontSize(10).SemiBold();
                                            table.Cell().Background(Colors.Grey.Lighten4).Padding(3)
                                                .Text(value ?? "-").FontSize(10);
                                        }
                                    });
                                }

                                // 1) CABIN
                                CategoryHeader("CABIN");
                                CategoryTable(new[]
                                {
                                    ("CABIN",                   doc.InspectionDetails.Cabin),
                                    ("DASHBOARD",               doc.InspectionDetails.Dashboard),
                                    ("ALL GLASSES",             doc.InspectionDetails.WindshieldGlass),
                                    ("SEATS",                   doc.InspectionDetails.Seats)
                                });

                                // 2) LOAD BODY
                                CategoryHeader("LOAD BODY");
                                CategoryTable(new[]
                                {
                                    ("CABIN",                   doc.InspectionDetails.Cabin),
                                    ("SEATS",                   doc.InspectionDetails.Seats),
                                    ("PROPELLER SHAFT",         doc.InspectionDetails.PropellerShaft),
                                    ("BODY CONDITION",          doc.InspectionDetails.BodyCondition)
                                });

                                // 3) ELECTRICAL SYSTEM
                                CategoryHeader("ELECTRICAL SYSTEM");
                                CategoryTable(new[]
                                {
                                    ("LIGHTS",                  doc.InspectionDetails.HeadLamps),
                                    ("BATTERY",                 doc.InspectionDetails.BatteryCondition),
                                    ("WIRING ASSY",             doc.InspectionDetails.ElectricAssembly)
                                });

                                // 4) SUSPENSION SYSTEM
                                CategoryHeader("SUSPENSION SYSTEM");
                                CategoryTable(new[]
                                {
                                    ("FRONT",                   doc.InspectionDetails.SuspensionSystem),  // if front/back separate
                                    ("AXLES",                   doc.InspectionDetails.PropellerShaft)     // or other field
                                });

                                // 5) OTHER SYSTEMS
                                CategoryHeader("OTHER SYSTEMS");
                                CategoryTable(new[]
                                {
                                    ("AIR CONDITIONER",         doc.InspectionDetails.Intercooler),
                                    ("PAINT WORK",              doc.InspectionDetails.PaintWork)
                                });
                            });
                        });

                        // ──────────────────────────────────────────────────────────────────────────────
                        // PART X+: Chassis Photo, Stencil Trace, Disclaimer, Stamp+Signature, Footer
                        // ──────────────────────────────────────────────────────────────────────────────
                        main.Item().Column(col =>
                        {
                            // Helper to draw a full-width label + fixed-size image row
                            void ImageRow(string label, byte[]? imageData)
                            {
                                col.Item().Row(row =>
                                {
                                    // Left label cell (unchanged)
                                    row.ConstantColumn(180)
                                    .Height(50)
                                    .Background(Colors.Blue.Medium)
                                    .Padding(10)
                                    .AlignCenter()
                                    .AlignMiddle()
                                    .Text(label)
                                    .FontColor(Colors.White)
                                    .FontSize(12)
                                    .SemiBold();

                                    // Right image cell: fixed height + max width + fit‐area scaling
                                    row.RelativeColumn()
                                    .MaxWidth(300)   // never exceed 300px wide
                                    .Height(55)     // always 55px tall
                                    .Padding(5)
                                    .Border(1)
                                    .BorderColor(Colors.Blue.Medium)
                                    .AlignCenter()
                                    .AlignMiddle()
                                    .Image(imageData ?? Array.Empty<byte>(), ImageScaling.FitArea);
                                });
                            }

                            // 1) Chassis No Photo
                            ImageRow(
                            label: "CHASSIS NO PHOTO",
                            imageData: photoStreams.TryGetValue("ChassisNumberPlate", out var c) ? c : null
                            );

                            // 2) Stencil Trace
                            ImageRow(
                            label: "STENCIL TRACE",
                            imageData: photoStreams.TryGetValue("ChassisImprint", out var t) ? t : null
                            );

                            col.Item().PaddingBottom(10);
                            // 3) DISCLAIMER box (full width)
                            col.Item()
                            .Border(1)
                            .BorderColor(Colors.Grey.Darken2)
                            .Padding(10)
                            .PaddingBottom(15)
                            .Text(txt =>
                            {
                                txt.Span("DISCLAIMER :").FontSize(10).SemiBold();
                                txt.Span(" We are not responsible for verifying the authenticity of the associated documents of the vehicle. We are not relied on the odometer reading of any vehicle at the time of physical inspection and isn’t answerable for verifying the authenticity thereof. Our organization is not responsible for any direct, indirect or exceptional damages for any misuse of this report. As there is no any standard price list for used vehicles, valuation amount mentioned in this report is our professional opinion on the market value of the vehicle based on our standard valuation methodology & procedures. This report is based entirely on the personnel inspection carried out and is issued without prejudice or favour nor bindings.")
                                    .FontSize(9);
                            });

                            // 4) Stamp + Signature (side by side)
                            col.Item().Row(row =>
                            {
                                // Stamp
                                row.ConstantColumn(120).AlignCenter()
                                    .Height(120)
                                    .Image("./png/stamp.png", ImageScaling.FitArea);

                                // Signature and details
                                row.RelativeColumn().PaddingLeft(20).Column(sigCol =>
                                {
                                    sigCol.Item()
                                        .Text("Approved by")
                                        .FontSize(10)
                                        .SemiBold();

                                    sigCol.Item()
                                        .Height(60)
                                        .PaddingTop(5)
                                        .Image("./png/signature.png", ImageScaling.FitArea);

                                    sigCol.Item()
                                        .PaddingTop(5)
                                        .Text("Mahesh Garikina\nLicense No : 74183\nPronto Moto Services")
                                        .FontSize(9);
                                });
                            });


                            // 5) Footer line + contact info
                            col.Item().PaddingBottom(5).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

                            col.Item()
                               .Grid(grid =>
                               {
                                   grid.Columns(2);        // two per row
                                   grid.Spacing(10);

                                   foreach (var kvp in photoStreams)
                                   {
                                       var imageData = kvp.Value;
                                       var dateText = doc.InspectionDetails.DateOfInspection
                                                           ?.ToString("d MMM yyyy h:mm:ss tt", CultureInfo.InvariantCulture)
                                                       ?? "";
                                       var locText = doc.InspectionDetails.InspectionLocation ?? "";

                                       grid.Item().Column(c =>
                                       {
                                           // fixed‐size box
                                           c.Item()
                                               .Width(320)     // fixed width 8x4
                                               .Height(240)    // fixed height 8x3
                                               .Border(1)
                                               .BorderColor(Colors.Blue.Darken2)
                                               .Stack(stack =>
                                               {
                                                   // 1) Photo at bottom layer
                                                   stack.Item()
                                                       .Image(imageData, ImageScaling.FitArea);

                                                   // 2) Overlay date+location at top‐left
                                                   stack.Item()
                                                       .Padding(5)
                                                       .Background(Colors.Black.WithAlpha(1))  // semi‐transparent bg
                                                       .AlignTop()
                                                       .AlignLeft()
                                                       .Column(txt =>
                                                       {
                                                           txt.Item()
                                                               .Text(dateText)
                                                               .FontSize(8)
                                                               .FontColor(Colors.White);

                                                           txt.Item()
                                                               .Text(locText)
                                                               .FontSize(8)
                                                               .FontColor(Colors.White);
                                                       });
                                               });

                                           // caption below
                                           c.Item()
                                               .PaddingTop(5)
                                               .AlignCenter()
                                               .Text(kvp.Key)
                                               .FontSize(9)
                                               .SemiBold();
                                       });
                                   }
                               });


                        });


                        // 3.8) Disclaimer + Icons Footer
                        main.Item().Column(col =>
                        {
                            col.Item().PaddingTop(20);

                            // Disclaimer
                            col.Item()
                               .Text("THIS REPORT IS ISSUED WITHOUT PREJUDICE")
                               .FontSize(10)
                               .Bold()
                               .AlignCenter();

                            // Thin gray separator
                            col.Item().LineHorizontal(1);
                            col.Item().BorderColor(Colors.Grey.Lighten2)
                                      .Height(1)
                                      .PaddingVertical(5);
                            col.Item().Padding(5);

                            // Address + contact icons
                            col.Item().Row(footerRow =>
                            {
                                footerRow.AutoItem()
                                         .Text("🏠 Registered Address: F-1, 2-216/A, Vakalapudi, Kakinada, East Godavari Dist, Andhra Pradesh – 533005")
                                         .FontSize(9);

                                footerRow.AutoItem()
                                         .PaddingLeft(10)
                                         .Text("🌐 www.prontomoto.in")
                                         .FontSize(9);


                            });
                            col.Item()
                                .AlignCenter()         // center the whole row within its parent
                                .Row(footerRow =>
                                {
                                    footerRow.AutoItem()
                                                .Text("✉ connect@prontomoto.in")
                                                .FontSize(9);

                                    footerRow.AutoItem()
                                                .PaddingLeft(20)
                                                .Text("☎ 0884-3596574")
                                                .FontSize(9);

                                    footerRow.AutoItem()
                                                .PaddingLeft(20)
                                                .Text("📱 +91 9885755567")
                                                .FontSize(9);
                                });



                        });


                    }); // end of page.Content()

                    // ————————————————————————————————————————————————————————
                    // FOOTER SECTION (Optional separate footer)
                    // ————————————————————————————————————————————————————————
                    page.Footer().AlignCenter().Text("Generated by Pronto Moto Services");
                });
            });

            // 3) Show the live preview in QuestPDF Companion
            pdfDoc.ShowInCompanion();

            // 4) (Optional) Generate PDF bytes if you need them:
            // byte[] pdfBytes = pdfDoc.GeneratePdf();
        }
    }
}
