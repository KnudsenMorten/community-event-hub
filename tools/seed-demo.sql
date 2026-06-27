-- ===========================================================================
--  CommunityHub - SYNTHETIC demo seed for local screenshot capture.
--  Fictional "Demo Community Conf" with invented people + sponsor companies.
--  NO real customer/personal data. Idempotent (guarded by NOT EXISTS / re-asserts).
--  Apply AFTER `dotnet ef database update` (the app auto-migrates on startup).
--    sqlcmd -S .\SQLEXPRESS -E -d CommunityHubDemo -i tools/seed-demo.sql
-- ===========================================================================
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- --- Event -----------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM [Events] WHERE [Code] = N'DEMO')
BEGIN
    INSERT INTO [Events]
        ([CommunityName],[Code],[DisplayName],[StartDate],[EndDate],[PreDayDate],
         [VenueName],[HubHostname],[IsActive],[LockDate],[CreatedAt])
    VALUES
        (N'Demo Community Conf', N'DEMO', N'Demo Community Conf 2027',
         '2027-09-14','2027-09-15','2027-09-13',
         N'Riverside Convention Center', N'democonf.example.com',
         1, NULL, SYSDATETIMEOFFSET());
END
ELSE
    UPDATE [Events] SET [CommunityName]=N'Demo Community Conf',
        [DisplayName]=N'Demo Community Conf 2027', [IsActive]=1 WHERE [Code]=N'DEMO';

DECLARE @E INT = (SELECT [Id] FROM [Events] WHERE [Code]=N'DEMO');

-- --- Participants (one per role + extra speakers) ---------------------------
DECLARE @P TABLE (Email NVARCHAR(320), FullName NVARCHAR(200), Role INT, Company NVARCHAR(64));
INSERT INTO @P VALUES
    (N'organizer@democonf.example.com', N'Alex Rivera',  0, NULL),
    (N'speaker@democonf.example.com',   N'Sam Taylor',   1, NULL),
    (N'jordan@democonf.example.com',    N'Jordan Lee',   1, NULL),
    (N'volunteer@democonf.example.com', N'Priya Nair',   3, NULL),
    (N'sponsor@democonf.example.com',   N'Chris Bauer',  4, N'contoso'),
    (N'attendee@democonf.example.com',  N'Robin Morgan', 5, NULL);

INSERT INTO [Participants]
    ([EventId],[Email],[FullName],[Phone],[Role],[SponsorCompanyId],[IsActive],[IsTestUser],[LifecycleState],[CreatedAt])
SELECT @E, p.Email, p.FullName, NULL, p.Role, p.Company, 1, 1, 2, SYSDATETIMEOFFSET()
FROM @P p
WHERE NOT EXISTS (SELECT 1 FROM [Participants] x WHERE x.[EventId]=@E AND x.[Email]=p.Email);

-- LifecycleState 2 = Active (login requires IsActive AND LifecycleState=Active).
UPDATE x SET x.[FullName]=p.FullName, x.[Role]=p.Role, x.[SponsorCompanyId]=p.Company,
             x.[IsActive]=1, x.[IsTestUser]=1, x.[LifecycleState]=2
FROM [Participants] x JOIN @P p ON p.Email=x.[Email] WHERE x.[EventId]=@E;

DECLARE @Speaker  INT = (SELECT Id FROM Participants WHERE EventId=@E AND Email=N'speaker@democonf.example.com');
DECLARE @Speaker2 INT = (SELECT Id FROM Participants WHERE EventId=@E AND Email=N'jordan@democonf.example.com');
DECLARE @Vol      INT = (SELECT Id FROM Participants WHERE EventId=@E AND Email=N'volunteer@democonf.example.com');
DECLARE @Attendee INT = (SELECT Id FROM Participants WHERE EventId=@E AND Email=N'attendee@democonf.example.com');

