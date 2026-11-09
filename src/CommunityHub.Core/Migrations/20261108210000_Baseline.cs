using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActorParticipantId = table.Column<int>(type: "int", nullable: true),
                    ActorEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ActorRole = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActingAs = table.Column<bool>(type: "bit", nullable: false),
                    OnBehalfOf = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TargetType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TargetId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    HttpMethod = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CouponSignupRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OrderType = table.Column<int>(type: "int", nullable: false),
                    TicketType = table.Column<int>(type: "int", nullable: false),
                    TicketCount = table.Column<int>(type: "int", nullable: false),
                    VatNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CountryIso2 = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    RequesterName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequesterEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    RequesterMobile = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    InvoiceEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    EanNumber = table.Column<string>(type: "nvarchar(13)", maxLength: 13, nullable: true),
                    SplitPercent = table.Column<int>(type: "int", nullable: false),
                    PurchaseOrder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErpCustomerNumber = table.Column<int>(type: "int", nullable: true),
                    ErpCustomerCreated = table.Column<bool>(type: "bit", nullable: false),
                    ErpError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CouponInvoicingSettingId = table.Column<int>(type: "int", nullable: true),
                    MailSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequestIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponSignupRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FriendlyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocLibrarySettingChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocLibrarySettingChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocLibrarySettingOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocLibrarySettingOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EconomicContactAnnotations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerNumber = table.Column<int>(type: "int", nullable: false),
                    ContactNumber = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EconomicContactAnnotations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ToEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ActualToEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CcEmails = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    RecipientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(998)", maxLength: 998, nullable: false),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Dropped = table.Column<bool>(type: "bit", nullable: false),
                    TemplateName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    LastRetryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResendDismissedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResendDismissedByEmail = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EngineAlertThrottles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ThrottleKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSubject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EngineAlertThrottles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErpCustomerLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ErpCustomerNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Cvr = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CvrValid = table.Column<bool>(type: "bit", nullable: false),
                    CvrValidationReason = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CvrOfflineOnly = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpCustomerLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErpOrderLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WebshopOrderId = table.Column<long>(type: "bigint", nullable: false),
                    ErpOrderNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    FxRateApplied = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    CurrencyCheckResult = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErpOrderLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CommunityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PreDayDate = table.Column<DateOnly>(type: "date", nullable: true),
                    KioskCapturePolicy = table.Column<int>(type: "int", nullable: true),
                    QrCapturePolicy = table.Column<int>(type: "int", nullable: true),
                    KioskRatingSymbols = table.Column<int>(type: "int", nullable: true),
                    QrRatingSymbols = table.Column<int>(type: "int", nullable: true),
                    VenueName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    HubHostname = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LockDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CalendarSyncEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    AutoCalendarInvitesEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobHealthMarkers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobKey = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ConsecutiveNoOps = table.Column<int>(type: "int", nullable: false),
                    LastNoOpReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobHealthMarkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobRunStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FunctionName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    MinIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastThrottledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ThrottledCount = table.Column<int>(type: "int", nullable: false),
                    LastContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastContentMailedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRunStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkedInOAuthTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    AccessToken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RefreshToken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConnectedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubjectUrn = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkedInOAuthTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogisticsFileStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    PathKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastMailedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastMailedTo = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Headline = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    WebUrl = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogisticsFileStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogisticsRunSummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    RanAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Published = table.Column<int>(type: "int", nullable: false),
                    Unchanged = table.Column<int>(type: "int", nullable: false),
                    Mailed = table.Column<int>(type: "int", nullable: false),
                    Problems = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Ok = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogisticsRunSummaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopBoothSpots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    BoothId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    XPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    YPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    WidthPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false),
                    HeightPercent = table.Column<decimal>(type: "decimal(7,3)", precision: 7, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopBoothSpots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopCatalogPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Chapter = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    BodyMarkdown = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MediaUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProductGroup = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ShowBoothMap = table.Column<bool>(type: "bit", nullable: false),
                    ProductMatch = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AvailabilityView = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false, defaultValue: "None"),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopCatalogPages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopJoinRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    MatchedBy = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Approved = table.Column<bool>(type: "bit", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopJoinRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopKnownContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IsSigner = table.Column<bool>(type: "bit", nullable: false),
                    IsEventCoordinator = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopKnownContacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ShopRegistrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SessionTokenHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SponsorApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IssuedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SponsorLeadNotificationPrefs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Recipients = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Cadence = table.Column<int>(type: "int", nullable: false),
                    SkipJunk = table.Column<bool>(type: "bit", nullable: false),
                    LastDeltaSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorLeadNotificationPrefs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SponsorLeads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ZohoRecordId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LeadKind = table.Column<int>(type: "int", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Company = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CaptureMethod = table.Column<int>(type: "int", nullable: false),
                    CapturedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusChangedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AiScreenScore = table.Column<int>(type: "int", nullable: true),
                    AiScreenLabel = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    AiScreenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastReplyAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastReplyByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorLeads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SponsorTokenVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    BumpedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BumpedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorTokenVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SurveyResponses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurveySlug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SelectedTrackId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyResponses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SurveyStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurveySlug = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    IsOpen = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebshopOrderArchives",
                columns: table => new
                {
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BillingCompany = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BillingEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebshopOrderArchives", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "WordPressRefreshPosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    WordPressPostId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BodyTemplate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RefreshWeekly = table.Column<bool>(type: "bit", nullable: false),
                    LastRefreshedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRefreshResult = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastPushedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordPressRefreshPosts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ZohoTokenLeases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CredentialKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccessToken = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetryNotBeforeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RefreshLeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RefreshCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZohoTokenLeases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgendaActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    BackstageSessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Room = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Track = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ActivityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Speakers = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DayIndex = table.Column<int>(type: "int", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgendaActivities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgendaActivities_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppGameParticipations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    GiftDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    GiftConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReminderLastSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppGameParticipations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppGameParticipations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttendeeMonitors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UsageReportSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCapRequestAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCapRequestedTickets = table.Column<int>(type: "int", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ViewCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendeeMonitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendeeMonitors_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Section = table.Column<int>(type: "int", nullable: false),
                    OverrideJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigOverrides_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CouponInvoicingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CouponName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BillingType = table.Column<int>(type: "int", nullable: false),
                    ErpCustomerNumber = table.Column<int>(type: "int", nullable: true),
                    RequesterContactNumber = table.Column<int>(type: "int", nullable: true),
                    RequesterName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FirstSeenClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAlertedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastInvoicedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InvoiceIntervalDays = table.Column<int>(type: "int", nullable: true),
                    InvoicedSharePercent = table.Column<int>(type: "int", nullable: true),
                    AgreedUnitPriceDkk = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    ClaimCapTickets = table.Column<int>(type: "int", nullable: true),
                    LastCapAlertAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCapAlertRemaining = table.Column<int>(type: "int", nullable: true),
                    SendClaimInviteMail = table.Column<bool>(type: "bit", nullable: false),
                    IssueUsageLink = table.Column<bool>(type: "bit", nullable: false),
                    SendUsageStatusMail = table.Column<bool>(type: "bit", nullable: false),
                    RequestedCapTickets = table.Column<int>(type: "int", nullable: true),
                    RequestedCapAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BackstageCodeConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BackstageCodeConfirmedByEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimInviteSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimInviteSentToEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponInvoicingSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CouponInvoicingSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailReminderCadences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TemplateKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: true),
                    IntervalDays = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailReminderCadences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailReminderCadences_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailTemplateOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TemplateKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OverrideText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplateOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailTemplateOverrides_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailTemplateRings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TemplateKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: true),
                    ReleasedToRing = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplateRings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailTemplateRings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationApiClients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreviousKeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationApiClients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationApiClients_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationPhysicalRooms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    QrCapturePolicy = table.Column<int>(type: "int", nullable: true),
                    QrRatingSymbols = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationPhysicalRooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationPhysicalRooms_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationResponses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SerialNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    CollectionTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeviceRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClockSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TimestampSuspect = table.Column<bool>(type: "bit", nullable: false),
                    FreeText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PositivePoint = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ImprovementPoint = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CapturePolicy = table.Column<int>(type: "int", nullable: true),
                    RatingSymbols = table.Column<int>(type: "int", nullable: true),
                    IsInactive = table.Column<bool>(type: "bit", nullable: false),
                    InactivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationResponses_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationRooms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationRooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationRooms_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventSoMePosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    AllowImportOverwrite = table.Column<bool>(type: "bit", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastOverwrittenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SourceFileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSoMePosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSoMePosts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourceEdition = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ImportBatch = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ImportedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalRecipients_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureGroupSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<int>(type: "int", nullable: false),
                    ReleasedToRing = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureGroupSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureGroupSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    FeatureKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ReleasedToRing = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    ReleasedToRingOverride = table.Column<int>(type: "int", nullable: true),
                    GroupOverride = table.Column<int>(type: "int", nullable: true),
                    ActiveFromForBroadRings = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NotificationRecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeatureSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GraphicsAssetLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    PersonaGroup = table.Column<int>(type: "int", nullable: false),
                    SiteUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DriveName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RootFolderPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    BrowseUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphicsAssetLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphicsAssetLocations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupPhotoSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    IsPreDay = table.Column<bool>(type: "bit", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsBlocked = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPhotoSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupPhotoSlots_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HotelChangeLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    HotelId = table.Column<int>(type: "int", nullable: true),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    ParticipantName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AffectsGuestList = table.Column<bool>(type: "bit", nullable: false),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelChangeLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelChangeLog_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HotelCoverageStandards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Class = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    Nights = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelCoverageStandards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelCoverageStandards_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HotelForecastExtras",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Night = table.Column<DateOnly>(type: "date", nullable: false),
                    ExtraRooms = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelForecastExtras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelForecastExtras_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Hotels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RoomBlockSize = table.Column<int>(type: "int", nullable: true),
                    ConfirmationNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    AgreementNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AgreementLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BookingReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NotifyEmails = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SentToHotelAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentToHotelByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hotels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Hotels_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImpersonationAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ActorKind = table.Column<int>(type: "int", nullable: false),
                    ActorParticipantId = table.Column<int>(type: "int", nullable: true),
                    ActorLabel = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TargetParticipantId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImpersonationAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImpersonationAudits_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailCampaigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TemplateKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Audience = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ScheduledFor = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BatchSize = table.Column<int>(type: "int", nullable: false),
                    BatchIntervalMinutes = table.Column<int>(type: "int", nullable: false),
                    PlanPreviewAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlanWaveAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlanEveryoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlanPreviewDoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlanWaveDoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlanEveryoneDoneAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlanWavePercent = table.Column<int>(type: "int", nullable: false),
                    PlanWaveSize = table.Column<int>(type: "int", nullable: true),
                    PlanStoppedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlanStoppedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    PlanSetByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    IncludeUnsubscribeLink = table.Column<bool>(type: "bit", nullable: false),
                    DryRunAcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DryRunAcknowledgedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DryRunRecipientCount = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBatchAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    SuppressedCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailCampaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailCampaigns_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailSuppressions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailSuppressions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailSuppressions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MasterClassSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OfferHoldHours = table.Column<int>(type: "int", nullable: false),
                    PromotionMode = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    BackstageOrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BuyerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CountryCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    City = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Postcode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    TaxId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OrderStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    SourceCreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RawJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MirrorState = table.Column<int>(type: "int", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.UniqueConstraint("AK_Orders_EventId_BackstageOrderId", x => new { x.EventId, x.BackstageOrderId });
                    table.ForeignKey(
                        name: "FK_Orders_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Quizzes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Topic = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    QuestionsPerAttempt = table.Column<int>(type: "int", nullable: false),
                    PerQuestionSeconds = table.Column<int>(type: "int", nullable: false),
                    BasePoints = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quizzes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quizzes_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AllDay = table.Column<bool>(type: "bit", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Roles = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleEntries_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SentReminders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ReminderType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccasionKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentReminders_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionizeEndpointSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    EndpointId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    View = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    EndpointLastChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PreviousEndpointId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PendingChangeMode = table.Column<int>(type: "int", nullable: false),
                    ChangeModeChosenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionizeEndpointSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionizeEndpointSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionizeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Abstract = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Room = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Track = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsCommonForAllTracks = table.Column<bool>(type: "bit", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LevelCode = table.Column<int>(type: "int", nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LengthMinutes = table.Column<int>(type: "int", nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    TypeIsManualOverride = table.Column<bool>(type: "bit", nullable: false),
                    Length = table.Column<int>(type: "int", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDateOverridden = table.Column<bool>(type: "bit", nullable: false),
                    IsServiceSession = table.Column<bool>(type: "bit", nullable: false),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false),
                    ExcludeFromSoMeAnnouncements = table.Column<bool>(type: "bit", nullable: false),
                    SoMeSingleSpeakerConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    SoMeTextEligible = table.Column<bool>(type: "bit", nullable: true),
                    SoMeTextEligibleHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SoMeTextEligibleCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SoMeTextEligibleReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsHubAdded = table.Column<bool>(type: "bit", nullable: false),
                    UsedForTesting = table.Column<bool>(type: "bit", nullable: false),
                    PublicToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RoomQrUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RoomQrGeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EvaluationFormUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EvaluationEmailedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublicSlug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LogisticsText = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    LogisticsUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LogisticsUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    PrepContent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PrepUpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PrepUpdatedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    MasterClassCapacity = table.Column<int>(type: "int", nullable: true),
                    BackstageSessionId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SponsorSlotKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ZohoChangeNotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ZohoChangeNotifiedHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SessionizeDeviationNotifiedHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BackstageStartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BackstageEndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BackstageRoom = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BackstageChangeCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSessionizeImportAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionSourceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SyncDirection = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    SpeakerSyncDirection = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    SpeakerScheduleNoticeFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSourceSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSourceSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    WooCategoryId = table.Column<long>(type: "bigint", nullable: true),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    GroupSetByOrganizer = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopCategories_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopCategories_ShopCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "ShopCategories",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopCompanies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PublicName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Cvr = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Postcode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CountryIso2 = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    CompanyEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InvoiceEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LinkedInUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TwitterUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    VatZone = table.Column<int>(type: "int", nullable: false),
                    ErpCustomerNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ErpError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    VatCheck = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErpRoleWarning = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RegisteredByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    BecameSponsorAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BillingReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopCompanies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopCompanies_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PurchaseOrderRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SignerParticipantId = table.Column<int>(type: "int", nullable: true),
                    CoordinatorParticipantId = table.Column<int>(type: "int", nullable: true),
                    SpecialTerms = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    InvoiceNotBefore = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentDays = table.Column<int>(type: "int", nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    VatRatePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentForSignatureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SignedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SignedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SignedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    SignatureKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SignatureImage = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    SignedFromIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ContractPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContractItemId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OnboardedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SwagCreditEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ErpInvoiceReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ErpDraftInvoiceNumber = table.Column<int>(type: "int", nullable: true),
                    InvoicedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InvoiceProblem = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReservationRequesterName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReservationRequesterEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReservationDeadline = table.Column<DateOnly>(type: "date", nullable: true),
                    ReservationReminderWeeks = table.Column<int>(type: "int", nullable: false),
                    ReservationRemindFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    ReservationNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReservationRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReservationRequestedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    ExtensionRequestedDeadline = table.Column<DateOnly>(type: "date", nullable: true),
                    ExtensionRequestReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ExtensionRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExtensionRequestedByParticipantId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopOrders_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignageSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    PortraitToken = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LandscapeToken = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TokensRotatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PortraitNowEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PortraitNextEnabled = table.Column<bool>(type: "bit", nullable: false),
                    PortraitFeedbackEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LandscapeNowEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LandscapeNextEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LandscapeFeedbackEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DailyFromLocal = table.Column<TimeOnly>(type: "time", nullable: true),
                    DailyToLocal = table.Column<TimeOnly>(type: "time", nullable: true),
                    ActiveFromLocal = table.Column<DateOnly>(type: "date", nullable: true),
                    ActiveToLocal = table.Column<DateOnly>(type: "date", nullable: true),
                    RotateSeconds = table.Column<int>(type: "int", nullable: false),
                    PageSeconds = table.Column<int>(type: "int", nullable: false),
                    PortraitColumns = table.Column<int>(type: "int", nullable: false),
                    PortraitRows = table.Column<int>(type: "int", nullable: false),
                    LandscapeColumns = table.Column<int>(type: "int", nullable: false),
                    LandscapeRows = table.Column<int>(type: "int", nullable: false),
                    PortraitGap = table.Column<int>(type: "int", nullable: false),
                    LandscapeGap = table.Column<int>(type: "int", nullable: false),
                    PortraitRowGap = table.Column<int>(type: "int", nullable: false),
                    LandscapeRowGap = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignageSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignageSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoMeBodySamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    SourceFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoMeBodySamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoMeBodySamples_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoMeCadenceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Occurrences = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoMeCadenceSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoMeCadenceSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoMeCategoryRounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    RoundNumber = table.Column<int>(type: "int", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PublishBy = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoMeCategoryRounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoMeCategoryRounds_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoMeCategoryRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Rounds = table.Column<int>(type: "int", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    MinDaysBetweenRounds = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoMeCategoryRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoMeCategoryRules_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoMeSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CompanyPageUrlOrOrgId = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SpeakerPreAlertOrganizerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    NotificationEmails = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    NotifyOnPublish = table.Column<bool>(type: "bit", nullable: false),
                    MaxPostsPerDay = table.Column<int>(type: "int", nullable: false),
                    AutoApproveEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AutoApproveLeadDays = table.Column<int>(type: "int", nullable: false),
                    ExcludedSessionTitlePatterns = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExceptionPostsPerDay = table.Column<int>(type: "int", nullable: false),
                    MaxSponsorPostsPerDay = table.Column<int>(type: "int", nullable: true),
                    SpeakerAnnouncementFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    MasterClassAnnouncementFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    SponsorAnnouncementFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    SponsorCategoryRound1From = table.Column<DateOnly>(type: "date", nullable: true),
                    SponsorCategoryRound2From = table.Column<DateOnly>(type: "date", nullable: true),
                    SpeakerTracksRound2From = table.Column<DateOnly>(type: "date", nullable: true),
                    SponsorRound2From = table.Column<DateOnly>(type: "date", nullable: true),
                    SpeakerTracksRound3From = table.Column<DateOnly>(type: "date", nullable: true),
                    SessionAnnouncementFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    EventPostWindowEndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CallForSpeakersClosesOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EventSystemUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EventTags = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrganizerCredits = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActionCatalog = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EventVenueCityCountry = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoMeSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoMeSettings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoMeTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoMeTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoMeTemplates_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorBoothMaterials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorBoothMaterials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorBoothMaterials_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorBoothMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    SyncedToZoho = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorBoothMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorBoothMembers_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZohoSponsorId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ZohoSponsorLinksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZohoSponsorCategoriesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZohoUnconfirmedPushesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZohoDeadLinkStrikesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZohoExhibitorId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Tier = table.Column<int>(type: "int", nullable: false),
                    BoothLabel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SponsorPackage = table.Column<int>(type: "int", nullable: false),
                    HasCurrentBoothOrder = table.Column<bool>(type: "bit", nullable: false),
                    IsSponsor = table.Column<bool>(type: "bit", nullable: false),
                    IsExhibitor = table.Column<bool>(type: "bit", nullable: false),
                    WebsiteUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LinkedInUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LinkedInOrganizationId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SoMeTagContacts = table.Column<bool>(type: "bit", nullable: false),
                    LinkedInOrganizationLookedUpFrom = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LinkedInOrganizationCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TwitterUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    EventCoordinatorFirstName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    EventCoordinatorLastName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    EventCoordinatorCompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EventCoordinatorEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    EventCoordinatorPhone = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ZohoContactEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ZohoSocialPushedHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ZohoSponsorProfilePushedHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LogoVectorPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LogoVectorFileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LogoRasterPath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LogoRasterFileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyDescription = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompanyDescriptionShort = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    SocialMediaIntro = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: true),
                    Ring = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    IsTestData = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    BoothCheckInSlot = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BoothCheckInSetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BoothCheckInSetByEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BoothCheckInMemberCount = table.Column<int>(type: "int", nullable: true),
                    HasSponsorSession = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorInfos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorInfos_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Abstract = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Track = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SyncedToZohoAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExcludeFromSoMeAnnouncements = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorSessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorUploadAudits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    WebUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UploadedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ArtefactMissingAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorUploadAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorUploadAudits_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SponsorUploadLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FolderKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Subfolder = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    FolderPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    EditLinkUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    NotifyEmailsCsv = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    NotifySubject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorUploadLocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorUploadLocations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SwagCatalogCredits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WooCouponId = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UsedOnShopOrderId = table.Column<int>(type: "int", nullable: true),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwagCatalogCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwagCatalogCredits_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SwagCatalogHolds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedByEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReleasedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExpiryNoticeSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwagCatalogHolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwagCatalogHolds_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncDeltas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    EntityType = table.Column<int>(type: "int", nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityLabel = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ChangeKind = table.Column<int>(type: "int", nullable: false),
                    ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDeltas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncDeltas_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastWebhookAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OrdersActive = table.Column<int>(type: "int", nullable: false),
                    OrdersCancelled = table.Column<int>(type: "int", nullable: false),
                    AttendeesActive = table.Column<int>(type: "int", nullable: false),
                    AttendeesCancelled = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncRuns_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TravelReviewLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TravelReimbursementId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelReviewLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelReviewLog_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolumePackageCompanies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CustomName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Domains = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CouponCodes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    LinkedEmails = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ErpCustomerNumbers = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ApprovedKeynoteMention = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedSocialMediaAnnouncement = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedGroupPhoto = table.Column<bool>(type: "bit", nullable: false),
                    DeclinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApproverEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ApproverName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApproverMobile = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    GroupPhotoContactEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    GroupPhotoContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GroupPhotoContactMobile = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    LinkedInUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LogoWebPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LogoPrintPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    QualifiedNow = table.Column<bool>(type: "bit", nullable: false),
                    LastQualifiedCount = table.Column<int>(type: "int", nullable: false),
                    FirstQualifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BenefitsApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BenefitsApprovedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    WizardToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WizardTokenIssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WizardTokenRevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WizardTokenExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WizardCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WizardInvitedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WizardLastOpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WizardOpenCount = table.Column<int>(type: "int", nullable: false),
                    WizardRemindedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WizardReminderCount = table.Column<int>(type: "int", nullable: false),
                    PostEventMailSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovalRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumePackageCompanies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolumePackageCompanies_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ZohoOrderSyncRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OrderId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    CancelHint = table.Column<bool>(type: "bit", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZohoOrderSyncRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ZohoOrderSyncRequests_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SurveyResponseAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurveyResponseId = table.Column<int>(type: "int", nullable: false),
                    QuestionId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: true),
                    ChoiceIds = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyResponseAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurveyResponseAnswers_SurveyResponses_SurveyResponseId",
                        column: x => x.SurveyResponseId,
                        principalTable: "SurveyResponses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SurveyResponsePicks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SurveyResponseId = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    TopicId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DesiredLevel = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurveyResponsePicks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurveyResponsePicks_SurveyResponses_SurveyResponseId",
                        column: x => x.SurveyResponseId,
                        principalTable: "SurveyResponses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CouponPrepaidAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CouponInvoicingSettingId = table.Column<int>(type: "int", nullable: false),
                    TicketClassId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TicketClassLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LowBalanceThreshold = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastLowBalanceAlertAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastLowBalanceAlertRemaining = table.Column<int>(type: "int", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ClosedReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponPrepaidAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CouponPrepaidAllocations_CouponInvoicingSettings_CouponInvoicingSettingId",
                        column: x => x.CouponInvoicingSettingId,
                        principalTable: "CouponInvoicingSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CouponPrepaidAllocations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EvaluationDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SerialNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreviousKeyHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HealthTelemetryEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RoomId = table.Column<int>(type: "int", nullable: true),
                    MasterClassRoomId = table.Column<int>(type: "int", nullable: true),
                    PhysicalRoomId = table.Column<int>(type: "int", nullable: true),
                    CapturePolicy = table.Column<int>(type: "int", nullable: true),
                    RatingSymbols = table.Column<int>(type: "int", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBatteryPercent = table.Column<int>(type: "int", nullable: true),
                    LastSignalQuality = table.Column<int>(type: "int", nullable: true),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CachedRecordCount = table.Column<int>(type: "int", nullable: true),
                    ClockSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsKiosk = table.Column<bool>(type: "bit", nullable: false),
                    PairingCodeHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PairingCodeExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PairingCodeUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    KioskLastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    KioskEnrolmentTokenHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    KioskEnrolmentIssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationDevices_EvaluationPhysicalRooms_PhysicalRoomId",
                        column: x => x.PhysicalRoomId,
                        principalTable: "EvaluationPhysicalRooms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationDevices_EvaluationRooms_MasterClassRoomId",
                        column: x => x.MasterClassRoomId,
                        principalTable: "EvaluationRooms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationDevices_EvaluationRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "EvaluationRooms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationDevices_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationRoomPlacements",
                columns: table => new
                {
                    PhysicalRoomId = table.Column<int>(type: "int", nullable: false),
                    EvaluationRoomId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationRoomPlacements", x => new { x.PhysicalRoomId, x.EvaluationRoomId });
                    table.ForeignKey(
                        name: "FK_EvaluationRoomPlacements_EvaluationPhysicalRooms_PhysicalRoomId",
                        column: x => x.PhysicalRoomId,
                        principalTable: "EvaluationPhysicalRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvaluationRoomPlacements_EvaluationRooms_EvaluationRoomId",
                        column: x => x.EvaluationRoomId,
                        principalTable: "EvaluationRooms",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EvaluationSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CehSessionId = table.Column<int>(type: "int", nullable: true),
                    RoomId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TrackName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ScheduledStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CollectionWindowOpensAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CollectionWindowClosesAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ScheduledLengthMinutes = table.Column<int>(type: "int", nullable: true),
                    PublishedReportVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PublishedReportAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationSessions_EvaluationRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "EvaluationRooms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationSessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventSoMePostOccurrences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventSoMePostId = table.Column<int>(type: "int", nullable: false),
                    PostDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    GraphicFileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SourcePhotoFileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSoMePostOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSoMePostOccurrences_EventSoMePosts_EventSoMePostId",
                        column: x => x.EventSoMePostId,
                        principalTable: "EventSoMePosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HotelAllotments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    HotelId = table.Column<int>(type: "int", nullable: false),
                    Night = table.Column<DateOnly>(type: "date", nullable: false),
                    RoomsAllotted = table.Column<int>(type: "int", nullable: false),
                    OriginalRooms = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelAllotments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelAllotments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HotelAllotments_Hotels_HotelId",
                        column: x => x.HotelId,
                        principalTable: "Hotels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HotelCutoffs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    HotelId = table.Column<int>(type: "int", nullable: false),
                    CutoffDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReleasePercent = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WarnDaysBefore = table.Column<int>(type: "int", nullable: false, defaultValue: 14),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelCutoffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelCutoffs_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HotelCutoffs_Hotels_HotelId",
                        column: x => x.HotelId,
                        principalTable: "Hotels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Participants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SecondaryEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    AlternateEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ErpContactNumber = table.Column<int>(type: "int", nullable: true),
                    IsSigner = table.Column<bool>(type: "bit", nullable: false),
                    LinkedInPersonUrn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LinkedInPersonUrnStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LinkedInPersonUrnCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LinkedInVanityName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEventCoordinator = table.Column<bool>(type: "bit", nullable: false),
                    IsBoothMember = table.Column<bool>(type: "bit", nullable: false),
                    SamePersonAsId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DeactivatedByOrganizerAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LifecycleState = table.Column<int>(type: "int", nullable: false),
                    QueueSource = table.Column<int>(type: "int", nullable: false),
                    OnboardingCompleted_Bio = table.Column<bool>(type: "bit", nullable: false),
                    OnboardingCompleted_Picture = table.Column<bool>(type: "bit", nullable: false),
                    OnboardingCompleted_Hotel = table.Column<bool>(type: "bit", nullable: false),
                    OnboardingCompleted_Appreciation = table.Column<bool>(type: "bit", nullable: false),
                    OnboardingCompleted_Swag = table.Column<bool>(type: "bit", nullable: false),
                    OnboardingCompleted_BioAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OnboardingCompleted_PictureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OnboardingCompleted_HotelAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OnboardingCompleted_AppreciationAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OnboardingCompleted_SwagAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HotelId = table.Column<int>(type: "int", nullable: true),
                    HotelConfirmationNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsTestUser = table.Column<bool>(type: "bit", nullable: false),
                    NeedsVisa = table.Column<bool>(type: "bit", nullable: false),
                    Ring = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CalendarFeedToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WelcomeShownAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WelcomeWithLoginSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Participants_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Participants_Hotels_HotelId",
                        column: x => x.HotelId,
                        principalTable: "Hotels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Participants_Participants_SamePersonAsId",
                        column: x => x.SamePersonAsId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailCampaignRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MailCampaignId = table.Column<int>(type: "int", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailCampaignRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailCampaignRecipients_MailCampaigns_MailCampaignId",
                        column: x => x.MailCampaignId,
                        principalTable: "MailCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Attendees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    BackstageTicketId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    PreviousEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReassignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TicketStatus = table.Column<int>(type: "int", nullable: false),
                    TicketClassName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TicketClassId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MirrorState = table.Column<int>(type: "int", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OrderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Country = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CountryCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    City = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Postcode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TaxId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BookingStatus = table.Column<int>(type: "int", nullable: false),
                    MasterClassName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HasReconciliationMismatch = table.Column<bool>(type: "bit", nullable: false),
                    SelfServiceToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MasterClassInviteSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReassignmentValidationPendingSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CheckedInAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attendees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attendees_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Attendees_Orders_EventId_OrderId",
                        columns: x => new { x.EventId, x.OrderId },
                        principalTable: "Orders",
                        principalColumns: new[] { "EventId", "BackstageOrderId" });
                });

            migrationBuilder.CreateTable(
                name: "QuizQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuizId = table.Column<int>(type: "int", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrectIndex = table.Column<int>(type: "int", nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizQuestions_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionSlideStats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSlideStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSlideStats_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionSlideStats_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    WooProductId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ShortDescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PriceEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SalePriceEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    PromotedSlot = table.Column<int>(type: "int", nullable: true),
                    SponsorContributionPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ManageStock = table.Column<bool>(type: "bit", nullable: false),
                    StockQuantity = table.Column<int>(type: "int", nullable: true),
                    InStock = table.Column<bool>(type: "bit", nullable: false),
                    BoothId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Timeslot = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    TimeslotTime = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PrimaryCategoryId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ContentManagedInCeh = table.Column<bool>(type: "bit", nullable: false),
                    WooModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopProducts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopProducts_ShopCategories_PrimaryCategoryId",
                        column: x => x.PrimaryCategoryId,
                        principalTable: "ShopCategories",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SponsorUploadFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SponsorUploadLocationId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    GraphItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ETag = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LastModifiedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastNotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorUploadFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorUploadFiles_SponsorUploadLocations_SponsorUploadLocationId",
                        column: x => x.SponsorUploadLocationId,
                        principalTable: "SponsorUploadLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupPhotoRegistrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    VolumePackageCompanyId = table.Column<int>(type: "int", nullable: true),
                    TicketCount = table.Column<int>(type: "int", nullable: false),
                    InternalParticipants = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlannedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SlotPublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SlotNotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    InviteLastSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPhotoRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupPhotoRegistrations_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupPhotoRegistrations_VolumePackageCompanies_VolumePackageCompanyId",
                        column: x => x.VolumePackageCompanyId,
                        principalTable: "VolumePackageCompanies",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolumePackageQualificationSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    VolumePackageCompanyId = table.Column<int>(type: "int", nullable: false),
                    OnDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AttendeeCount = table.Column<int>(type: "int", nullable: false),
                    Qualified = table.Column<bool>(type: "bit", nullable: false),
                    FromOrders = table.Column<int>(type: "int", nullable: false),
                    FromCoupons = table.Column<int>(type: "int", nullable: false),
                    FromAttendeeDomains = table.Column<int>(type: "int", nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolumePackageQualificationSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolumePackageQualificationSnapshots_VolumePackageCompanies_VolumePackageCompanyId",
                        column: x => x.VolumePackageCompanyId,
                        principalTable: "VolumePackageCompanies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CouponPrepaidPurchases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CouponPrepaidAllocationId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    ErpInvoiceNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ErpInvoiceIsBooked = table.Column<bool>(type: "bit", nullable: false),
                    ErpInvoiceRaisedByCeh = table.Column<bool>(type: "bit", nullable: false),
                    ErpInvoiceConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ErpInvoiceConfirmedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    LastBillingReminderAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UnitPriceDkk = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CouponPrepaidPurchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CouponPrepaidPurchases_CouponPrepaidAllocations_CouponPrepaidAllocationId",
                        column: x => x.CouponPrepaidAllocationId,
                        principalTable: "CouponPrepaidAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationDeviceProvisionRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SerialNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestCount = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    EvaluationDeviceId = table.Column<int>(type: "int", nullable: true),
                    KeyIssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationDeviceProvisionRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationDeviceProvisionRequests_EvaluationDevices_EvaluationDeviceId",
                        column: x => x.EvaluationDeviceId,
                        principalTable: "EvaluationDevices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationDeviceProvisionRequests_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationTelemetryWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    EvaluationDeviceId = table.Column<int>(type: "int", nullable: true),
                    FromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ToUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationTelemetryWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationTelemetryWindows_EvaluationDevices_EvaluationDeviceId",
                        column: x => x.EvaluationDeviceId,
                        principalTable: "EvaluationDevices",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_EvaluationTelemetryWindows_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationSessionSpeakers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EvaluationSessionId = table.Column<int>(type: "int", nullable: false),
                    SpeakerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CehParticipantId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationSessionSpeakers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationSessionSpeakers_EvaluationSessions_EvaluationSessionId",
                        column: x => x.EvaluationSessionId,
                        principalTable: "EvaluationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AllocationScenarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OwnerParticipantId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DroppedParticipantId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CommittedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DiscardedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationScenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationScenarios_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarios_Participants_DroppedParticipantId",
                        column: x => x.DroppedParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarios_Participants_OwnerParticipantId",
                        column: x => x.OwnerParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DietaryRequirements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Surface = table.Column<int>(type: "int", nullable: false),
                    DietChoice = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Gluten = table.Column<bool>(type: "bit", nullable: false),
                    Crustaceans = table.Column<bool>(type: "bit", nullable: false),
                    Eggs = table.Column<bool>(type: "bit", nullable: false),
                    Fish = table.Column<bool>(type: "bit", nullable: false),
                    Peanuts = table.Column<bool>(type: "bit", nullable: false),
                    Soybeans = table.Column<bool>(type: "bit", nullable: false),
                    Milk = table.Column<bool>(type: "bit", nullable: false),
                    TreeNuts = table.Column<bool>(type: "bit", nullable: false),
                    Celery = table.Column<bool>(type: "bit", nullable: false),
                    Mustard = table.Column<bool>(type: "bit", nullable: false),
                    Sesame = table.Column<bool>(type: "bit", nullable: false),
                    Sulphites = table.Column<bool>(type: "bit", nullable: false),
                    Lupin = table.Column<bool>(type: "bit", nullable: false),
                    Molluscs = table.Column<bool>(type: "bit", nullable: false),
                    OtherAllergens = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DietaryRequirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DietaryRequirements_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DietaryRequirements_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DinnerSignups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Attending = table.Column<bool>(type: "bit", nullable: false),
                    Rsvp = table.Column<int>(type: "int", nullable: false),
                    DietaryPreference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AllergyNotes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PlusOne = table.Column<bool>(type: "bit", nullable: false),
                    PlusOneCount = table.Column<int>(type: "int", nullable: false),
                    Comments = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CalendarInviteSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DinnerSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DinnerSignups_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DinnerSignups_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FeedbackItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    PageUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RoutedTo = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedbackItems_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FeedbackItems_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GraphicAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    StableKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SharePointPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SharePointUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StorageItemId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    IsOrganizerOverridden = table.Column<bool>(type: "bit", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    InputHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphicAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GraphicAssets_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GraphicAssets_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_GraphicAssets_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HotelBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    NeedsRoom = table.Column<bool>(type: "bit", nullable: false),
                    CheckInDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CheckOutDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RoomShareWith = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ConfirmationState = table.Column<int>(type: "int", nullable: false),
                    ConfirmationNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RoomType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledHotelId = table.Column<int>(type: "int", nullable: true),
                    GuestsInRoom = table.Column<int>(type: "int", nullable: true),
                    RoommateParticipantId = table.Column<int>(type: "int", nullable: true),
                    CalendarInviteSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelBookings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HotelBookings_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HotelCoverageExtras",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    ExtraNights = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HotelCoverageExtras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HotelCoverageExtras_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HotelCoverageExtras_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoginPins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    PinHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginPins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoginPins_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LunchSignups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    LunchEarlySetupDay = table.Column<bool>(type: "bit", nullable: false),
                    LunchSetupDay = table.Column<bool>(type: "bit", nullable: false),
                    LunchPreDay = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LunchSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LunchSignups_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LunchSignups_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MagicLinkGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TokenIdHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MultiUse = table.Column<bool>(type: "bit", nullable: false),
                    TokenProtected = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UseCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MagicLinkGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MagicLinkGrants_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MagicLinkGrants_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OrganizerActionItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizerActionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizerActionItems_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrganizerActionItems_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ParticipantOrderOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Item = table.Column<int>(type: "int", nullable: false),
                    Include = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SetByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantOrderOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantOrderOverrides_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParticipantOrderOverrides_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ParticipantPolicyAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    AcceptedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CodeOfConductUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PrivacyPolicyUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantPolicyAcceptances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantPolicyAcceptances_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParticipantPolicyAcceptances_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ParticipantSecretaryTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    IssuedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParticipantSecretaryTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ParticipantSecretaryTokens_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ParticipantSecretaryTokens_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PartyRsvps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Attending = table.Column<bool>(type: "bit", nullable: false),
                    HeadCount = table.Column<int>(type: "int", nullable: true),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartyRsvps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartyRsvps_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartyRsvps_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "QuizAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    QuizId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Seed = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Score = table.Column<int>(type: "int", nullable: false),
                    ElapsedMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizAttempts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuizAttempts_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuizAttempts_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SavedSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedSessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedSessions_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SavedSessions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SessionEvaluationFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    UploadedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    UploadedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionEvaluationFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionEvaluationFiles_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionEvaluationFiles_Participants_UploadedByParticipantId",
                        column: x => x.UploadedByParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SessionEvaluationFiles_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SessionSpeakers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSpeakers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionSpeakers_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SessionSpeakers_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SoMePosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    SessionId = table.Column<int>(type: "int", nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ScheduledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    PlanState = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    TemplateKind = table.Column<int>(type: "int", nullable: true),
                    SubjectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Occurrence = table.Column<int>(type: "int", nullable: true),
                    AutoText = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    ActionPhrase = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IntroText = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ManualTextOverride = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    ImageRef = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    MediaKind = table.Column<int>(type: "int", nullable: false),
                    Tags = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AutoGenerated = table.Column<bool>(type: "bit", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExternalPostId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SpeakerPreAlertSent = table.Column<bool>(type: "bit", nullable: false),
                    Next24NoticeSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUpdatedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoMePosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoMePosts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SoMePosts_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SoMePosts_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SpeakerBackstageEmailSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    IdentityEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DesiredEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerBackstageEmailSyncs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeakerBackstageEmailSyncs_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpeakerBackstageEmailSyncs_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SpeakerProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Accreditation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsFirstTimeSpeaker = table.Column<bool>(type: "bit", nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SpeakingPreDay = table.Column<bool>(type: "bit", nullable: false),
                    SpeakingMainDay = table.Column<bool>(type: "bit", nullable: false),
                    SpeakerFunding = table.Column<int>(type: "int", nullable: false),
                    SpeakerCategory = table.Column<int>(type: "int", nullable: true),
                    GuestFundedNights = table.Column<int>(type: "int", nullable: true),
                    SelectedForPublish = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ContactEmailOverride = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CalendarEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CalendarEmailSetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Tagline = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Biography = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Blog = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LinkedIn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Twitter = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PhotoUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SessionizeSpeakerId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    BackstageSpeakerId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    FirstName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MvpCategories = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PhotoSharePointPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PhotoArchivedFromUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhotoArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SpeakerEditedFields = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BioLastEditedBySpeakerAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BackstageName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    BackstageTagline = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BackstageBio = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BackstageCountry = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CountryConfirmedInBackstage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CountryConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CountryConfirmedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BackstageLinkedIn = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BackstageTwitter = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ZohoGapNotifiedHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BackstageChangeCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSessionizeImportAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeakerProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeakerProfiles_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpeakerProfiles_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SponsorCompanyContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsSigner = table.Column<bool>(type: "bit", nullable: false),
                    IsEventCoordinator = table.Column<bool>(type: "bit", nullable: false),
                    IsBoothMember = table.Column<bool>(type: "bit", nullable: false),
                    ErpContactNumber = table.Column<int>(type: "int", nullable: true),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorCompanyContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorCompanyContacts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SponsorCompanyContacts_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SponsorSessionSpeakers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SponsorSessionId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SponsorSessionSpeakers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SponsorSessionSpeakers_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SponsorSessionSpeakers_SponsorSessions_SponsorSessionId",
                        column: x => x.SponsorSessionId,
                        principalTable: "SponsorSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SwagPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    WantsPolo = table.Column<bool>(type: "bit", nullable: false),
                    PoloSize = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    WantsJacket = table.Column<bool>(type: "bit", nullable: false),
                    JacketSize = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    WantsGift = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    WantsCredlyBadge = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwagPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwagPreferences_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SwagPreferences_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    AssignedParticipantId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FormStepKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecisionAnswer = table.Column<int>(type: "int", nullable: true),
                    DecisionAnsweredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    ClosedReason = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tasks_Participants_AssignedParticipantId",
                        column: x => x.AssignedParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TravelReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelReceipts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TravelReceipts_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TravelReimbursements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    RequestReimbursement = table.Column<bool>(type: "bit", nullable: false),
                    OriginCity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClaimAmountEur = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReopenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReopenedByEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReopenCount = table.Column<int>(type: "int", nullable: false),
                    IsPaid = table.Column<bool>(type: "bit", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PaidNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HasCompany = table.Column<bool>(type: "bit", nullable: true),
                    ReviewState = table.Column<int>(type: "int", nullable: false),
                    AiVerdict = table.Column<int>(type: "int", nullable: false),
                    AiSummary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AiFindingsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    AiCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AiInputHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AiNotifiedHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SentBackCount = table.Column<int>(type: "int", nullable: false),
                    ErpSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelReimbursements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelReimbursements_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TravelReimbursements_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VisaApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    FirstNames = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Nationality = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    HomeStreet = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HomePostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    HomeCity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HomeCountry = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyStreet = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyPostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    CompanyCity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyCountry = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    CompanyPhone = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CompanyEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CompanyWebsite = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DepartmentAndRole = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    VisitedBefore = table.Column<bool>(type: "bit", nullable: true),
                    CompanyActivity = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ContactOrigin = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RelationshipDuration = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FinancialScope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ArrivalDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LengthOfVisitDays = table.Column<int>(type: "int", nullable: true),
                    Entries = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SpecialCircumstances = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TravelPaidBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StayPaidBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Accommodation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    HostGuarantees = table.Column<bool>(type: "bit", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApplicationId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApplicationIdSetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisaApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisaApplications_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VisaLetters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisaLetters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisaLetters_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    SelectedShifts = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PreferredRole = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MaxHoursPerDay = table.Column<int>(type: "int", nullable: false),
                    LinkedInUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhotoUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AgreementAcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProfileConsent = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerAvailabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerAvailabilities_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerAvailabilities_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolunteerCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LeadParticipantId = table.Column<int>(type: "int", nullable: true),
                    SupervisorParticipantId = table.Column<int>(type: "int", nullable: true),
                    EldkLeadName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerCategories_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerCategories_Participants_LeadParticipantId",
                        column: x => x.LeadParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerCategories_Participants_SupervisorParticipantId",
                        column: x => x.SupervisorParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolunteerDayAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerDayAvailabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerDayAvailabilities_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerDayAvailabilities_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolunteerPlanningRoster",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    AddedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerPlanningRoster", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerPlanningRoster_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerPlanningRoster_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MasterClassComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    AuthorParticipantId = table.Column<int>(type: "int", nullable: true),
                    AuthorAttendeeId = table.Column<int>(type: "int", nullable: true),
                    AuthorDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ParentCommentId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Attendees_AuthorAttendeeId",
                        column: x => x.AuthorAttendeeId,
                        principalTable: "Attendees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterClassComments_MasterClassComments_ParentCommentId",
                        column: x => x.ParentCommentId,
                        principalTable: "MasterClassComments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Participants_AuthorParticipantId",
                        column: x => x.AuthorParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassComments_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MasterClassSignups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    AttendeeId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OfferExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PromotionNotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AutoSwitchConsentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WantsMonthBeforeReminder = table.Column<bool>(type: "bit", nullable: false),
                    MonthReminderSentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassSignups_Attendees_AttendeeId",
                        column: x => x.AttendeeId,
                        principalTable: "Attendees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassSignups_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterClassSignups_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MasterClassSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    AttendeeId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    UnsubscribedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Attendees_AttendeeId",
                        column: x => x.AttendeeId,
                        principalTable: "Attendees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassSubscriptions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SessionQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    AskerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AskerEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    QuestionText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    IsPrivate = table.Column<bool>(type: "bit", nullable: false),
                    AskerAttendeeId = table.Column<int>(type: "int", nullable: true),
                    IpHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResponseText = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RespondedByParticipantId = table.Column<int>(type: "int", nullable: true),
                    RespondedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionQuestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionQuestions_Attendees_AskerAttendeeId",
                        column: x => x.AskerAttendeeId,
                        principalTable: "Attendees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SessionQuestions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SessionQuestions_Participants_RespondedByParticipantId",
                        column: x => x.RespondedByParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SessionQuestions_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopEntitlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SponsorCompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceEdition = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BoothId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Timeslot = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    MatchedProductId = table.Column<int>(type: "int", nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClaimedOrderLineId = table.Column<int>(type: "int", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeclinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopEntitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopEntitlements_ShopProducts_MatchedProductId",
                        column: x => x.MatchedProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    PiecesPerUnit = table.Column<int>(type: "int", nullable: false),
                    UnitPriceEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NormalUnitPriceEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AgreedUnitPriceEur = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    SurchargePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    ProductName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    StockTaken = table.Column<bool>(type: "bit", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopOrderLines_ShopOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ShopOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShopOrderLines_ShopProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ShopProductCategories",
                columns: table => new
                {
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProductCategories", x => new { x.ProductId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_ShopProductCategories_ShopCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "ShopCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ShopProductCategories_ShopProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopProductImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    SourceUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AltText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SharePointPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SharePointItemId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CopyError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CopiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProductImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopProductImages_ShopProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuizAttemptAnswers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuizAttemptId = table.Column<int>(type: "int", nullable: false),
                    QuestionId = table.Column<int>(type: "int", nullable: false),
                    OrderIndex = table.Column<int>(type: "int", nullable: false),
                    ShownAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SelectedIndex = table.Column<int>(type: "int", nullable: true),
                    IsCorrect = table.Column<bool>(type: "bit", nullable: false),
                    PointsAwarded = table.Column<int>(type: "int", nullable: false),
                    ElapsedMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizAttemptAnswers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizAttemptAnswers_QuizAttempts_QuizAttemptId",
                        column: x => x.QuizAttemptId,
                        principalTable: "QuizAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_QuizAttemptAnswers_QuizQuestions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "QuizQuestions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolunteerBucketSupervisors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    AppointedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerBucketSupervisors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerBucketSupervisors_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerBucketSupervisors_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerBucketSupervisors_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerCategoryPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    AddedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerCategoryPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerCategoryPreferences_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerCategoryPreferences_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerCategoryPreferences_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerSubcategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerSubcategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerSubcategories_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerSubcategories_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExternalKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    SubcategoryId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Shift = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TimeEnd = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    EndTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    Criticality = table.Column<int>(type: "int", nullable: false),
                    ResponsibleTeam = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EldkLeadName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResourcesNeeded = table.Column<int>(type: "int", nullable: false),
                    Prerequisites = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Expectations = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerTasks_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerTasks_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerTasks_VolunteerSubcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "VolunteerSubcategories",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AllocationScenarioMoves",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScenarioId = table.Column<int>(type: "int", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Op = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    HotelId = table.Column<int>(type: "int", nullable: true),
                    TargetRole = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationScenarioMoves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_AllocationScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "AllocationScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_Hotels_HotelId",
                        column: x => x.HotelId,
                        principalTable: "Hotels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TaskAllocationDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OwnerParticipantId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    TargetRole = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    Source = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAllocationDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_Participants_OwnerParticipantId",
                        column: x => x.OwnerParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerHelpRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    RequestedByParticipantId = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Response = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RespondedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerHelpRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_Participants_RequestedByParticipantId",
                        column: x => x.RequestedByParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolunteerTaskAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    AssignedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DecisionStatus = table.Column<int>(type: "int", nullable: false),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecisionAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerTaskAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerTaskAssignments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerTaskAssignments_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerTaskAssignments_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgendaActivities_EventId_BackstageSessionId",
                table: "AgendaActivities",
                columns: new[] { "EventId", "BackstageSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgendaActivities_EventId_StartsAt_EndsAt",
                table: "AgendaActivities",
                columns: new[] { "EventId", "StartsAt", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_HotelId",
                table: "AllocationScenarioMoves",
                column: "HotelId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_ParticipantId",
                table: "AllocationScenarioMoves",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_ScenarioId_ParticipantId_TaskId_HotelId",
                table: "AllocationScenarioMoves",
                columns: new[] { "ScenarioId", "ParticipantId", "TaskId", "HotelId" },
                unique: true,
                filter: "[TaskId] IS NOT NULL AND [HotelId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_TaskId",
                table: "AllocationScenarioMoves",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarios_DroppedParticipantId",
                table: "AllocationScenarios",
                column: "DroppedParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarios_EventId_OwnerParticipantId_Status",
                table: "AllocationScenarios",
                columns: new[] { "EventId", "OwnerParticipantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarios_OwnerParticipantId",
                table: "AllocationScenarios",
                column: "OwnerParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_AppGameParticipations_EventId_SponsorCompanyId",
                table: "AppGameParticipations",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendeeMonitors_EventId_Kind_Value",
                table: "AttendeeMonitors",
                columns: new[] { "EventId", "Kind", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendeeMonitors_Token",
                table: "AttendeeMonitors",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_BackstageTicketId",
                table: "Attendees",
                columns: new[] { "EventId", "BackstageTicketId" },
                unique: true,
                filter: "[BackstageTicketId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_Email",
                table: "Attendees",
                columns: new[] { "EventId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_HasReconciliationMismatch",
                table: "Attendees",
                columns: new[] { "EventId", "HasReconciliationMismatch" });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_MirrorState",
                table: "Attendees",
                columns: new[] { "EventId", "MirrorState" });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_OrderId",
                table: "Attendees",
                columns: new[] { "EventId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EventId_Category",
                table: "AuditEntries",
                columns: new[] { "EventId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EventId_OccurredUtc",
                table: "AuditEntries",
                columns: new[] { "EventId", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigOverrides_EventId_Section",
                table: "ConfigOverrides",
                columns: new[] { "EventId", "Section" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CouponInvoicingSettings_EventId_CouponName",
                table: "CouponInvoicingSettings",
                columns: new[] { "EventId", "CouponName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CouponPrepaidAllocations_CouponInvoicingSettingId_TicketClassId",
                table: "CouponPrepaidAllocations",
                columns: new[] { "CouponInvoicingSettingId", "TicketClassId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CouponPrepaidAllocations_EventId",
                table: "CouponPrepaidAllocations",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_CouponPrepaidPurchases_CouponPrepaidAllocationId",
                table: "CouponPrepaidPurchases",
                column: "CouponPrepaidAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CouponSignupRequests_EventId_CreatedAt",
                table: "CouponSignupRequests",
                columns: new[] { "EventId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DietaryRequirements_EventId_ParticipantId_Surface",
                table: "DietaryRequirements",
                columns: new[] { "EventId", "ParticipantId", "Surface" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DietaryRequirements_EventId_Surface",
                table: "DietaryRequirements",
                columns: new[] { "EventId", "Surface" });

            migrationBuilder.CreateIndex(
                name: "IX_DietaryRequirements_ParticipantId",
                table: "DietaryRequirements",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_DinnerSignups_EventId_ParticipantId",
                table: "DinnerSignups",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DinnerSignups_ParticipantId",
                table: "DinnerSignups",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocLibrarySettingChanges_ChangedAt",
                table: "DocLibrarySettingChanges",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DocLibrarySettingChanges_Kind_Key_ChangedAt",
                table: "DocLibrarySettingChanges",
                columns: new[] { "Kind", "Key", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocLibrarySettingOverrides_Kind_Key",
                table: "DocLibrarySettingOverrides",
                columns: new[] { "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EconomicContactAnnotations_CustomerNumber_ContactNumber",
                table: "EconomicContactAnnotations",
                columns: new[] { "CustomerNumber", "ContactNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailLogs_EventId_ParticipantId",
                table: "EmailLogs",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailLogs_EventId_SentAt",
                table: "EmailLogs",
                columns: new[] { "EventId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailLogs_EventId_ToEmail",
                table: "EmailLogs",
                columns: new[] { "EventId", "ToEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailReminderCadences_EventId_TemplateKey_Role",
                table: "EmailReminderCadences",
                columns: new[] { "EventId", "TemplateKey", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplateOverrides_EventId_TemplateKey",
                table: "EmailTemplateOverrides",
                columns: new[] { "EventId", "TemplateKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplateRings_EventId_TemplateKey_Role",
                table: "EmailTemplateRings",
                columns: new[] { "EventId", "TemplateKey", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EngineAlertThrottles_ThrottleKey",
                table: "EngineAlertThrottles",
                column: "ThrottleKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpCustomerLinks_EventId_SponsorCompanyId",
                table: "ErpCustomerLinks",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErpOrderLinks_EventId_WebshopOrderId",
                table: "ErpOrderLinks",
                columns: new[] { "EventId", "WebshopOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationApiClients_EventId_Name",
                table: "EvaluationApiClients",
                columns: new[] { "EventId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EvaluationDeviceId",
                table: "EvaluationDeviceProvisionRequests",
                column: "EvaluationDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDeviceProvisionRequests_EventId_SerialNumber",
                table: "EvaluationDeviceProvisionRequests",
                columns: new[] { "EventId", "SerialNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_EventId_SerialNumber",
                table: "EvaluationDevices",
                columns: new[] { "EventId", "SerialNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_MasterClassRoomId",
                table: "EvaluationDevices",
                column: "MasterClassRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_PhysicalRoomId",
                table: "EvaluationDevices",
                column: "PhysicalRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationDevices_RoomId",
                table: "EvaluationDevices",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationPhysicalRooms_EventId_Name",
                table: "EvaluationPhysicalRooms",
                columns: new[] { "EventId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_EventId_CollectionTimestamp",
                table: "EvaluationResponses",
                columns: new[] { "EventId", "CollectionTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_EventId_SessionId",
                table: "EvaluationResponses",
                columns: new[] { "EventId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationResponses_SerialNumber_DeviceRecordId",
                table: "EvaluationResponses",
                columns: new[] { "SerialNumber", "DeviceRecordId" },
                unique: true,
                filter: "[SerialNumber] IS NOT NULL AND [DeviceRecordId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRoomPlacements_EvaluationRoomId",
                table: "EvaluationRoomPlacements",
                column: "EvaluationRoomId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationRooms_EventId_NameKey",
                table: "EvaluationRooms",
                columns: new[] { "EventId", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessions_EventId_CehSessionId",
                table: "EvaluationSessions",
                columns: new[] { "EventId", "CehSessionId" },
                unique: true,
                filter: "[CehSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessions_RoomId_CollectionWindowOpensAt",
                table: "EvaluationSessions",
                columns: new[] { "RoomId", "CollectionWindowOpensAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessionSpeakers_EvaluationSessionId_SpeakerEmail",
                table: "EvaluationSessionSpeakers",
                columns: new[] { "EvaluationSessionId", "SpeakerEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationSessionSpeakers_SpeakerEmail",
                table: "EvaluationSessionSpeakers",
                column: "SpeakerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationTelemetryWindows_EvaluationDeviceId",
                table: "EvaluationTelemetryWindows",
                column: "EvaluationDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationTelemetryWindows_EventId_IsActive_FromUtc",
                table: "EvaluationTelemetryWindows",
                columns: new[] { "EventId", "IsActive", "FromUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Code",
                table: "Events",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_IsActive",
                table: "Events",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_EventSoMePostOccurrences_EventSoMePostId_Sequence",
                table: "EventSoMePostOccurrences",
                columns: new[] { "EventSoMePostId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventSoMePosts_EventId_Slug",
                table: "EventSoMePosts",
                columns: new[] { "EventId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalRecipients_EventId_Email",
                table: "ExternalRecipients",
                columns: new[] { "EventId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureGroupSettings_EventId_Group",
                table: "FeatureGroupSettings",
                columns: new[] { "EventId", "Group" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeatureSettings_EventId_FeatureKey",
                table: "FeatureSettings",
                columns: new[] { "EventId", "FeatureKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackItems_EventId_ResolvedAt_CreatedAt",
                table: "FeedbackItems",
                columns: new[] { "EventId", "ResolvedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackItems_ParticipantId",
                table: "FeedbackItems",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_EventId_ParticipantId_Status",
                table: "GraphicAssets",
                columns: new[] { "EventId", "ParticipantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_EventId_StableKey",
                table: "GraphicAssets",
                columns: new[] { "EventId", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_EventId_Type_Status",
                table: "GraphicAssets",
                columns: new[] { "EventId", "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_ParticipantId",
                table: "GraphicAssets",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphicAssets_SessionId",
                table: "GraphicAssets",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GraphicsAssetLocations_EventId_PersonaGroup",
                table: "GraphicsAssetLocations",
                columns: new[] { "EventId", "PersonaGroup" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupPhotoRegistrations_EventId_ScheduledAtUtc",
                table: "GroupPhotoRegistrations",
                columns: new[] { "EventId", "ScheduledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupPhotoRegistrations_VolumePackageCompanyId",
                table: "GroupPhotoRegistrations",
                column: "VolumePackageCompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupPhotoSlots_EventId_StartUtc",
                table: "GroupPhotoSlots",
                columns: new[] { "EventId", "StartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelAllotments_EventId_HotelId_Night",
                table: "HotelAllotments",
                columns: new[] { "EventId", "HotelId", "Night" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelAllotments_HotelId",
                table: "HotelAllotments",
                column: "HotelId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_EventId_ParticipantId",
                table: "HotelBookings",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelBookings_ParticipantId",
                table: "HotelBookings",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelChangeLog_EventId_At",
                table: "HotelChangeLog",
                columns: new[] { "EventId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_HotelChangeLog_EventId_NotifiedAt",
                table: "HotelChangeLog",
                columns: new[] { "EventId", "NotifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HotelCoverageExtras_EventId_ParticipantId",
                table: "HotelCoverageExtras",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelCoverageExtras_ParticipantId",
                table: "HotelCoverageExtras",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelCoverageStandards_EventId_Class",
                table: "HotelCoverageStandards",
                columns: new[] { "EventId", "Class" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelCutoffs_EventId_CutoffDate",
                table: "HotelCutoffs",
                columns: new[] { "EventId", "CutoffDate" });

            migrationBuilder.CreateIndex(
                name: "IX_HotelCutoffs_EventId_HotelId_CutoffDate",
                table: "HotelCutoffs",
                columns: new[] { "EventId", "HotelId", "CutoffDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HotelCutoffs_HotelId",
                table: "HotelCutoffs",
                column: "HotelId");

            migrationBuilder.CreateIndex(
                name: "IX_HotelForecastExtras_EventId_Night",
                table: "HotelForecastExtras",
                columns: new[] { "EventId", "Night" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Hotels_EventId_Name",
                table: "Hotels",
                columns: new[] { "EventId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationAudits_EventId_CreatedAt",
                table: "ImpersonationAudits",
                columns: new[] { "EventId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationAudits_EventId_TargetParticipantId",
                table: "ImpersonationAudits",
                columns: new[] { "EventId", "TargetParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_JobHealthMarkers_JobKey",
                table: "JobHealthMarkers",
                column: "JobKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobRunStates_FunctionName",
                table: "JobRunStates",
                column: "FunctionName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkedInOAuthTokens_Kind",
                table: "LinkedInOAuthTokens",
                column: "Kind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoginPins_ParticipantId_ExpiresAt",
                table: "LoginPins",
                columns: new[] { "ParticipantId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LogisticsFileStates_EventId_FileName",
                table: "LogisticsFileStates",
                columns: new[] { "EventId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogisticsRunSummaries_EventId",
                table: "LogisticsRunSummaries",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LunchSignups_EventId_ParticipantId",
                table: "LunchSignups",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LunchSignups_ParticipantId",
                table: "LunchSignups",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkGrants_EventId_ParticipantId_Purpose",
                table: "MagicLinkGrants",
                columns: new[] { "EventId", "ParticipantId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkGrants_ParticipantId",
                table: "MagicLinkGrants",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinkGrants_TokenIdHash",
                table: "MagicLinkGrants",
                column: "TokenIdHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailCampaignRecipients_MailCampaignId_Email",
                table: "MailCampaignRecipients",
                columns: new[] { "MailCampaignId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailCampaignRecipients_MailCampaignId_State",
                table: "MailCampaignRecipients",
                columns: new[] { "MailCampaignId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MailCampaigns_EventId_State",
                table: "MailCampaigns",
                columns: new[] { "EventId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MailSuppressions_EventId_Email",
                table: "MailSuppressions",
                columns: new[] { "EventId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_AuthorAttendeeId",
                table: "MasterClassComments",
                column: "AuthorAttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_AuthorParticipantId",
                table: "MasterClassComments",
                column: "AuthorParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_EventId",
                table: "MasterClassComments",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_ParentCommentId",
                table: "MasterClassComments",
                column: "ParentCommentId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassComments_SessionId_CreatedAt",
                table: "MasterClassComments",
                columns: new[] { "SessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSettings_EventId",
                table: "MasterClassSettings",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_AttendeeId",
                table: "MasterClassSignups",
                column: "AttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "AttendeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_AttendeeId_SessionId",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "AttendeeId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_EventId_SessionId_Status",
                table: "MasterClassSignups",
                columns: new[] { "EventId", "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_SessionId_Status",
                table: "MasterClassSignups",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_AttendeeId",
                table: "MasterClassSubscriptions",
                column: "AttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_EventId",
                table: "MasterClassSubscriptions",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_ParticipantId",
                table: "MasterClassSubscriptions",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_SessionId_Kind",
                table: "MasterClassSubscriptions",
                columns: new[] { "SessionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_SessionId_Kind_AttendeeId",
                table: "MasterClassSubscriptions",
                columns: new[] { "SessionId", "Kind", "AttendeeId" },
                unique: true,
                filter: "[AttendeeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSubscriptions_SessionId_Kind_ParticipantId",
                table: "MasterClassSubscriptions",
                columns: new[] { "SessionId", "Kind", "ParticipantId" },
                unique: true,
                filter: "[ParticipantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_EventId_MirrorState",
                table: "Orders",
                columns: new[] { "EventId", "MirrorState" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizerActionItems_EventId_Type_ParticipantId_ResolvedAt",
                table: "OrganizerActionItems",
                columns: new[] { "EventId", "Type", "ParticipantId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizerActionItems_ParticipantId",
                table: "OrganizerActionItems",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantOrderOverrides_EventId_ParticipantId_Item",
                table: "ParticipantOrderOverrides",
                columns: new[] { "EventId", "ParticipantId", "Item" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantOrderOverrides_ParticipantId",
                table: "ParticipantOrderOverrides",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantPolicyAcceptances_EventId_ParticipantId",
                table: "ParticipantPolicyAcceptances",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantPolicyAcceptances_ParticipantId",
                table: "ParticipantPolicyAcceptances",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_CalendarFeedToken",
                table: "Participants",
                column: "CalendarFeedToken",
                unique: true,
                filter: "[CalendarFeedToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_AlternateEmail",
                table: "Participants",
                columns: new[] { "EventId", "AlternateEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_Email",
                table: "Participants",
                columns: new[] { "EventId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_HotelId",
                table: "Participants",
                columns: new[] { "EventId", "HotelId" });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_LifecycleState",
                table: "Participants",
                columns: new[] { "EventId", "LifecycleState" });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_SponsorCompanyId",
                table: "Participants",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_HotelId",
                table: "Participants",
                column: "HotelId");

            migrationBuilder.CreateIndex(
                name: "IX_Participants_SamePersonAsId",
                table: "Participants",
                column: "SamePersonAsId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantSecretaryTokens_EventId_ParticipantId",
                table: "ParticipantSecretaryTokens",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantSecretaryTokens_ParticipantId",
                table: "ParticipantSecretaryTokens",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_ParticipantSecretaryTokens_Token",
                table: "ParticipantSecretaryTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartyRsvps_EventId_Email",
                table: "PartyRsvps",
                columns: new[] { "EventId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartyRsvps_ParticipantId",
                table: "PartyRsvps",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttemptAnswers_QuestionId",
                table: "QuizAttemptAnswers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttemptAnswers_QuizAttemptId_OrderIndex",
                table: "QuizAttemptAnswers",
                columns: new[] { "QuizAttemptId", "OrderIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_EventId_QuizId_ParticipantId",
                table: "QuizAttempts",
                columns: new[] { "EventId", "QuizId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_ParticipantId",
                table: "QuizAttempts",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_QuizId_Score_ElapsedMs",
                table: "QuizAttempts",
                columns: new[] { "QuizId", "Score", "ElapsedMs" });

            migrationBuilder.CreateIndex(
                name: "IX_QuizQuestions_QuizId_SortOrder",
                table: "QuizQuestions",
                columns: new[] { "QuizId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Quizzes_EventId_Slug",
                table: "Quizzes",
                columns: new[] { "EventId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quizzes_EventId_Topic",
                table: "Quizzes",
                columns: new[] { "EventId", "Topic" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_EventId_ParticipantId",
                table: "SavedSessions",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_EventId_ParticipantId_SessionId",
                table: "SavedSessions",
                columns: new[] { "EventId", "ParticipantId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_ParticipantId",
                table: "SavedSessions",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedSessions_SessionId",
                table: "SavedSessions",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleEntries_EventId_StartsAt",
                table: "ScheduleEntries",
                columns: new[] { "EventId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_EventId_RecipientEmail_ReminderType_OccasionKey",
                table: "SentReminders",
                columns: new[] { "EventId", "RecipientEmail", "ReminderType", "OccasionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluationFiles_EventId_SessionId",
                table: "SessionEvaluationFiles",
                columns: new[] { "EventId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluationFiles_SessionId_Kind",
                table: "SessionEvaluationFiles",
                columns: new[] { "SessionId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionEvaluationFiles_UploadedByParticipantId",
                table: "SessionEvaluationFiles",
                column: "UploadedByParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionizeEndpointSettings_EventId",
                table: "SessionizeEndpointSettings",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_AskerAttendeeId",
                table: "SessionQuestions",
                column: "AskerAttendeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_EventId_IpHash",
                table: "SessionQuestions",
                columns: new[] { "EventId", "IpHash" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_EventId_Status_CreatedAt",
                table: "SessionQuestions",
                columns: new[] { "EventId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_RespondedByParticipantId",
                table: "SessionQuestions",
                column: "RespondedByParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_SessionId_AskerAttendeeId",
                table: "SessionQuestions",
                columns: new[] { "SessionId", "AskerAttendeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionQuestions_SessionId_Status",
                table: "SessionQuestions",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_BackstageSessionId",
                table: "Sessions",
                columns: new[] { "EventId", "BackstageSessionId" },
                unique: true,
                filter: "[BackstageSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_Length",
                table: "Sessions",
                columns: new[] { "EventId", "Length" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_Room",
                table: "Sessions",
                columns: new[] { "EventId", "Room" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_SessionizeId",
                table: "Sessions",
                columns: new[] { "EventId", "SessionizeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_SponsorSlotKey",
                table: "Sessions",
                columns: new[] { "EventId", "SponsorSlotKey" },
                unique: true,
                filter: "[SponsorSlotKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_Type",
                table: "Sessions",
                columns: new[] { "EventId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PublicSlug",
                table: "Sessions",
                column: "PublicSlug",
                unique: true,
                filter: "[PublicSlug] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PublicToken",
                table: "Sessions",
                column: "PublicToken",
                unique: true,
                filter: "[PublicToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SessionSlideStats_EventId_SessionId",
                table: "SessionSlideStats",
                columns: new[] { "EventId", "SessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionSlideStats_SessionId",
                table: "SessionSlideStats",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionSourceSettings_EventId",
                table: "SessionSourceSettings",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SessionSpeakers_ParticipantId",
                table: "SessionSpeakers",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionSpeakers_SessionId_ParticipantId",
                table: "SessionSpeakers",
                columns: new[] { "SessionId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopBoothSpots_EventId_BoothId",
                table: "ShopBoothSpots",
                columns: new[] { "EventId", "BoothId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopCatalogPages_EventId_Slug",
                table: "ShopCatalogPages",
                columns: new[] { "EventId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopCategories_EventId_WooCategoryId",
                table: "ShopCategories",
                columns: new[] { "EventId", "WooCategoryId" },
                unique: true,
                filter: "[WooCategoryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopCategories_ParentId",
                table: "ShopCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopCompanies_EventId_Cvr",
                table: "ShopCompanies",
                columns: new[] { "EventId", "Cvr" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopCompanies_EventId_SponsorCompanyId",
                table: "ShopCompanies",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopEntitlements_EventId_SponsorCompanyId",
                table: "ShopEntitlements",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopEntitlements_MatchedProductId",
                table: "ShopEntitlements",
                column: "MatchedProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopJoinRequests_EventId_Approved",
                table: "ShopJoinRequests",
                columns: new[] { "EventId", "Approved" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopKnownContacts_EventId_SponsorCompanyId_Email",
                table: "ShopKnownContacts",
                columns: new[] { "EventId", "SponsorCompanyId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrderLines_OrderId",
                table: "ShopOrderLines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrderLines_ProductId",
                table: "ShopOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_EventId_Number",
                table: "ShopOrders",
                columns: new[] { "EventId", "Number" },
                unique: true,
                filter: "[Number] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopOrders_EventId_SponsorCompanyId_Status",
                table: "ShopOrders",
                columns: new[] { "EventId", "SponsorCompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductCategories_CategoryId",
                table: "ShopProductCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductImages_ProductId",
                table: "ShopProductImages",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_EventId_BoothId",
                table: "ShopProducts",
                columns: new[] { "EventId", "BoothId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_EventId_WooProductId",
                table: "ShopProducts",
                columns: new[] { "EventId", "WooProductId" },
                unique: true,
                filter: "[WooProductId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_PrimaryCategoryId",
                table: "ShopProducts",
                column: "PrimaryCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopRegistrations_EventId_Email",
                table: "ShopRegistrations",
                columns: new[] { "EventId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_SignageSettings_EventId",
                table: "SignageSettings",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoMeBodySamples_EventId_Kind_SortOrder",
                table: "SoMeBodySamples",
                columns: new[] { "EventId", "Kind", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SoMeCadenceSettings_EventId_Kind",
                table: "SoMeCadenceSettings",
                columns: new[] { "EventId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoMeCategoryRounds_EventId_Category_RoundNumber",
                table: "SoMeCategoryRounds",
                columns: new[] { "EventId", "Category", "RoundNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoMeCategoryRules_EventId_Category",
                table: "SoMeCategoryRules",
                columns: new[] { "EventId", "Category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoMePosts_EventId_IsDeleted",
                table: "SoMePosts",
                columns: new[] { "EventId", "IsDeleted" });

            migrationBuilder.CreateIndex(
                name: "IX_SoMePosts_EventId_Status_IsActive_ScheduledAtUtc",
                table: "SoMePosts",
                columns: new[] { "EventId", "Status", "IsActive", "ScheduledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SoMePosts_EventId_SubjectKey_Occurrence",
                table: "SoMePosts",
                columns: new[] { "EventId", "SubjectKey", "Occurrence" });

            migrationBuilder.CreateIndex(
                name: "IX_SoMePosts_ParticipantId",
                table: "SoMePosts",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SoMePosts_SessionId",
                table: "SoMePosts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SoMeSettings_EventId",
                table: "SoMeSettings",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoMeTemplates_EventId_Kind",
                table: "SoMeTemplates",
                columns: new[] { "EventId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerBackstageEmailSyncs_EventId_ParticipantId",
                table: "SpeakerBackstageEmailSyncs",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerBackstageEmailSyncs_ParticipantId",
                table: "SpeakerBackstageEmailSyncs",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerProfiles_EventId_ParticipantId",
                table: "SpeakerProfiles",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeakerProfiles_ParticipantId",
                table: "SpeakerProfiles",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorApiKeys_EventId_SponsorCompanyId_RevokedAt",
                table: "SponsorApiKeys",
                columns: new[] { "EventId", "SponsorCompanyId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorBoothMaterials_EventId_SponsorCompanyId_Kind",
                table: "SponsorBoothMaterials",
                columns: new[] { "EventId", "SponsorCompanyId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorBoothMembers_EventId_SponsorCompanyId_Email",
                table: "SponsorBoothMembers",
                columns: new[] { "EventId", "SponsorCompanyId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_EventId_ParticipantId_SponsorCompanyId",
                table: "SponsorCompanyContacts",
                columns: new[] { "EventId", "ParticipantId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_EventId_SponsorCompanyId",
                table: "SponsorCompanyContacts",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_EventId_SponsorCompanyId_ErpContactNumber",
                table: "SponsorCompanyContacts",
                columns: new[] { "EventId", "SponsorCompanyId", "ErpContactNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorCompanyContacts_ParticipantId",
                table: "SponsorCompanyContacts",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorInfos_EventId_SponsorCompanyId",
                table: "SponsorInfos",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorInfos_EventId_Tier",
                table: "SponsorInfos",
                columns: new[] { "EventId", "Tier" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeadNotificationPrefs_EventId_SponsorCompanyId",
                table: "SponsorLeadNotificationPrefs",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeads_EventId_SponsorCompanyId_CapturedAt",
                table: "SponsorLeads",
                columns: new[] { "EventId", "SponsorCompanyId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeads_EventId_SponsorCompanyId_Status",
                table: "SponsorLeads",
                columns: new[] { "EventId", "SponsorCompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorLeads_EventId_ZohoRecordId",
                table: "SponsorLeads",
                columns: new[] { "EventId", "ZohoRecordId" },
                unique: true,
                filter: "[ZohoRecordId] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorSessions_EventId_SponsorCompanyId",
                table: "SponsorSessions",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorSessionSpeakers_ParticipantId",
                table: "SponsorSessionSpeakers",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SponsorSessionSpeakers_SponsorSessionId_Email",
                table: "SponsorSessionSpeakers",
                columns: new[] { "SponsorSessionId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorTokenVersions_EventId_SponsorCompanyId",
                table: "SponsorTokenVersions",
                columns: new[] { "EventId", "SponsorCompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorUploadAudits_EventId_SponsorCompanyId_Kind",
                table: "SponsorUploadAudits",
                columns: new[] { "EventId", "SponsorCompanyId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_SponsorUploadFiles_SponsorUploadLocationId_GraphItemId",
                table: "SponsorUploadFiles",
                columns: new[] { "SponsorUploadLocationId", "GraphItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorUploadLocations_EventId_SponsorCompanyId_FolderKey",
                table: "SponsorUploadLocations",
                columns: new[] { "EventId", "SponsorCompanyId", "FolderKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponseAnswers_QuestionId",
                table: "SurveyResponseAnswers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponseAnswers_SurveyResponseId_QuestionId",
                table: "SurveyResponseAnswers",
                columns: new[] { "SurveyResponseId", "QuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponsePicks_SurveyResponseId_Rank",
                table: "SurveyResponsePicks",
                columns: new[] { "SurveyResponseId", "Rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponsePicks_SurveyResponseId_TopicId",
                table: "SurveyResponsePicks",
                columns: new[] { "SurveyResponseId", "TopicId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponsePicks_TopicId",
                table: "SurveyResponsePicks",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponses_SurveySlug_SelectedTrackId",
                table: "SurveyResponses",
                columns: new[] { "SurveySlug", "SelectedTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyResponses_SurveySlug_SubmittedAt",
                table: "SurveyResponses",
                columns: new[] { "SurveySlug", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SurveyStates_SurveySlug",
                table: "SurveyStates",
                column: "SurveySlug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwagCatalogCredits_EventId_Code",
                table: "SwagCatalogCredits",
                columns: new[] { "EventId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwagCatalogCredits_EventId_SponsorCompanyId",
                table: "SwagCatalogCredits",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_SwagCatalogHolds_EventId_ProductId",
                table: "SwagCatalogHolds",
                columns: new[] { "EventId", "ProductId" },
                unique: true,
                filter: "[ReleasedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SwagCatalogHolds_EventId_ReleasedAt",
                table: "SwagCatalogHolds",
                columns: new[] { "EventId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SwagPreferences_EventId_ParticipantId",
                table: "SwagPreferences",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwagPreferences_ParticipantId",
                table: "SwagPreferences",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncDeltas_EventId_Status_EntityType_EntityId_ChangeKind",
                table: "SyncDeltas",
                columns: new[] { "EventId", "Status", "EntityType", "EntityId", "ChangeKind" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_EventId_Key",
                table: "SyncRuns",
                columns: new[] { "EventId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_EventId_OwnerParticipantId",
                table: "TaskAllocationDrafts",
                columns: new[] { "EventId", "OwnerParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_OwnerParticipantId",
                table: "TaskAllocationDrafts",
                column: "OwnerParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_ParticipantId",
                table: "TaskAllocationDrafts",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_TaskId_ParticipantId",
                table: "TaskAllocationDrafts",
                columns: new[] { "TaskId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_AssignedParticipantId",
                table: "Tasks",
                column: "AssignedParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_EventId_AssignedParticipantId",
                table: "Tasks",
                columns: new[] { "EventId", "AssignedParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_EventId_DueDate",
                table: "Tasks",
                columns: new[] { "EventId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_EventId_SponsorCompanyId",
                table: "Tasks",
                columns: new[] { "EventId", "SponsorCompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_TravelReceipts_EventId_ParticipantId",
                table: "TravelReceipts",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TravelReceipts_ParticipantId",
                table: "TravelReceipts",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelReimbursements_EventId_ParticipantId",
                table: "TravelReimbursements",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelReimbursements_ParticipantId",
                table: "TravelReimbursements",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelReviewLog_EventId_TravelReimbursementId_At",
                table: "TravelReviewLog",
                columns: new[] { "EventId", "TravelReimbursementId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_VisaApplications_EventId",
                table: "VisaApplications",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_VisaApplications_ParticipantId",
                table: "VisaApplications",
                column: "ParticipantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisaLetters_EventId",
                table: "VisaLetters",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_VisaLetters_ParticipantId",
                table: "VisaLetters",
                column: "ParticipantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolumePackageCompanies_EventId_CustomName",
                table: "VolumePackageCompanies",
                columns: new[] { "EventId", "CustomName" });

            migrationBuilder.CreateIndex(
                name: "IX_VolumePackageCompanies_WizardToken",
                table: "VolumePackageCompanies",
                column: "WizardToken",
                unique: true,
                filter: "[WizardToken] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VolumePackageQualificationSnapshots_VolumePackageCompanyId_OnDate",
                table: "VolumePackageQualificationSnapshots",
                columns: new[] { "VolumePackageCompanyId", "OnDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerAvailabilities_EventId_ParticipantId",
                table: "VolunteerAvailabilities",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerAvailabilities_ParticipantId",
                table: "VolunteerAvailabilities",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerBucketSupervisors_CategoryId_ParticipantId",
                table: "VolunteerBucketSupervisors",
                columns: new[] { "CategoryId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerBucketSupervisors_EventId_ParticipantId",
                table: "VolunteerBucketSupervisors",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerBucketSupervisors_ParticipantId",
                table: "VolunteerBucketSupervisors",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_EventId_Name",
                table: "VolunteerCategories",
                columns: new[] { "EventId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_EventId_SupervisorParticipantId",
                table: "VolunteerCategories",
                columns: new[] { "EventId", "SupervisorParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_LeadParticipantId",
                table: "VolunteerCategories",
                column: "LeadParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_SupervisorParticipantId",
                table: "VolunteerCategories",
                column: "SupervisorParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategoryPreferences_CategoryId_ParticipantId",
                table: "VolunteerCategoryPreferences",
                columns: new[] { "CategoryId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategoryPreferences_EventId_ParticipantId",
                table: "VolunteerCategoryPreferences",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategoryPreferences_ParticipantId",
                table: "VolunteerCategoryPreferences",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerDayAvailabilities_EventId_ParticipantId_Day",
                table: "VolunteerDayAvailabilities",
                columns: new[] { "EventId", "ParticipantId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerDayAvailabilities_ParticipantId",
                table: "VolunteerDayAvailabilities",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_CategoryId",
                table: "VolunteerHelpRequests",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_EventId_CategoryId_Status",
                table: "VolunteerHelpRequests",
                columns: new[] { "EventId", "CategoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_EventId_RequestedByParticipantId",
                table: "VolunteerHelpRequests",
                columns: new[] { "EventId", "RequestedByParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_RequestedByParticipantId",
                table: "VolunteerHelpRequests",
                column: "RequestedByParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_TaskId",
                table: "VolunteerHelpRequests",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerPlanningRoster_EventId_ParticipantId",
                table: "VolunteerPlanningRoster",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerPlanningRoster_ParticipantId",
                table: "VolunteerPlanningRoster",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerSubcategories_CategoryId",
                table: "VolunteerSubcategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerSubcategories_EventId_CategoryId",
                table: "VolunteerSubcategories",
                columns: new[] { "EventId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTaskAssignments_EventId_ParticipantId",
                table: "VolunteerTaskAssignments",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTaskAssignments_ParticipantId",
                table: "VolunteerTaskAssignments",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTaskAssignments_TaskId_ParticipantId",
                table: "VolunteerTaskAssignments",
                columns: new[] { "TaskId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_CategoryId",
                table: "VolunteerTasks",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_EventId_CategoryId",
                table: "VolunteerTasks",
                columns: new[] { "EventId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_EventId_Status",
                table: "VolunteerTasks",
                columns: new[] { "EventId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_EventId_SubcategoryId",
                table: "VolunteerTasks",
                columns: new[] { "EventId", "SubcategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_ExternalKey",
                table: "VolunteerTasks",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_SubcategoryId",
                table: "VolunteerTasks",
                column: "SubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WebshopOrderArchives_EventId_CompanyId",
                table: "WebshopOrderArchives",
                columns: new[] { "EventId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebshopOrderArchives_EventId_Status",
                table: "WebshopOrderArchives",
                columns: new[] { "EventId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WordPressRefreshPosts_EventId_WordPressPostId",
                table: "WordPressRefreshPosts",
                columns: new[] { "EventId", "WordPressPostId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ZohoOrderSyncRequests_EventId_ProcessedAt",
                table: "ZohoOrderSyncRequests",
                columns: new[] { "EventId", "ProcessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ZohoTokenLeases_CredentialKey",
                table: "ZohoTokenLeases",
                column: "CredentialKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgendaActivities");

            migrationBuilder.DropTable(
                name: "AllocationScenarioMoves");

            migrationBuilder.DropTable(
                name: "AppGameParticipations");

            migrationBuilder.DropTable(
                name: "AttendeeMonitors");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "ConfigOverrides");

            migrationBuilder.DropTable(
                name: "CouponPrepaidPurchases");

            migrationBuilder.DropTable(
                name: "CouponSignupRequests");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "DietaryRequirements");

            migrationBuilder.DropTable(
                name: "DinnerSignups");

            migrationBuilder.DropTable(
                name: "DocLibrarySettingChanges");

            migrationBuilder.DropTable(
                name: "DocLibrarySettingOverrides");

            migrationBuilder.DropTable(
                name: "EconomicContactAnnotations");

            migrationBuilder.DropTable(
                name: "EmailLogs");

            migrationBuilder.DropTable(
                name: "EmailReminderCadences");

            migrationBuilder.DropTable(
                name: "EmailTemplateOverrides");

            migrationBuilder.DropTable(
                name: "EmailTemplateRings");

            migrationBuilder.DropTable(
                name: "EngineAlertThrottles");

            migrationBuilder.DropTable(
                name: "ErpCustomerLinks");

            migrationBuilder.DropTable(
                name: "ErpOrderLinks");

            migrationBuilder.DropTable(
                name: "EvaluationApiClients");

            migrationBuilder.DropTable(
                name: "EvaluationDeviceProvisionRequests");

            migrationBuilder.DropTable(
                name: "EvaluationResponses");

            migrationBuilder.DropTable(
                name: "EvaluationRoomPlacements");

            migrationBuilder.DropTable(
                name: "EvaluationSessionSpeakers");

            migrationBuilder.DropTable(
                name: "EvaluationTelemetryWindows");

            migrationBuilder.DropTable(
                name: "EventSoMePostOccurrences");

            migrationBuilder.DropTable(
                name: "ExternalRecipients");

            migrationBuilder.DropTable(
                name: "FeatureGroupSettings");

            migrationBuilder.DropTable(
                name: "FeatureSettings");

            migrationBuilder.DropTable(
                name: "FeedbackItems");

            migrationBuilder.DropTable(
                name: "GraphicAssets");

            migrationBuilder.DropTable(
                name: "GraphicsAssetLocations");

            migrationBuilder.DropTable(
                name: "GroupPhotoRegistrations");

            migrationBuilder.DropTable(
                name: "GroupPhotoSlots");

            migrationBuilder.DropTable(
                name: "HotelAllotments");

            migrationBuilder.DropTable(
                name: "HotelBookings");

            migrationBuilder.DropTable(
                name: "HotelChangeLog");

            migrationBuilder.DropTable(
                name: "HotelCoverageExtras");

            migrationBuilder.DropTable(
                name: "HotelCoverageStandards");

            migrationBuilder.DropTable(
                name: "HotelCutoffs");

            migrationBuilder.DropTable(
                name: "HotelForecastExtras");

            migrationBuilder.DropTable(
                name: "ImpersonationAudits");

            migrationBuilder.DropTable(
                name: "JobHealthMarkers");

            migrationBuilder.DropTable(
                name: "JobRunStates");

            migrationBuilder.DropTable(
                name: "LinkedInOAuthTokens");

            migrationBuilder.DropTable(
                name: "LoginPins");

            migrationBuilder.DropTable(
                name: "LogisticsFileStates");

            migrationBuilder.DropTable(
                name: "LogisticsRunSummaries");

            migrationBuilder.DropTable(
                name: "LunchSignups");

            migrationBuilder.DropTable(
                name: "MagicLinkGrants");

            migrationBuilder.DropTable(
                name: "MailCampaignRecipients");

            migrationBuilder.DropTable(
                name: "MailSuppressions");

            migrationBuilder.DropTable(
                name: "MasterClassComments");

            migrationBuilder.DropTable(
                name: "MasterClassSettings");

            migrationBuilder.DropTable(
                name: "MasterClassSignups");

            migrationBuilder.DropTable(
                name: "MasterClassSubscriptions");

            migrationBuilder.DropTable(
                name: "OrganizerActionItems");

            migrationBuilder.DropTable(
                name: "ParticipantOrderOverrides");

            migrationBuilder.DropTable(
                name: "ParticipantPolicyAcceptances");

            migrationBuilder.DropTable(
                name: "ParticipantSecretaryTokens");

            migrationBuilder.DropTable(
                name: "PartyRsvps");

            migrationBuilder.DropTable(
                name: "QuizAttemptAnswers");

            migrationBuilder.DropTable(
                name: "SavedSessions");

            migrationBuilder.DropTable(
                name: "ScheduleEntries");

            migrationBuilder.DropTable(
                name: "SentReminders");

            migrationBuilder.DropTable(
                name: "SessionEvaluationFiles");

            migrationBuilder.DropTable(
                name: "SessionizeEndpointSettings");

            migrationBuilder.DropTable(
                name: "SessionQuestions");

            migrationBuilder.DropTable(
                name: "SessionSlideStats");

            migrationBuilder.DropTable(
                name: "SessionSourceSettings");

            migrationBuilder.DropTable(
                name: "SessionSpeakers");

            migrationBuilder.DropTable(
                name: "ShopBoothSpots");

            migrationBuilder.DropTable(
                name: "ShopCatalogPages");

            migrationBuilder.DropTable(
                name: "ShopCompanies");

            migrationBuilder.DropTable(
                name: "ShopEntitlements");

            migrationBuilder.DropTable(
                name: "ShopJoinRequests");

            migrationBuilder.DropTable(
                name: "ShopKnownContacts");

            migrationBuilder.DropTable(
                name: "ShopOrderLines");

            migrationBuilder.DropTable(
                name: "ShopProductCategories");

            migrationBuilder.DropTable(
                name: "ShopProductImages");

            migrationBuilder.DropTable(
                name: "ShopRegistrations");

            migrationBuilder.DropTable(
                name: "SignageSettings");

            migrationBuilder.DropTable(
                name: "SoMeBodySamples");

            migrationBuilder.DropTable(
                name: "SoMeCadenceSettings");

            migrationBuilder.DropTable(
                name: "SoMeCategoryRounds");

            migrationBuilder.DropTable(
                name: "SoMeCategoryRules");

            migrationBuilder.DropTable(
                name: "SoMePosts");

            migrationBuilder.DropTable(
                name: "SoMeSettings");

            migrationBuilder.DropTable(
                name: "SoMeTemplates");

            migrationBuilder.DropTable(
                name: "SpeakerBackstageEmailSyncs");

            migrationBuilder.DropTable(
                name: "SpeakerProfiles");

            migrationBuilder.DropTable(
                name: "SponsorApiKeys");

            migrationBuilder.DropTable(
                name: "SponsorBoothMaterials");

            migrationBuilder.DropTable(
                name: "SponsorBoothMembers");

            migrationBuilder.DropTable(
                name: "SponsorCompanyContacts");

            migrationBuilder.DropTable(
                name: "SponsorInfos");

            migrationBuilder.DropTable(
                name: "SponsorLeadNotificationPrefs");

            migrationBuilder.DropTable(
                name: "SponsorLeads");

            migrationBuilder.DropTable(
                name: "SponsorSessionSpeakers");

            migrationBuilder.DropTable(
                name: "SponsorTokenVersions");

            migrationBuilder.DropTable(
                name: "SponsorUploadAudits");

            migrationBuilder.DropTable(
                name: "SponsorUploadFiles");

            migrationBuilder.DropTable(
                name: "SurveyResponseAnswers");

            migrationBuilder.DropTable(
                name: "SurveyResponsePicks");

            migrationBuilder.DropTable(
                name: "SurveyStates");

            migrationBuilder.DropTable(
                name: "SwagCatalogCredits");

            migrationBuilder.DropTable(
                name: "SwagCatalogHolds");

            migrationBuilder.DropTable(
                name: "SwagPreferences");

            migrationBuilder.DropTable(
                name: "SyncDeltas");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "TaskAllocationDrafts");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "TravelReceipts");

            migrationBuilder.DropTable(
                name: "TravelReimbursements");

            migrationBuilder.DropTable(
                name: "TravelReviewLog");

            migrationBuilder.DropTable(
                name: "VisaApplications");

            migrationBuilder.DropTable(
                name: "VisaLetters");

            migrationBuilder.DropTable(
                name: "VolumePackageQualificationSnapshots");

            migrationBuilder.DropTable(
                name: "VolunteerAvailabilities");

            migrationBuilder.DropTable(
                name: "VolunteerBucketSupervisors");

            migrationBuilder.DropTable(
                name: "VolunteerCategoryPreferences");

            migrationBuilder.DropTable(
                name: "VolunteerDayAvailabilities");

            migrationBuilder.DropTable(
                name: "VolunteerHelpRequests");

            migrationBuilder.DropTable(
                name: "VolunteerPlanningRoster");

            migrationBuilder.DropTable(
                name: "VolunteerTaskAssignments");

            migrationBuilder.DropTable(
                name: "WebshopOrderArchives");

            migrationBuilder.DropTable(
                name: "WordPressRefreshPosts");

            migrationBuilder.DropTable(
                name: "ZohoOrderSyncRequests");

            migrationBuilder.DropTable(
                name: "ZohoTokenLeases");

            migrationBuilder.DropTable(
                name: "AllocationScenarios");

            migrationBuilder.DropTable(
                name: "CouponPrepaidAllocations");

            migrationBuilder.DropTable(
                name: "EvaluationSessions");

            migrationBuilder.DropTable(
                name: "EvaluationDevices");

            migrationBuilder.DropTable(
                name: "EventSoMePosts");

            migrationBuilder.DropTable(
                name: "MailCampaigns");

            migrationBuilder.DropTable(
                name: "QuizAttempts");

            migrationBuilder.DropTable(
                name: "QuizQuestions");

            migrationBuilder.DropTable(
                name: "Attendees");

            migrationBuilder.DropTable(
                name: "ShopOrders");

            migrationBuilder.DropTable(
                name: "ShopProducts");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "SponsorSessions");

            migrationBuilder.DropTable(
                name: "SponsorUploadLocations");

            migrationBuilder.DropTable(
                name: "SurveyResponses");

            migrationBuilder.DropTable(
                name: "VolumePackageCompanies");

            migrationBuilder.DropTable(
                name: "VolunteerTasks");

            migrationBuilder.DropTable(
                name: "CouponInvoicingSettings");

            migrationBuilder.DropTable(
                name: "EvaluationPhysicalRooms");

            migrationBuilder.DropTable(
                name: "EvaluationRooms");

            migrationBuilder.DropTable(
                name: "Quizzes");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "ShopCategories");

            migrationBuilder.DropTable(
                name: "VolunteerSubcategories");

            migrationBuilder.DropTable(
                name: "VolunteerCategories");

            migrationBuilder.DropTable(
                name: "Participants");

            migrationBuilder.DropTable(
                name: "Hotels");

            migrationBuilder.DropTable(
                name: "Events");
        }
    }
}
