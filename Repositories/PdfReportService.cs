using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Valuation.Api.Models;
using Microsoft.Azure.Cosmos;
using System.Net;

namespace Valuation.Api.Services
{
    public class PdfReportService
    {
        private readonly CosmosClient _cosmos;
        private readonly string _dbId;
        private readonly string _containerId;

        private Container Container => _cosmos.GetDatabase(_dbId).GetContainer(_containerId);

        private PartitionKey GetPk(string vehicleNumber, string applicantContact) =>
            new($"{vehicleNumber}|{applicantContact}");  // use colon delimiter for composite key

        private readonly HttpClient _httpClient;

        public PdfReportService(HttpClient httpClient, CosmosClient cosmos, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _cosmos = cosmos;
            _dbId = configuration["Cosmos:DatabaseId"] ?? "ValuationsDb";
            _containerId = configuration["Cosmos:ContainerId"] ?? "Valuations";
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
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        [Obsolete]
        public async Task<byte[]> GenerateAndShowPdf(ValuationDocument doc)
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

                         // 1) Logo in its own constant‐width column
                        headerRow.ConstantColumn(56)                // 60pt wide for the logo
                        .AlignCenter()
                        .Height(56)                        // 32pt tall
                        .Image("./png/logo.png", ImageScaling.FitArea);

                        // 2) Some spacing
                        headerRow.ConstantColumn(10);   

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
                                // 1) Define your four columns
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.ConstantColumn(140);  // left label
                                    columns.RelativeColumn();     // left value
                                    columns.ConstantColumn(140);  // right label
                                    columns.RelativeColumn();     // right value
                                });

                                // 2) (Optional) Header row
                                table.Header(header =>
                                {
                                    header.Cell().Background(Colors.Blue.Darken1).Padding(5)
                                        .Text("FIELD").FontColor(Colors.White).SemiBold();
                                    header.Cell().Background(Colors.Blue.Darken1).Padding(5)
                                        .Text("VALUE").FontColor(Colors.White).SemiBold();
                                    header.Cell().Background(Colors.Blue.Darken1).Padding(5)
                                        .Text("FIELD").FontColor(Colors.White).SemiBold();
                                    header.Cell().Background(Colors.Blue.Darken1).Padding(5)
                                        .Text("VALUE").FontColor(Colors.White).SemiBold();
                                });

                                // 3) Your rows as tuples
                                var rows = new[]
                                {
                                    ("TYPE OF VAL",           doc.TypeOfVal ?? "-",
                                    "DATE",                  doc.CreatedAt.ToString("dd-MM-yy", CultureInfo.InvariantCulture)),

                                    ("REPORT REQUESTED BY",   doc.Stakeholder.ExecutiveName ?? "-",
                                    "BRANCH",                doc.Stakeholder.Name ?? "-"),

                                    ("INSPECTION DATE",       doc.InspectionDetails.DateOfInspection.HasValue
                                                                ? doc.InspectionDetails.DateOfInspection.Value
                                                                    .ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                                                : "-",
                                    "REF NO",                doc.ReferenceNumber ?? "-"),

                                    ("INSPECTION LOCATION",   doc.InspectionDetails.InspectionLocation ?? "-",
                                    "REGN NO",               doc.VehicleDetails.RegistrationNumber ?? "-")
                                };