-- --- Speaker profiles -------------------------------------------------------
MERGE [SpeakerProfiles] AS t
USING (VALUES
    (@Speaker,  N'Sam',   N'Taylor', N'Automation enthusiast & community speaker',
        N'Sam Taylor builds practical automation for IT teams and loves sharing hands-on demos with the community.', 1),
    (@Speaker2, N'Jordan',N'Lee',    N'Cloud security advocate',
        N'Jordan Lee helps organisations adopt secure-by-default cloud practices and speaks regularly at community events.', 1)
) AS s(Pid,FN,LN,Tag,Bio,Pub)
ON t.EventId=@E AND t.ParticipantId=s.Pid
WHEN NOT MATCHED THEN INSERT
    ([EventId],[ParticipantId],[FirstName],[LastName],[Tagline],[Biography],[SelectedForPublish],[CreatedAt])
    VALUES (@E,s.Pid,s.FN,s.LN,s.Tag,s.Bio,s.Pub,SYSDATETIMEOFFSET())
WHEN MATCHED THEN UPDATE SET t.Tagline=s.Tag, t.Biography=s.Bio, t.SelectedForPublish=s.Pub;

-- --- Sessions + speaker links ----------------------------------------------
DECLARE @S TABLE (Sid NVARCHAR(64), Title NVARCHAR(200), Room NVARCHAR(64), Track NVARCHAR(64),
                  StartsAt DATETIMEOFFSET, Mins INT, Owner INT);
INSERT INTO @S VALUES
    (N'demo-s1', N'Automating Everything with Pipelines', N'Hall A', N'Automation',
        '2027-09-14 10:00 +00:00', 60, @Speaker),
    (N'demo-s2', N'Hands-on Lab: Infrastructure as Code',  N'Lab 1',  N'Automation',
        '2027-09-14 13:30 +00:00', 90, @Speaker),
    (N'demo-s3', N'Securing the Modern Cloud',             N'Hall B', N'Security',
        '2027-09-15 09:30 +00:00', 60, @Speaker2);

INSERT INTO [Sessions]
    ([EventId],[SessionizeId],[Title],[Abstract],[Room],[Track],[StartsAt],[EndsAt],
     [IsServiceSession],[CreatedAt],[Length],[Type])
SELECT @E, s.Sid, s.Title,
    N'A practical, demo-driven session for the Demo Community Conf audience.',
    s.Room, s.Track, s.StartsAt, DATEADD(MINUTE, s.Mins, s.StartsAt), 0, SYSDATETIMEOFFSET(), s.Mins, 0
FROM @S s
WHERE NOT EXISTS (SELECT 1 FROM [Sessions] x WHERE x.EventId=@E AND x.SessionizeId=s.Sid);

INSERT INTO [SessionSpeakers] ([SessionId],[ParticipantId])
SELECT se.Id, s.Owner
FROM @S s JOIN [Sessions] se ON se.EventId=@E AND se.SessionizeId=s.Sid
WHERE NOT EXISTS (SELECT 1 FROM [SessionSpeakers] x WHERE x.SessionId=se.Id AND x.ParticipantId=s.Owner);

-- --- Sponsor company + booth ------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM [SponsorInfos] WHERE EventId=@E AND SponsorCompanyId=N'contoso')
    INSERT INTO [SponsorInfos]
        ([EventId],[SponsorCompanyId],[CompanyDescription],[CompanyDescriptionShort],
         [CreatedAt],[Tier],[SponsorPackage],[WebsiteUrl],[BoothLabel],
         [EventCoordinatorFirstName],[EventCoordinatorLastName],[EventCoordinatorEmail])
    VALUES (@E,N'contoso',
        N'Contoso builds developer tooling and cloud platforms used by teams worldwide.',
        N'Developer tooling & cloud platforms.',
        SYSDATETIMEOFFSET(), 0, 1, N'https://contoso.example.com', N'B-12',
        N'Chris', N'Bauer', N'sponsor@democonf.example.com');
ELSE
    UPDATE [SponsorInfos] SET [SponsorPackage]=1, [BoothLabel]=N'B-12',
        [CompanyDescription]=N'Contoso builds developer tooling and cloud platforms used by teams worldwide.'
    WHERE EventId=@E AND SponsorCompanyId=N'contoso';

-- a second sponsor company so directory/listing views have content
IF NOT EXISTS (SELECT 1 FROM [SponsorInfos] WHERE EventId=@E AND SponsorCompanyId=N'fabrikam')
    INSERT INTO [SponsorInfos]
        ([EventId],[SponsorCompanyId],[CompanyDescriptionShort],[CreatedAt],[Tier],[SponsorPackage],[BoothLabel])
    VALUES (@E,N'fabrikam',N'Data & analytics solutions.',SYSDATETIMEOFFSET(),0,2,N'B-08');

