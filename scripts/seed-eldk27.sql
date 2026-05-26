-- ===========================================================================
--  CommunityHub - seed data for the ELDK27 edition
-- ---------------------------------------------------------------------------
--  Run this AFTER `dotnet ef database update` has created the schema.
--
--  TEST ACCOUNTS (real addresses - these receive the PIN sign-in email):
--    Organizer  : mok@expertslive.dk
--    Speaker    : mok@mortenknudsen.net
--    Volunteer  : mortenknudsen1974@gmail.com
--    Sponsor    : mok@2linkit.net           (company 2LINKIT)
--  Plus two example rows (Masterclass Speaker, Attendee) on example.com -
--  change them if you want to test those flows too.
--
--  ParticipantRole enum -> int (see ParticipantRole.cs):
--    0 Organizer  1 Speaker  2 MasterclassSpeaker
--    3 Volunteer  4 Sponsor  5 Attendee
--
--  Idempotent: re-running will not duplicate rows (guarded by NOT EXISTS).
-- ===========================================================================

SET NOCOUNT ON;

-- --- Event -----------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM [Events] WHERE [Code] = N'ELDK27')
BEGIN
    INSERT INTO [Events]
        ([CommunityName], [Code], [DisplayName],
         [StartDate], [EndDate], [PreDayDate],
         [VenueName], [HubHostname], [IsActive], [LockDate], [CreatedAt])
    VALUES
        (N'Experts Live Denmark', N'ELDK27',
         N'Experts Live Denmark 2027',
         '2027-02-09', '2027-02-10', '2027-02-08',
         N'Bella Center Copenhagen',
         N'hub.eldk27.expertslive.dk',
         1,                       -- IsActive: this is the active edition
         '2027-01-20',            -- LockDate: forms read-only after this
         SYSDATETIMEOFFSET());
END;

DECLARE @EventId INT =
    (SELECT [Id] FROM [Events] WHERE [Code] = N'ELDK27');

-- --- Test participants -----------------------------------------------------
--  Email is the login identity; it must be unique within the edition.
--  The Sponsor row carries SponsorCompanyId 'test-2linkit' so the
--  company-scoped sponsor area works and matches TESTMODE's test sponsor.

DECLARE @People TABLE (
    Email NVARCHAR(320),
    FullName NVARCHAR(200),
    Role INT,
    SponsorCompanyId NVARCHAR(64));

INSERT INTO @People (Email, FullName, Role, SponsorCompanyId) VALUES
    (N'mok@expertslive.dk',          N'Morten Knudsen (Organizer)',  0, NULL),
    (N'mok@mortenknudsen.net',       N'Morten Knudsen (Speaker)',    1, NULL),
    (N'mcspeaker@example.com',       N'Test Masterclass Speaker',    2, NULL),
    (N'mortenknudsen1974@gmail.com', N'Morten Knudsen (Volunteer)',  3, NULL),
    (N'mok@2linkit.net',             N'Morten Knudsen (2LINKIT)',    4, N'test-2linkit'),
    (N'attendee@example.com',        N'Test Attendee',               5, NULL);

INSERT INTO [Participants]
    ([EventId], [Email], [FullName], [Phone], [Role],
     [SponsorCompanyId], [IsActive], [CreatedAt])
SELECT
    @EventId, p.Email, p.FullName, NULL, p.Role,
    p.SponsorCompanyId, 1, SYSDATETIMEOFFSET()
FROM @People p
WHERE NOT EXISTS (
    SELECT 1 FROM [Participants] x
    WHERE x.[EventId] = @EventId AND x.[Email] = p.Email);

-- --- Sample sponsor task for the 2LINKIT Sponsor account -------------------
--  SourceKey starts "woo:" and SponsorCompanyId matches the sponsor's, so the
--  company-scoped sponsor page picks it up. Idempotent on SourceKey.
IF NOT EXISTS (
    SELECT 1 FROM [Tasks]
    WHERE [EventId] = @EventId AND [SourceKey] = N'woo:seed:2linkit:logo')
BEGIN
    INSERT INTO [Tasks]
        ([EventId], [AssignedParticipantId], [Title], [Description],
         [DueDate], [State], [SourceKey], [SponsorCompanyId], [CreatedAt])
    VALUES
        (@EventId, NULL,
         N'Upload company logo in vector format',
         N'Seed task so the 2LINKIT sponsor area can be tested.',
         '2027-01-15', 0,                 -- State 0 = Open
         N'woo:seed:2linkit:logo', N'test-2linkit', SYSDATETIMEOFFSET());
END;

-- --- Sample task for the Speaker account -----------------------------------
DECLARE @SpeakerId INT =
    (SELECT [Id] FROM [Participants]
     WHERE [EventId] = @EventId AND [Email] = N'mok@mortenknudsen.net');

IF @SpeakerId IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM [Tasks]
    WHERE [EventId] = @EventId AND [SourceKey] = N'seed:speaker:abstract')
BEGIN
    INSERT INTO [Tasks]
        ([EventId], [AssignedParticipantId], [Title], [Description],
         [DueDate], [State], [SourceKey], [SponsorCompanyId], [CreatedAt])
    VALUES
        (@EventId, @SpeakerId,
         N'Submit session title and abstract',
         N'Seed task so the Speaker task list can be tested.',
         '2027-01-10', 0,
         N'seed:speaker:abstract', NULL, SYSDATETIMEOFFSET());
END;

-- --- Result ----------------------------------------------------------------
SELECT
    (SELECT COUNT(*) FROM [Events] WHERE [Code] = N'ELDK27')         AS Events,
    (SELECT COUNT(*) FROM [Participants] WHERE [EventId] = @EventId) AS Participants,
    (SELECT COUNT(*) FROM [Tasks] WHERE [EventId] = @EventId)        AS Tasks;

PRINT 'Seed complete. Sign in at /Login with one of these emails:';
PRINT '  Organizer : mok@expertslive.dk';
PRINT '  Speaker   : mok@mortenknudsen.net';
PRINT '  Volunteer : mortenknudsen1974@gmail.com';
PRINT '  Sponsor   : mok@2linkit.net';