                                // 4) Iterate, optionally zebra‐stripe each row
                                for (int i = 0; i < rows.Length; i++)
                                {
                                    var bg = (i % 2 == 0)
                                        ? Colors.Grey.Lighten5   // even rows
                                        : Colors.Grey.Lighten1;  // odd rows

                                    var (l1, v1, l2, v2) = rows[i];

                                    // Left label
                                    table.Cell().Background(bg).Padding(5)
                                        .Text(l1).FontSize(11).SemiBold();

                                    // Left value (pale‐blue background for values)
                                    table.Cell().Background(Colors.Blue.Lighten4).Padding(5)
                                        .Text(v1).FontSize(11);

                                    // Right label
                                    table.Cell().Background(bg).Padding(5)
                                        .Text(l2).FontSize(11).SemiBold();

                                    // Right value
                                    table.Cell().Background(Colors.Blue.Lighten4).Padding(5)
                                        .Text(v2).FontSize(11);
                                }
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
                                row.RelativeColumn(1)
                                    .Padding(5)
                                    .Table(table =>
                                    {
                                        // Define two columns: label + value
                                        table.ColumnsDefinition(cd =>
                                        {
                                            cd.ConstantColumn(140);
                                            cd.RelativeColumn();
                                        });

                                        // Optional: set header row styling
                                        table.Header(header =>
                                        {
                                            header.Cell().Background(Colors.Blue.Darken1).Padding(5)
                                                    .Text("FIELD").FontColor(Colors.White).SemiBold();
                                            header.Cell().Background(Colors.Blue.Darken1).Padding(5)
                                                    .Text("VALUE").FontColor(Colors.White).SemiBold();
                                        });

                                        // Data rows (you can alternate background colors if you like)
                                        var rows = new[]
                                        {
                                            ("REGISTERED OWNER", doc.VehicleDetails.OwnerName),
                                            ("APPLICANT NAME",  doc.Stakeholder.Applicant.Name),
                                            ("VEHICLE CATEGORY",      doc.VehicleDetails.Model),
                                            ("MAKE",                  doc.VehicleDetails.Make),
                                            ("MODEL",                 doc.VehicleDetails.Model),
                                            ("CHASSIS NO ⚙️",         doc.VehicleDetails.ChassisNumber),
                                            ("ENGINE NO ⚙️",          doc.VehicleDetails.EngineNumber),
                                            ("YEAR OF MFG",           doc.VehicleDetails.ManufacturedDate.HasValue
                                                                        ? doc.VehicleDetails.ManufacturedDate.Value
                                                                                .ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                                                        : "-"),
                                            ("REGISTRATION DATE",     doc.VehicleDetails.DateOfRegistration.HasValue
                                                                        ? doc.VehicleDetails.DateOfRegistration.Value
                                                                                .ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                                                        : "-"),
                                            ("CLASS OF VEHICLE",       doc.VehicleDetails.ClassOfVehicle),
                                            ("BODY TYPE",             doc.VehicleDetails.BodyType),
                                            ("OWNER SR NO",            doc.VehicleDetails.OwnerSerialNo?.ToString()),
                                            ("HYPOTHECATION",          doc.VehicleDetails.Hypothecation.ToString())
                                        };

                                        for (int i = 0; i < rows.Length; i++)
                                        {
                                            var (label, value) = rows[i];
                                            var bg = (i % 2 == 0)
                                                ? Colors.Grey.Lighten5   // even row
                                                : Colors.Grey.Lighten1;  // odd row

                                            table.Cell().Background(bg).Padding(5)
                                                    .Text(label).FontSize(11).SemiBold();

                                            table.Cell().Background(bg).Padding(5)
                                                    .Text(value ?? "-").FontSize(11);
                                        }
                                    });