-- booth members
INSERT INTO [SponsorBoothMembers]
    ([EventId],[SponsorCompanyId],[FirstName],[LastName],[Email],[Role],[SyncedToZoho],[CreatedAt])
SELECT @E,N'contoso',v.FN,v.LN,v.Em,v.R,0,SYSDATETIMEOFFSET()
FROM (VALUES (N'Chris',N'Bauer',N'sponsor@democonf.example.com',0),
             (N'Dana',N'Khan',N'dana@contoso.example.com',1)) v(FN,LN,Em,R)
WHERE NOT EXISTS (SELECT 1 FROM [SponsorBoothMembers] x WHERE x.EventId=@E AND x.Email=v.Em);

-- booth materials
INSERT INTO [SponsorBoothMaterials]
    ([EventId],[SponsorCompanyId],[Kind],[Url],[FileName],[CreatedAt])
SELECT @E,N'contoso',v.K,v.U,v.F,SYSDATETIMEOFFSET()
FROM (VALUES (1,N'https://contoso.example.com/brochure.pdf',N'brochure.pdf'),
             (0,N'https://contoso.example.com/booth-loop.mp4',N'booth-loop.mp4')) v(K,U,F)
WHERE NOT EXISTS (SELECT 1 FROM [SponsorBoothMaterials] x WHERE x.EventId=@E AND x.Url=v.U);

-- sponsor tasks (company-scoped)
INSERT INTO [Tasks]
    ([EventId],[AssignedParticipantId],[Title],[Description],[DueDate],[State],[SourceKey],[SponsorCompanyId],[CreatedAt],[CompletedAt],[IsMandatory])
SELECT @E,NULL,v.T,v.D,v.Due,v.St,v.SK,N'contoso',SYSDATETIMEOFFSET(),
       CASE WHEN v.St=2 THEN SYSDATETIMEOFFSET() ELSE NULL END, 1
FROM (VALUES
    (N'Upload company logo (vector)', N'Provide your logo in SVG/EPS for signage.', '2027-08-15', 2, N'demo:contoso:logo'),
    (N'Submit booth staff list',      N'Tell us who will staff the booth.',         '2027-08-20', 2, N'demo:contoso:staff'),
    (N'Provide booth description',    N'Short description shown in the expo guide.', '2027-08-25', 0, N'demo:contoso:desc'),
    (N'Order extra power & monitor',  N'Optional booth add-ons via the webshop.',    '2027-09-01', 0, N'demo:contoso:addons')
) v(T,D,Due,St,SK)
WHERE NOT EXISTS (SELECT 1 FROM [Tasks] x WHERE x.EventId=@E AND x.SourceKey=v.SK);

-- --- Speaker tasks (unified /Tasks checklist %) ----------------------------
INSERT INTO [Tasks]
    ([EventId],[AssignedParticipantId],[Title],[Description],[DueDate],[State],[SourceKey],[CreatedAt],[CompletedAt],[IsMandatory])
SELECT @E,@Speaker,v.T,v.D,v.Due,v.St,v.SK,SYSDATETIMEOFFSET(),
       CASE WHEN v.St=2 THEN SYSDATETIMEOFFSET() ELSE NULL END, 1
FROM (VALUES
    (N'Submit session title and abstract', N'Confirm the details for your talk.',     '2027-07-10', 2, N'demo:speaker:abstract'),
    (N'Confirm your bio and photo',        N'Used on the public speaker page.',       '2027-07-20', 2, N'demo:speaker:bio'),
    (N'Upload your final slide deck',      N'PDF export, 16:9.',                       '2027-09-05', 0, N'demo:speaker:slides'),
    (N'Book your hotel',                   N'Reserve your room for the conference.',   '2027-08-01', 2, N'demo:speaker:hotel'),
    (N'Reply to the appreciation dinner',  N'Let us know if you can join.',           '2027-08-10', 0, N'demo:speaker:dinner'),
    (N'Submit travel reimbursement',       N'Upload ticket and invoice to be repaid.','2027-09-20', 0, N'demo:speaker:travel')
) v(T,D,Due,St,SK)
WHERE NOT EXISTS (SELECT 1 FROM [Tasks] x WHERE x.EventId=@E AND x.SourceKey=v.SK);

