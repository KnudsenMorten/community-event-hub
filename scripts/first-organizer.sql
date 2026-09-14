-- ===========================================================================
--  first-organizer.sql  -  Create your edition and the first organizer account
-- ---------------------------------------------------------------------------
--  Edit the values marked EDIT, then run it AFTER the web app has started once
--  (it creates the database schema on startup):
--     ./scripts/grant-db-access.sh dev --sql-file scripts/first-organizer.sql
--  Safe to re-run: nothing is inserted twice.
-- ===========================================================================
SET NOCOUNT ON;

DECLARE @Code        nvarchar(32)  = N'DEMO27';                          -- EDIT: matches edition.code in config/event.eldk27.json
DECLARE @Community   nvarchar(200) = N'Demo Community';                  -- EDIT
DECLARE @DisplayName nvarchar(200) = N'Demo Community Conference 2027';  -- EDIT
DECLARE @StartDate   date          = '2027-03-02';                       -- EDIT: first main day
DECLARE @EndDate     date          = '2027-03-03';                       -- EDIT: last day
DECLARE @PreDayDate  date          = '2027-03-01';                       -- EDIT: or NULL
DECLARE @Venue       nvarchar(200) = N'Riverside Convention Center';     -- EDIT
DECLARE @Hostname    nvarchar(255) = N'hub.your-event.example';          -- EDIT: the hub's hostname
DECLARE @Email       nvarchar(320) = N'you@your-event.example';          -- EDIT: your sign-in address
DECLARE @FullName    nvarchar(200) = N'Your Name';                       -- EDIT

DECLARE @Now datetimeoffset = SYSDATETIMEOFFSET();

IF NOT EXISTS (SELECT 1 FROM [Events] WHERE [Code] = @Code)
    INSERT INTO [Events] ([CommunityName], [Code], [DisplayName], [StartDate], [EndDate], [PreDayDate],
                          [VenueName], [HubHostname], [IsActive], [LockDate], [CreatedAt])
    VALUES (@Community, @Code, @DisplayName, @StartDate, @EndDate, @PreDayDate,
            @Venue, @Hostname, 1, NULL, @Now);

DECLARE @EventId int = (SELECT [Id] FROM [Events] WHERE [Code] = @Code);

-- Role 0 = Organizer. LifecycleState 2 = Active (sign-in needs IsActive AND Active).
-- Ring 0 = the earliest release ring, so your own account receives mail from day one.
IF NOT EXISTS (SELECT 1 FROM [Participants] WHERE [EventId] = @EventId AND [Email] = @Email)
    INSERT INTO [Participants] ([EventId], [Email], [FullName], [Role], [IsActive], [LifecycleState], [Ring], [CreatedAt])
    VALUES (@EventId, @Email, @FullName, 0, 1, 2, 0, @Now);

SELECT e.[Code], e.[IsActive], p.[Email], p.[Role], p.[LifecycleState]
FROM [Events] e JOIN [Participants] p ON p.[EventId] = e.[Id]
WHERE e.[Code] = @Code AND p.[Email] = @Email;
