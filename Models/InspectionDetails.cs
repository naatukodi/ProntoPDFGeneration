// Models/InspectionDetails.cs
namespace Valuation.Api.Models
{
    public class InspectionDetails
    {
        // --- Original fields ---
        public string VehicleInspectedBy { get; set; } = default!;
        public DateTime? DateOfInspection { get; set; }
        public string? InspectionLocation { get; set; }
        public bool? VehicleMoved { get; set; }
        public bool? EngineStarted { get; set; }
        public long? Odometer { get; set; }
        public bool? VinPlate { get; set; }
        public string? BodyType { get; set; }
        public string? OverallTyreCondition { get; set; }
        public bool? OtherAccessoryFitment { get; set; }
        public string? WindshieldGlass { get; set; }
        public bool? RoadWorthyCondition { get; set; }

        // Basis systems
        public string? EngineCondition { get; set; }
        public string? SuspensionSystem { get; set; }
        public string? SteeringAssy { get; set; }
        public string? BrakeSystem { get; set; }
        public string? ChassisCondition { get; set; }
        public string? BodyCondition { get; set; }
        public string? BatteryCondition { get; set; }
        public string? PaintWork { get; set; }

        // Transmission
        public string? ClutchSystem { get; set; }
        public string? GearBoxAssy { get; set; }
        public string? PropellerShaft { get; set; }
        public string? DifferentialAssy { get; set; }

        // Cabin
        public string? Cabin { get; set; }
        public string? Dashboard { get; set; }
        public string? Seats { get; set; }

        // Electrical
        public string? HeadLamps { get; set; }
        public string? ElectricAssembly { get; set; }

        // Cooling
        public string? Radiator { get; set; }
        public string? Intercooler { get; set; }
        public string? AllHosePipes { get; set; }

        // Photo URLs
        public List<string>? Photos { get; set; }

        // --- Additional fields (synced with backend) ---
        public string? FuelSystem { get; set; }
        public string? ExteriorCondition { get; set; }
        public string? InteriorCondition { get; set; }
        public string? DriveShafts { get; set; }
        public string? SteeringSystem { get; set; }
        public string? SteeringWheel { get; set; }
        public string? SteeringColumn { get; set; }
        public string? SteeringBox { get; set; }
        public string? SteeringLinkages { get; set; }
        public string? SteeringHandle { get; set; }
        public string? FrontForkAssy { get; set; }
        public string? Bonnet { get; set; }
        public string? Bumpers { get; set; }
        public string? Doors { get; set; }
        public string? Fenders { get; set; }
        public string? Mudguards { get; set; }
        public string? AllGlasses { get; set; }
        public string? FrontFairing { get; set; }
        public string? RearCowls { get; set; }
        public string? Boom { get; set; }
        public string? Bucket { get; set; }
        public string? ChainTrack { get; set; }
        public string? HydraulicCylinders { get; set; }
        public string? SwingUnit { get; set; }
        public string? Upholstery { get; set; }
        public string? InteriorTrims { get; set; }
        public string? Front { get; set; }
        public string? Rear { get; set; }
        public string? SpeedoMeter { get; set; }
        public string? Axles { get; set; }
        public string? FrontAxles { get; set; }
        public string? RearAxles { get; set; }
        public string? AirConditioner { get; set; }
        public string? Audio { get; set; }
        public string? RightSideWing { get; set; }
        public string? LeftSideWing { get; set; }
        public string? TailGate { get; set; }
        public string? LoadFloor { get; set; }

        // Brakes additional
        public string? ParkingBrake { get; set; }
        public string? Abs { get; set; }

        // Electrical additional
        public string? TailLightsIndicators { get; set; }
        public string? WiringAssy { get; set; }

        // Crash guards
        public string? FrontCrashGuard { get; set; }
        public string? RearCrashGuard { get; set; }

        // 4W specific
        public string? AirBags { get; set; }
        public string? SunRoof { get; set; }
        public string? SideFenders { get; set; }

        // CV specific
        public string? HydraulicLift { get; set; }
        public string? SideUnderRunProtection { get; set; }

        // 2W specific
        public string? MainStand { get; set; }
        public string? SideStand { get; set; }
        public string? FrontMudGuard { get; set; }
        public string? RearMudGuard { get; set; }
        public string? FuelTankCondition { get; set; }
        public string? ChainSprocket { get; set; }
        public string? FrontBrakeCondition { get; set; }
        public string? RearBrakeCondition { get; set; }
        public string? HeadLight { get; set; }
        public string? TailLight { get; set; }
        public string? Indicators { get; set; }
        public string? HornCondition { get; set; }
        public string? MirrorCondition { get; set; }
        public string? SeatCondition { get; set; }
        public string? HandleBarGrips { get; set; }
        public string? FootRest { get; set; }
        public string? AlloyWheelRim { get; set; }

        // CE specific
        public string? Retarder { get; set; }
        public string? DifferentialLock { get; set; }
        public string? Pto { get; set; }
        public string? HydraulicSystem { get; set; }
        public string? BoomArm { get; set; }
        public string? BucketCondition { get; set; }
        public string? BladeCondition { get; set; }
        public string? LiftingCapacity { get; set; }
        public string? TyreConditionCe { get; set; }
        public string? UnderCarriage { get; set; }
        public string? CrawlerTracks { get; set; }
        public string? SteelRims { get; set; }
        public string? AttachmentCondition { get; set; }
        public string? CabCondition { get; set; }
        public string? CounterWeight { get; set; }
        public string? RockBreaker { get; set; }