-- --- Volunteer structure + tasks + shifts ----------------------------------
IF NOT EXISTS (SELECT 1 FROM [VolunteerCategories] WHERE EventId=@E AND Name=N'Registration Desk')
    INSERT INTO [VolunteerCategories] ([EventId],[Name],[Description],[CreatedAt])
    VALUES (@E,N'Registration Desk',N'Welcome and check in attendees.',SYSDATETIMEOFFSET());
IF NOT EXISTS (SELECT 1 FROM [VolunteerCategories] WHERE EventId=@E AND Name=N'Session Hosting')
    INSERT INTO [VolunteerCategories] ([EventId],[Name],[Description],[CreatedAt])
    VALUES (@E,N'Session Hosting',N'Introduce speakers and manage rooms.',SYSDATETIMEOFFSET());

DECLARE @CatReg INT = (SELECT Id FROM VolunteerCategories WHERE EventId=@E AND Name=N'Registration Desk');
DECLARE @CatHost INT = (SELECT Id FROM VolunteerCategories WHERE EventId=@E AND Name=N'Session Hosting');

IF NOT EXISTS (SELECT 1 FROM [VolunteerSubcategories] WHERE EventId=@E AND Name=N'Main Entrance')
    INSERT INTO [VolunteerSubcategories] ([EventId],[CategoryId],[Name],[Description],[CreatedAt])
    VALUES (@E,@CatReg,N'Main Entrance',N'Front desk by the main doors.',SYSDATETIMEOFFSET());
IF NOT EXISTS (SELECT 1 FROM [VolunteerSubcategories] WHERE EventId=@E AND Name=N'Hall A Hosts')
    INSERT INTO [VolunteerSubcategories] ([EventId],[CategoryId],[Name],[Description],[CreatedAt])
    VALUES (@E,@CatHost,N'Hall A Hosts',N'Room hosting for Hall A.',SYSDATETIMEOFFSET());

DECLARE @SubEnt INT = (SELECT Id FROM VolunteerSubcategories WHERE EventId=@E AND Name=N'Main Entrance');
DECLARE @SubHall INT = (SELECT Id FROM VolunteerSubcategories WHERE EventId=@E AND Name=N'Hall A Hosts');

INSERT INTO [VolunteerTasks]
    ([EventId],[SubcategoryId],[Title],[Description],[DueDate],[Shift],[Status],[CreatedAt],[ResourcesNeeded],[ResponsibleTeam],[Criticality],[TimeEnd],[ExternalKey])
SELECT @E,v.Sub,v.T,v.D,'2027-09-14',v.Shift,0,SYSDATETIMEOFFSET(),v.Need,v.Team,v.Crit,v.TEnd,NEWID()
FROM (VALUES
    (@SubEnt, N'Greet & check in attendees',  N'Scan badges and welcome guests at the main entrance.', N'Day 1 - 08:00', 3, N'Core Team', 2, N'10:00'),
    (@SubEnt, N'Hand out lanyards & swag',     N'Distribute welcome bags at the desk.',                 N'Day 1 - 08:30', 2, N'Core Team', 1, N'10:30'),
    (@SubHall,N'Introduce speakers in Hall A', N'Welcome the room and keep sessions on time.',          N'Day 1 - 09:45', 2, N'Core Team', 2, N'12:00'),
    (@SubHall,N'Run the Q&A mic in Hall A',    N'Pass the microphone during audience questions.',       N'Day 1 - 13:15', 1, N'Core Team', 1, N'15:00')
) v(Sub,T,D,Shift,Need,Team,Crit,TEnd)
WHERE NOT EXISTS (SELECT 1 FROM [VolunteerTasks] x WHERE x.EventId=@E AND x.Title=v.T);

-- assign two shifts to the volunteer (one confirmed, one pending)
INSERT INTO [VolunteerTaskAssignments]
    ([EventId],[TaskId],[ParticipantId],[AssignedByEmail],[CreatedAt],[DecisionStatus],[DecisionAt])
SELECT @E, vt.Id, @Vol, N'organizer@democonf.example.com', SYSDATETIMEOFFSET(), v.DS,
       CASE WHEN v.DS=1 THEN SYSDATETIMEOFFSET() ELSE NULL END
