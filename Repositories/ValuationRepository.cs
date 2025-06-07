using System;
using System.Collections.Generic;
using System.Linq;
using Valuation.Api.Models;

namespace Valuation.Api.Services;
    public class ValuationRepository : IValuationRepository
{
    private readonly List<ValuationDocument> _documents = new()
        {
            new ValuationDocument
            {
                // Top‐level fields
                id = "cd8cb1dd-0343-48b1-9831-183c9d31c46f",
                CompositeKey = "MH02CD5678|9620027500",
                VehicleNumber = "MH02CD5678",
                ApplicantContact = "9620027500",
                VehicleSegment = null,
                Documents = null,   // (or new List<Document>() if that property is never null)

                // ── Stakeholder sub‐object ─────────────────────────────────────────────────
                Stakeholder = new Stakeholder
                {
                    Name = "State Bank of India (SBI)",
                    ExecutiveName = "Mahesh",
                    ExecutiveContact = "1234567890",
                    ExecutiveWhatsapp = "1234567890",
                    ExecutiveEmail = "tiru@live.in",
                    VehicleNumber = null,
                    VehicleSegment = null,

                    Applicant = new Applicant
                    {
                        Name = "Tiru",
                        Contact = "9620027500"
                    },

                    Documents = new List<Document>
                    {
                        new Document
                        {
                            Type = "RC",
                            FilePath = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/rc.pdf",
                            UploadedAt = DateTime.Parse("2025-06-03T09:31:32.5257185Z")
                        },
                        new Document
                        {
                            Type = "Insurance",
                            FilePath = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/insurance.pdf",
                            UploadedAt = DateTime.Parse("2025-06-03T09:31:32.5257192Z")
                        }
                    }
                },

                // ── VehicleDetails sub‐object ─────────────────────────────────────────────────
                VehicleDetails = new VehicleDetailsDto
                {
                    RegistrationNumber = "MH02CD5678",
                    Make = "HONDA MOTORCYCLE AND SCOOTER INDIA (P) LTD",
                    Model = "UNICORN-ELECTRICSUTOSTARTWITHA",
                    MonthOfMfg = 10,
                    YearOfMfg = 2009,
                    BodyType = "Bike",
                    ChassisNumber = "ME4KC1234J57653",
                    EngineNumber = "KC00487G2207",
                    Colour = "BLACK",
                    Fuel = "PETROL",
                    OwnerName = "GITANJALI RAHEJA",
                    PresentAddress = "PLOT NO.330, PHASE-3, KAMALAPURI COLONY, 536-0",
                    PermanentAddress = "PLOT NO.330, PHASE-3, KAMALAPURI COLONY, 536-0",
                    Hypothecation = false,
                    Insurer = "",
                    DateOfRegistration = DateTime.Parse("2009-10-30T00:00:00"),
                    ClassOfVehicle = "M-Cycle/Scooter(2WN)",
                    EngineCC = 149,
                    GrossVehicleWeight = 316,
                    OwnerSerialNo = null,
                    SeatingCapacity = 2,
                    InsurancePolicyNo = "",
                    InsuranceValidUpTo = DateTime.Parse("1800-01-01T00:00:00"),
                    IDV = null,
                    PermitNo = null,
                    PermitValidUpTo = null,
                    FitnessNo = null,
                    FitnessValidTo = null,
                    BacklistStatus = false,
                    RcStatus = false,
                    StencilTraceUrl = null,
                    ChassisNoPhotoUrl = null,
                    Odometer = null,
                    StencilTrace = null,
                    ChassisNoPhoto = null,
                    Documents = new List<Document>(),  // or empty list
                    Rto = "RTA-HYDERABAD-CZ, TELANGANA",
                    Lender = "",
                    ExShowroomPrice = null,
                    CategoryCode = "2WN",
                    NormsType = "Not Available",
                    MakerVariant = null,
                    PollutionCertificateNumber = "",
                    PollutionCertificateUpto = DateTime.Parse("1800-01-01T00:00:00"),
                    PermitType = "",
                    PermitIssued = DateTime.Parse("1800-01-01T00:00:00"),
                    PermitFrom = DateTime.Parse("1800-01-01T00:00:00"),
                    TaxUpto = DateTime.Parse("1800-01-01T00:00:00"),
                    TaxPaidUpto = "LTT",
                    ManufacturedDate = DateTime.Parse("2009-09-01T00:00:00")
                },

                // ── Audit fields ───────────────────────────────────────────────────────────────
                CreatedAt = DateTime.Parse("2025-06-03T06:43:54.9441092Z"),
                UpdatedAt = DateTime.Parse("0001-01-01T00:00:00"),

                // ── InspectionDetails sub‐object ───────────────────────────────────────────────
                InspectionDetails = new InspectionDetails
                {
                    VehicleInspectedBy = "Mahesh",
                    DateOfInspection = DateTime.Parse("2025-06-03T00:00:00"),
                    InspectionLocation = "Kakinada",
                    VehicleMoved = true,
                    EngineStarted = true,
                    Odometer = 80000,
                    VinPlate = true,
                    BodyType = "Bike",
                    OverallTyreCondition = "Good",
                    OtherAccessoryFitment = false,
                    WindshieldGlass = "Good",
                    RoadWorthyCondition = true,
                    EngineCondition = "Good",
                    SuspensionSystem = "Good",
                    SteeringAssy = "Good",
                    BrakeSystem = "Good",
                    ChassisCondition = "Good",
                    BodyCondition = "Good",
                    BatteryCondition = "Good",
                    PaintWork = "Good",
                    ClutchSystem = "Good",
                    GearBoxAssy = "Good",
                    PropellerShaft = "Good",
                    DifferentialAssy = "Good",
                    Cabin = "Good",
                    Dashboard = "Good",
                    Seats = "Good",
                    HeadLamps = "Good",
                    ElectricAssembly = "Good",
                    Radiator = "Good",
                    Intercooler = "Good",
                    AllHosePipes = "Good",
                    Photos = new List<string>()
                },

                // ── QualityControl sub‐object ──────────────────────────────────────────────────
                QualityControl = new QualityControl
                {
                    OverallRating = "Good",
                    ValuationAmount = 40000,
                    ChassisPunch = "Good",
                    Remarks = "Vehicle is found in good road worthy condition.2.Vehicle inspected with open chassis(no load body/platform available)"
                },

                // ── ValuationResponse sub‐object ────────────────────────────────────────────────
                ValuationResponse = new ValuationResponse
                {
                    RawResponse = "Based on the web-search snippets provided, here are the estimated INR price ranges for the vehicle:\n\n" +
                                  "1. Low: ₹6 L – ₹6.5 L\n" +
                                  "2. Mid: ₹6.5 L – ₹7 L\n" +
                                  "3. High: ₹7 L – ₹7.5 L\n\n" +
                                  "Rationale:\n" +
                                  "- Low: The lower end of the range is based on the lowest price mentioned in the search results, indicating the minimum price point for this vehicle in the market.\n" +
                                  "- Mid: The mid-range is set between the low and high ends, considering the average prices mentioned in the search results and the general market value of the vehicle.\n" +
                                  "- High: The higher end of the range is determined by the highest price quoted in the search results, suggesting the maximum price range for this vehicle in the current market.",
                    LowRange = 6,
                    MidRange = 6,
                    HighRange = 7
                },

                // ── PhotoUrls as a Dictionary<string,string> ───────────────────────────────────
                PhotoUrls = new Dictionary<string, string>
                {
                    ["FrontLeftSide"]       = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/817b7aa4-f41d-4438-919f-c51aac037dfc-1001089080.jpg",
                    ["FrontRightSide"]      = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/cb584fd0-95e7-4ee0-9910-4bd258364022-disney.jpg",
                    ["RearLeftSide"]        = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/06f35a40-0a3b-4fae-a8b0-3833a3238081-disney.jpg",
                    ["RearRightSide"]       = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/dd18e136-7ea6-4bf2-927d-3d12aaf99601-hq720.png",
                    ["FrontViewGrille"]     = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/c3105805-e8e3-4cab-9644-0da5dd987da4-hq720.png",
                    ["RearViewTailgate"]    = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/03db0d62-f24d-4983-ba5b-8e52f73da98f-disney.jpg",
                    ["DriverSideProfile"]   = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/872e4d93-5d92-42c5-839f-920311b337eb-hq720.png",
                    ["PassengerSideProfile"]= "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/9c402ce9-4c15-4864-b233-e486bd052761-disney.jpg",
                    ["Dashboard"]           = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/817b7aa4-f41d-4438-919f-c51aac037dfc-1001089080.jpg",
                    ["InstrumentCluster"]   = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/b8322d0c-6b45-4661-9c93-8e08110ff2ac-1000174684.jpg",
                    ["EngineBay"]           = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/1b6ad6d9-1e86-465c-8ddf-c5eaddb55986-1067_belleandarielcoloringpagesonly.com.png",
                    ["ChassisNumberPlate"]  = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/430b4405-67ab-4c92-9b26-216a297e2aad-1000174712.jpg",
                    ["ChassisImprint"]      = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/71735efe-d5c4-4621-91bf-e69101c3e2fb-1000174710.jpg",
                    ["GearAndSeats"]        = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/9f858ce3-d0c1-4f4e-b303-87d932e4e90c-hq720.png",
                    ["DashboardCloseup"]    = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/39fcc085-3848-45cf-97eb-b3d90a4c2ae2-1000130783.jpg",
                    ["Odometer"]            = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/a916f959-55ad-490e-9621-5455a4390138-1000179456.jpg",
                    ["SelfieWithVehicle"]   = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/e488cb7f-1d50-452e-9eef-80649df2fe71-1000180487.jpg",
                    ["Underbody"]           = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/49f3f0e7-6356-4f8d-94aa-1a6b88789710-1000183180.jpg",
                    ["TiresAndRims"]        = "https://prontomoto.azureedge.net/documents/MH02CD5678/9620027500/ad344a60-f7d5-452e-a312-d835a39522d7-1000183773.jpg"
                },

                // ── Workflow steps ───────────────────────────────────────────────────────────
                Workflow = new List<WorkflowStep>
                {
                    new WorkflowStep
                    {
                        StepOrder = 1,
                        TemplateStepId = 1,
                        AssignedToRole = "Stakeholder",
                        Status = "Completed",
                        StartedAt = null,
                        CompletedAt = DateTime.Parse("2025-06-03T09:31:32.8498628Z")
                    },
                    new WorkflowStep
                    {
                        StepOrder = 2,
                        TemplateStepId = 2,
                        AssignedToRole = "BackEnd",
                        Status = "Completed",
                        StartedAt = DateTime.Parse("2025-06-03T09:31:33.1846268Z"),
                        CompletedAt = DateTime.Parse("2025-06-03T09:33:11.6402939Z")
                    },
                    new WorkflowStep
                    {
                        StepOrder = 3,
                        TemplateStepId = 3,
                        AssignedToRole = "AVO",
                        Status = "Completed",
                        StartedAt = DateTime.Parse("2025-06-03T09:33:12.0017738Z"),
                        CompletedAt = DateTime.Parse("2025-06-03T10:17:15.9095214Z")
                    },
                    new WorkflowStep
                    {
                        StepOrder = 4,
                        TemplateStepId = 4,
                        AssignedToRole = "QC",
                        Status = "Completed",
                        StartedAt = DateTime.Parse("2025-06-03T10:17:16.5067358Z"),
                        CompletedAt = DateTime.Parse("2025-06-03T10:18:53.8825017Z")
                    },
                    new WorkflowStep
                    {
                        StepOrder = 5,
                        TemplateStepId = 5,
                        AssignedToRole = "FinalReport",
                        Status = "InProgress",
                        StartedAt = DateTime.Parse("2025-06-03T10:18:54.1945596Z"),
                        CompletedAt = null
                    }
                },

                // ── Final metadata ───────────────────────────────────────────────────────────
                Status = "Open",
                CreatedBy = null,
                UpdatedBy = null,
                DeletedAt = null,
                DeletedBy = null,
                CompletedAt = null,
                CompletedBy = null,
                AssignedTo = null
            }
            // ← You can add more ValuationDocument entries to _documents if needed
        };

    // Retrieve a single document by its ID
    public ValuationDocument GetValuation(string id) =>
        _documents.FirstOrDefault(v => v.id == id);

    // Retrieve all documents
    public IEnumerable<ValuationDocument> GetAll() => _documents;
}