        // BUS specific
        public string? CoachCondition { get; set; }
        public string? PassengerSeats { get; set; }
        public string? EmergencyExits { get; set; }
        public string? LuggageCompartment { get; set; }
        public string? AcSystem { get; set; }
        public string? DestinationBoard { get; set; }
        public string? SideMirrors { get; set; }

        // FE specific
        public string? RightIndividualBrakes { get; set; }
        public string? LeftIndividualBrakes { get; set; }
        public string? ThreePointLinkage { get; set; }
        public string? PowerTakeOff { get; set; }
        public string? HitchSystem { get; set; }
        public string? HydraulicLiftFe { get; set; }
        public string? FrontWeights { get; set; }
        public string? RearWeights { get; set; }
        public string? RopsCanopy { get; set; }
        public string? FrontTyreCondition { get; set; }
        public string? RearTyreCondition { get; set; }
        public string? ImplementAttachments { get; set; }
        public string? FuelTankFe { get; set; }
        public string? FrontAxleFe { get; set; }
        public string? RearDrawbar { get; set; }

        public string? ChassisVerificationPhotoUrl { get; set; }
        public string? ChassisStencilTracePhotoUrl { get; set; }

        // --- Excel-registry aligned fields ---
        public string? TyreCondition { get; set; }
        public string? ElectricalSystem { get; set; }
        public string? LoadBodyAssy { get; set; }
        public string? BodyAssy { get; set; }
        public string? CabinAssy { get; set; }
        public string? FrontBrakes { get; set; }
        public string? RearBrakes { get; set; }
        public string? HeadLights { get; set; }
        public string? FrontSuspension { get; set; }
        public string? RearSuspension { get; set; }
        public string? RightSideGate { get; set; }
        public string? LeftSideGate { get; set; }

        // 2W specific (registry)
        public string? FrontScoop { get; set; }
        public string? RvMirrors { get; set; }
        public string? LockSet { get; set; }
        public string? SideCovers { get; set; }
        public string? BellyPanels { get; set; }
        public string? BrakeLeversFluid { get; set; }
        public string? Silencer { get; set; }
        public string? SilencerCover { get; set; }
        public string? Accelerator { get; set; }
        public string? HandleBar { get; set; }
        public string? SteeringStem { get; set; }
        public string? FrontShockAbsorber { get; set; }
        public string? RearShockAbsorber { get; set; }
        public string? LegGuard { get; set; }
        public string? SareeGuard { get; set; }
        public string? ChainGuard { get; set; }
        public string? SelfStart { get; set; }
        public string? Horn { get; set; }
        public string? KickPedalFootRest { get; set; }

        // 3W specific (registry)
        public string? FrontPanel { get; set; }
        public string? FrontGlassFrame { get; set; }
        public string? Switches { get; set; }
        public string? LoadCarrier { get; set; }

        // CE specific (registry)
        public string? SteeringControlSystem { get; set; }
        public string? CabinStructure { get; set; }
        public string? DashboardControls { get; set; }
        public string? GlassPanels { get; set; }
        public string? BucketBlade { get; set; }
        public string? PinsAndBushes { get; set; }
        public string? ServiceBrake { get; set; }
        public string? EmergencyStop { get; set; }
        public string? Sensors { get; set; }
        public string? SteeringControlLevers { get; set; }
        public string? HydraulicSteeringPump { get; set; }
        public string? SwivelJoints { get; set; }
        public string? HydraulicOilCooler { get; set; }
        public string? HydraulicPump { get; set; }
        public string? HosesAndFittings { get; set; }
        public string? SwingMechanism { get; set; }
        public string? TrackChains { get; set; }
        public string? Sprockets { get; set; }
        public string? Rollers { get; set; }
        public string? HourMeter { get; set; }
        public string? BonnetGuard { get; set; }
        public string? TorqueConverter { get; set; }
        public string? FinalDrive { get; set; }

        // BUS specific (registry)
        public string? BodyStructure { get; set; }
        public string? DriverCabin { get; set; }
        public string? BumpersAndGrilles { get; set; }
        public string? SeatsAndBerths { get; set; }
        public string? SideBodyPanels { get; set; }
        public string? RearBodyPanels { get; set; }

        // FE/Tractor specific (registry)
        public string? OperatorPlatform { get; set; }
        public string? OperatorStation { get; set; }
        public string? Canopy { get; set; }
        public string? FrontGrilles { get; set; }
        public string? BrakeEqualization { get; set; }
        public string? FanAssy { get; set; }
        public string? RearAxleFe { get; set; }
        public string? TieRodsJoints { get; set; }
        public string? Muffler { get; set; }
        public string? AirFilter { get; set; }
        public string? DropArm { get; set; }
        public string? AttachmentHitch { get; set; }

        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? AssignedTo { get; set; }
        public string? AssignedToPhoneNumber { get; set; }
        public string? AssignedToEmail { get; set; }
        public string? AssignedToWhatsapp { get; set; }
        public string? Remarks { get; set; }
    }
}