FROM (VALUES (N'Greet & check in attendees',1),(N'Introduce speakers in Hall A',0)) v(T,DS)
JOIN [VolunteerTasks] vt ON vt.EventId=@E AND vt.Title=v.T
WHERE NOT EXISTS (SELECT 1 FROM [VolunteerTaskAssignments] x WHERE x.EventId=@E AND x.TaskId=vt.Id AND x.ParticipantId=@Vol);

-- volunteer availability (so the wizard availability step + schedule show content)
IF NOT EXISTS (SELECT 1 FROM [VolunteerAvailabilities] WHERE EventId=@E AND ParticipantId=@Vol)
    INSERT INTO [VolunteerAvailabilities]
        ([EventId],[ParticipantId],[SelectedShifts],[PreferredRole],[MaxHoursPerDay],[CreatedAt])
    VALUES (@E,@Vol,N'Day 1 morning, Day 1 afternoon',N'Registration Desk',6,SYSDATETIMEOFFSET());

INSERT INTO [VolunteerDayAvailabilities] ([EventId],[ParticipantId],[Day],[Level],[Note],[UpdatedAt])
SELECT @E,@Vol,v.D,v.L,NULL,SYSDATETIMEOFFSET()
FROM (VALUES ('2027-09-14',0),('2027-09-15',1)) v(D,L)
WHERE NOT EXISTS (SELECT 1 FROM [VolunteerDayAvailabilities] x WHERE x.EventId=@E AND x.ParticipantId=@Vol AND x.Day=v.D);

-- --- Attendee + master-class booking ----------------------------------------
IF NOT EXISTS (SELECT 1 FROM [Attendees] WHERE EventId=@E AND Email=N'attendee@democonf.example.com')
    INSERT INTO [Attendees]
        ([EventId],[Email],[FirstName],[LastName],[FullName],[TicketStatus],[TicketClassName],
         [BookingStatus],[MasterClassName],[HasReconciliationMismatch],[LastSyncedAt],[CreatedAt],[CompanyName],[JobTitle])
    VALUES (@E,N'attendee@democonf.example.com',N'Robin',N'Morgan',N'Robin Morgan',1,N'2-Day Conference Pass',
            1,N'Hands-on Lab: Infrastructure as Code',0,SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET(),N'Northwind',N'Platform Engineer');

DECLARE @AttRow INT = (SELECT Id FROM Attendees WHERE EventId=@E AND Email=N'attendee@democonf.example.com');
DECLARE @McSession INT = (SELECT Id FROM Sessions WHERE EventId=@E AND SessionizeId=N'demo-s2');

IF @McSession IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [MasterClassSignups] WHERE EventId=@E AND AttendeeId=@AttRow)
    INSERT INTO [MasterClassSignups]
        ([EventId],[SessionId],[AttendeeId],[Status],[CreatedAt],[UpdatedAt],[ConfirmedAt])
    VALUES (@E,@McSession,@AttRow,0,SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET(),SYSDATETIMEOFFSET());

-- attendee saved sessions (personal agenda)
INSERT INTO [SavedSessions] ([EventId],[ParticipantId],[SessionId],[CreatedAt])
SELECT @E,@Attendee,se.Id,SYSDATETIMEOFFSET()
FROM [Sessions] se WHERE se.EventId=@E AND se.SessionizeId IN (N'demo-s1',N'demo-s3')
  AND NOT EXISTS (SELECT 1 FROM [SavedSessions] x WHERE x.EventId=@E AND x.ParticipantId=@Attendee AND x.SessionId=se.Id);

-- --- Result ----------------------------------------------------------------
SELECT
    (SELECT COUNT(*) FROM Participants WHERE EventId=@E)              AS Participants,
    (SELECT COUNT(*) FROM Sessions WHERE EventId=@E)                 AS Sessions,
    (SELECT COUNT(*) FROM SponsorInfos WHERE EventId=@E)             AS Sponsors,
    (SELECT COUNT(*) FROM VolunteerTasks WHERE EventId=@E)           AS VolunteerTasks,
    (SELECT COUNT(*) FROM Tasks WHERE EventId=@E)                    AS Tasks,
    (SELECT COUNT(*) FROM Attendees WHERE EventId=@E)                AS Attendees;