                                //
                                // ── RIGHT COLUMN: PHOTO + TIMESTAMP + CAPTION ──
                                //
                                row.RelativeColumn(1).Padding(5).Column(col =>
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
                                .Width(20)    // <— set container to 10px wide
                                .Height(20)   // <— set container to 10px tall
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
                                .Width(20)
                                .Height(20)
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
                                .Width(20)
                                .Height(20)
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

                        // 3.7) Stamp + Signature Row
                        main.Item().Row(row =>
                        {
                            // Stamp (left)
                            row.RelativeColumn()
                               .AlignCenter()
                               .Width(48)
                               .Height(48)
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
                                    .Width(48)
                                    .Height(40)
                                   .Image("./png/signature.png", ImageScaling.FitArea);

                                col.Item()
                                   .Text("Mahesh Garikina\nLicense No : 74183\nPronto Moto Services")
                                   .FontSize(9)
                                   .FontColor(Colors.Black)
                                   .AlignLeft();
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
                        main.Item()
                            .Border(1)
                            .BorderColor(Colors.Blue.Darken2)
                            .Padding(5)
                            .Table(table =>
                            {
                                // 1) Define four columns: label, value, label, value
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.ConstantColumn(140);
                                    columns.RelativeColumn();
                                    columns.ConstantColumn(140);
                                    columns.RelativeColumn();
                                });

                                // 2) Helper to draw a full-width category header
                                void AddCategoryHeader(string title)
                                {
                                    table.Cell()
                                        .ColumnSpan(4)
                                        .Background(Colors.Blue.Medium)
                                        .Padding(5)
                                        .AlignCenter()
                                        .Text(title)
                                        .FontColor(Colors.White)
                                        .FontSize(10)
                                        .SemiBold();
                                }

                                // 3) Helper to add one data‐row (two label/value pairs) with optional zebra
                                int rowIndex = 0;
                                void AddDataRow((string Label, string Value) left, (string Label, string Value) right)
                                {
                                    var bg = (rowIndex++ % 2 == 0)
                                        ? Colors.Grey.Lighten1
                                        : Colors.Grey.Lighten5;

                                    // Left label cell
                                    table.Cell()
                                        .Background(bg)
                                        .Padding(5)
                                        .Text(left.Label)
                                        .FontSize(10)
                                        .SemiBold();

                                    // Left value cell
                                    table.Cell()
                                        .Background(Colors.Blue.Lighten4)
                                        .Padding(5)
                                        .Text(left.Value)
                                        .FontSize(10);

                                    // Right label cell
                                    table.Cell()
                                        .Background(bg)
                                        .Padding(5)
                                        .Text(right.Label)
                                        .FontSize(10)
                                        .SemiBold();

                                    // Right value cell
                                    table.Cell()
                                        .Background(Colors.Blue.Lighten4)
                                        .Padding(5)
                                        .Text(right.Value)
                                        .FontSize(10);
                                }

                                // ───── Permit & Insurance ─────
                                AddCategoryHeader("PERMIT & INSURANCE");
                                AddDataRow(
                                    ("PERMIT NO",      doc.VehicleDetails.PermitNo    ?? "NA"),
                                    ("POLICY NO",      doc.VehicleDetails.InsurancePolicyNo ?? "NA")
                                );
                                AddDataRow(
                                    ("PERMIT VALID UP TO",
                                        doc.VehicleDetails.PermitValidUpTo.HasValue
                                        ? doc.VehicleDetails.PermitValidUpTo.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                        : "NA"),
                                    ("INSURANCE VALID UP TO",
                                        doc.VehicleDetails.InsuranceValidUpTo.HasValue
                                        ? doc.VehicleDetails.InsuranceValidUpTo.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                        : "NA")
                                );
                                AddDataRow(
                                    ("IDV",  
                                        doc.VehicleDetails.IDV.HasValue 
                                        ? $"₹{doc.VehicleDetails.IDV.Value:N0}" 
                                        : "NA"),
                                    ("", "")
                                );

                                // ───── Fitness & Tax ─────
                                AddCategoryHeader("FITNESS & TAX");
                                AddDataRow(
                                    ("FITNESS VALID UP TO",
                                        doc.VehicleDetails.FitnessValidTo.HasValue
                                        ? doc.VehicleDetails.FitnessValidTo.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                        : "NA"),
                                    ("TAX VALID UP TO",
                                        doc.VehicleDetails.TaxUpto.HasValue
                                        ? doc.VehicleDetails.TaxUpto.Value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)
                                        : "NA")
                                );
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

                        // ──────────────────────────────────────────────────────────────────────────────
                        // PART X: SYSTEMS INSPECTION SECTION
                        // ──────────────────────────────────────────────────────────────────────────────
                        main.Item().Row(row =>
                        {
                            // ─────────────────────── LEFT HALF ───────────────────────
                            row.RelativeColumn().Padding(5).Table(table =>
                            {
                                // 1) Define two columns: label + value
                                table.ColumnsDefinition(cd =>
                                {
                                    cd.ConstantColumn(140);
                                    cd.RelativeColumn();
                                });

                                // 2) Helper to draw a category header spanning both columns
                                void AddCategoryHeader(string title)
                                {
                                    table.Cell()
                                        .ColumnSpan(2)                           // span across both cols
                                        .Background(Colors.Blue.Medium)
                                        .Padding(5)
                                        .Text(title)
                                        .FontColor(Colors.White)
                                        .FontSize(12)
                                        .SemiBold()
                                        .AlignCenter();
                                }

                                // 3) Helper to draw a normal data row (with optional zebra)
                                int rowIndex = 0;
                                void AddDataRow(string label, string value)
                                {
                                    var bg = (rowIndex++ % 2 == 0)
                                        ? Colors.Grey.Lighten1
                                        : Colors.Grey.Lighten5;

                                    table.Cell()
                                        .Background(bg)
                                        .Padding(5)
                                        .Text(label)
                                        .FontSize(10)
                                        .SemiBold();

                                    table.Cell()
                                        .Background(bg)
                                        .Padding(5)
                                        .Text(value ?? "-")
                                        .FontSize(10);
                                }

                                // ─ BASIC SYSTEMS ─
                                AddCategoryHeader("BASIC SYSTEMS");
                                AddDataRow("ENGINE CONDITION",      doc.InspectionDetails.EngineCondition);
                                AddDataRow("CHASSIS CONDITION",     doc.InspectionDetails.ChassisCondition);
                                AddDataRow("CABIN ASSY",            doc.InspectionDetails.Cabin);
                                AddDataRow("LOAD BODY ASSY",        doc.InspectionDetails.BodyCondition);
                                AddDataRow("STEERING SYSTEM",       doc.InspectionDetails.SteeringAssy);
                                AddDataRow("BRAKE SYSTEM",          doc.InspectionDetails.BrakeSystem);
                                AddDataRow("ELECTRICAL SYSTEM",     doc.InspectionDetails.ElectricAssembly);
                                AddDataRow("SUSPENSION SYSTEM",     doc.InspectionDetails.SuspensionSystem);
                                AddDataRow("FUEL SYSTEM",           doc.VehicleDetails.Fuel);
                                AddDataRow("TYRE CONDITION",        doc.InspectionDetails.OverallTyreCondition);

                                // ─ TRANSMISSION SYSTEM ─
                                AddCategoryHeader("TRANSMISSION SYSTEM");
                                AddDataRow("GEARBOX ASSY",          doc.InspectionDetails.GearBoxAssy);
                                AddDataRow("CLUTCH SYSTEM",         doc.InspectionDetails.ClutchSystem);
                                AddDataRow("DIFFERENTIAL ASSY",     doc.InspectionDetails.DifferentialAssy);

                                // ─ COOLING SYSTEM ─
                                AddCategoryHeader("COOLING SYSTEM");
                                AddDataRow("RADIATOR",              doc.InspectionDetails.Radiator);
                                AddDataRow("INTER COOLER",          doc.InspectionDetails.Intercooler);
                                AddDataRow("ALL HOSE PIPES",        doc.InspectionDetails.AllHosePipes);

                                // ─ STEERING SYSTEM ─
                                AddCategoryHeader("STEERING SYSTEM");
                                AddDataRow("STEERING COLUMN",       doc.InspectionDetails.SteeringAssy);
                                AddDataRow("BRAKE SYSTEM",          doc.InspectionDetails.BrakeSystem);
                                AddDataRow("SUSPENSION SYSTEM",     doc.InspectionDetails.SuspensionSystem);
                            });


                            // ─────────────────────── RIGHT HALF ───────────────────────
                            row.RelativeColumn().Padding(5).Table(table =>
                            {
                                // same two‐column definition
                                table.ColumnsDefinition(cd =>
                                {
                                    cd.ConstantColumn(140);
                                    cd.RelativeColumn();
                                });

                                void AddCategoryHeader(string title)
                                {
                                    table.Cell()
                                        .ColumnSpan(2)
                                        .Background(Colors.Blue.Medium)
                                        .Padding(5)
                                        .Text(title)
                                        .FontColor(Colors.White)
                                        .FontSize(12)
                                        .SemiBold()
                                        .AlignCenter();
                                }

                                int rowIndex = 0;
                                void AddDataRow(string label, string value)
                                {
                                    var bg = (rowIndex++ % 2 == 0)
                                        ? Colors.Grey.Lighten1
                                        : Colors.Grey.Lighten5;

                                    table.Cell()
                                        .Background(bg)
                                        .Padding(5)
                                        .Text(label)
                                        .FontSize(10)
                                        .SemiBold();

                                    table.Cell()
                                        .Background(bg)
                                        .Padding(5)
                                        .Text(value ?? "-")
                                        .FontSize(10);
                                }

                                // ─ CABIN ─
                                AddCategoryHeader("CABIN");
                                AddDataRow("CABIN",                  doc.InspectionDetails.Cabin);
                                AddDataRow("DASHBOARD",              doc.InspectionDetails.Dashboard);
                                AddDataRow("ALL GLASSES",            doc.InspectionDetails.WindshieldGlass);
                                AddDataRow("SEATS",                  doc.InspectionDetails.Seats);

                                // ─ LOAD BODY ─
                                AddCategoryHeader("LOAD BODY");
                                AddDataRow("CABIN",                  doc.InspectionDetails.Cabin);
                                AddDataRow("SEATS",                  doc.InspectionDetails.Seats);
                                AddDataRow("PROPELLER SHAFT",        doc.InspectionDetails.PropellerShaft);
                                AddDataRow("BODY CONDITION",         doc.InspectionDetails.BodyCondition);

                                // ─ ELECTRICAL SYSTEM ─
                                AddCategoryHeader("ELECTRICAL SYSTEM");
                                AddDataRow("LIGHTS",                 doc.InspectionDetails.HeadLamps);
                                AddDataRow("BATTERY",                doc.InspectionDetails.BatteryCondition);
                                AddDataRow("WIRING ASSY",            doc.InspectionDetails.ElectricAssembly);

                                // ─ SUSPENSION SYSTEM ─
                                AddCategoryHeader("SUSPENSION SYSTEM");
                                AddDataRow("FRONT",                  doc.InspectionDetails.SuspensionSystem);
                                AddDataRow("AXLES",                  doc.InspectionDetails.PropellerShaft);

                                // ─ OTHER SYSTEMS ─
                                AddCategoryHeader("OTHER SYSTEMS");
                                AddDataRow("AIR CONDITIONER",        doc.InspectionDetails.Intercooler);
                                AddDataRow("PAINT WORK",             doc.InspectionDetails.PaintWork);
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


                            // 5) Images + Footer line + contact info
                            col.Item().PaddingBottom(5).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);

                            // col.Item()
                            //    .Grid(grid =>
                            //    {
                            //        grid.Columns(2);        // two per row
                            //        grid.Spacing(10);

                            //        foreach (var kvp in photoStreams)
                            //        {
                            //            var imageData = kvp.Value;
                            //            var dateText = doc.InspectionDetails.DateOfInspection
                            //                                ?.ToString("d MMM yyyy h:mm:ss tt", CultureInfo.InvariantCulture)
                            //                            ?? "";
                            //            var locText = doc.InspectionDetails.InspectionLocation ?? "";

                            //            grid.Item().Column(c =>
                            //            {
                            //                // fixed‐size box
                            //                c.Item()
                            //                    .Width(160)
                            //                    .Height(120)    // fixed height 4x3
                            //                    .Border(1)
                            //                    .BorderColor(Colors.Blue.Darken2)
                            //                    .Stack(stack =>
                            //                    {
                            //                        // 1) Photo at bottom layer
                            //                        stack.Item()
                            //                            .Image(imageData, ImageScaling.FitArea);

                            //                        // 2) Overlay date+location at top‐left
                            //                        stack.Item()
                            //                            .Padding(5)
                            //                            .Background(Colors.Black.WithAlpha(1))  // semi‐transparent bg
                            //                            .AlignTop()
                            //                            .AlignLeft()
                            //                            .Column(txt =>
                            //                            {
                            //                                txt.Item()
                            //                                    .Text(dateText)
                            //                                    .FontSize(8)
                            //                                    .FontColor(Colors.White);

                            //                                txt.Item()
                            //                                    .Text(locText)
                            //                                    .FontSize(8)
                            //                                    .FontColor(Colors.White);
                            //                            });
                            //                    });

                            //                // caption below
                            //                c.Item()
                            //                    .PaddingTop(5)
                            //                    .AlignCenter()
                            //                    .Text(kvp.Key)
                            //                    .FontSize(9)
                            //                    .SemiBold();
                            //            });
                            //        }
                            //    });

                            // 2g) PHOTOS & MEDIA SECTION
                        col.Item().PaddingTop(5).Text("Photos").Bold();
                        col.Item().Grid(grid =>
                                    {
                                        grid.Columns(3);
                                        grid.Spacing(10);

                                        foreach (var kvp in photoStreams)
                                        {
                                            var imageName = kvp.Key;
                                            var imageData = kvp.Value;

                                            // Look up the CDN URL for this image name
                                            var cdnUrl = doc.PhotoUrls.ContainsKey(imageName)
                                                ? doc.PhotoUrls[imageName]
                                                : string.Empty;

                                            grid.Item().Column(imgCol =>
                                            {
                                                // 1) Thumbnail with hyperlink
                                                imgCol.Item()
                                                    .Hyperlink(cdnUrl)               // <- make the image clickable
                                                    .Width(80)
                                                    .Height(80)
                                                    .Image(imageData, ImageScaling.FitArea);

                                                // 2) Caption with hyperlink as well
                                                imgCol.Item()
                                                    .Hyperlink(cdnUrl)               // <- clicking the text also jumps
                                                    .Text(imageName)
                                                    .FontSize(9)
                                                    .Italic()
                                                    .AlignCenter();
                                            });
                                        }
                                    });


                        });


                        // 3.8) Disclaimer + Icons Footer
                        main.Item().Column(col =>
                        {

                            // Disclaimer
                            col.Item()
                               .Text("THIS REPORT IS ISSUED WITHOUT PREJUDICE")
                               .FontSize(10)
                               .Bold()
                               .AlignCenter();

                            // Thin gray separator
                            // Remove vertical padding entirely:
                            col.Item()
                                .Border(1)
                                .BorderColor(Colors.Grey.Lighten2)
                                .Height(1);

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
            //pdfDoc.ShowInCompanion();

            // 4) (Optional) Generate PDF bytes if you need them:
            return pdfDoc.GeneratePdf();
        }
    }
}
