namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// §767 — renders a SET OF EXAMPLE graphics covering every layout variant, so a human can compare
/// them side by side and choose.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-01: <i>"i consider this as a proof-of-concept where you can make different
/// examples, then i will decide. i have newer tried to automate this before. last year a graphic
/// person made them manually. i expect more changes to come."</i></para>
///
/// <para>🔑 <b>This is production code, not scaffolding.</b> The variants he is choosing between are
/// renderer PROPERTIES, so this service stays useful long after the choice is made: it is how the
/// layout is re-checked whenever the template, the palette or a font changes — the one thing a test
/// suite cannot do for a graphics feature is look at the result.</para>
///
/// <para>It writes files and nothing else — no SharePoint, no DB, no notification. The caller
/// decides where the folder is.</para>
/// </remarks>
public sealed class SoMeGraphicExampleService
{
    /// <summary>One rendered example: its file name and its bytes.</summary>
    public sealed record Example(string FileName, byte[] Content);

    // The real ELDK27 values, taken from config/event.eldk27.json rather than invented, so what he
    // is judging is the actual strip he will ship.
    private const string EventNameSample = "Experts Live Denmark 2027";
    private const string EventDatesSample = "9-10 Feb 2027";
    private const string EventLocationSample = "Copenhagen, Denmark";
    private const string SampleTitle =
        "Trusted data foundations for AI across Microsoft 365, Copilot and AI agents";

    /// <summary>
    /// Render every variant. <paramref name="secondPhoto"/> drives the multi-speaker GIFs; pass the
    /// same photo twice if only one is available.
    /// </summary>
    public IReadOnlyList<Example> Build(
        byte[] template, byte[] photo, byte[] secondPhoto, byte[] sponsorLogo,
        string speakerName = "Nikki Chapple", string secondSpeakerName = "Per Larsen")
    {
        var examples = new List<Example>();

        void Png(string name, SoMeGraphicRenderer r, Func<SoMeGraphicRenderer, byte[]> render) =>
            examples.Add(new Example(name, render(r)));

        // ---- speakers: one axis of variation per file, so a comparison isolates one change ----
        Png("A1-speaker-baseline.png", new SoMeGraphicRenderer(),
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("A2-speaker-two-tone-ring.png", new SoMeGraphicRenderer { TwoToneRing = true },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("A3-speaker-with-session-title.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = "Trusted data foundations for AI across Microsoft 365, Copilot and AI agents",
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("A4-speaker-badge-left.png",
            new SoMeGraphicRenderer { TwoToneRing = true, BadgeOnLeft = true },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("A5-speaker-darker-60.png",
            new SoMeGraphicRenderer { TwoToneRing = true, ToneDown = 0.60f },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("A6-speaker-lighter-30.png",
            new SoMeGraphicRenderer { TwoToneRing = true, ToneDown = 0.30f },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("A7-speaker-crop-high.png",
            new SoMeGraphicRenderer { TwoToneRing = true, CropAnchor = 0.15f },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        // A long, accented name — proves the wrap clears the badge instead of running under it.
        Png("A8-speaker-long-name.png", new SoMeGraphicRenderer { TwoToneRing = true },
            r => r.RenderSpeakerPng(template, secondPhoto, "Morten Bøtkjær-Nilsson"));

        // ---- multi-speaker GIFs: one frame per person ------------------------------------
        var pair = new (byte[]?, string)[] { (photo, speakerName), (secondPhoto, secondSpeakerName) };

        examples.Add(new Example("B1-session-two-speakers.gif",
            new SoMeGraphicRenderer { TwoToneRing = true }.RenderSpeakerGif(template, pair)));

        examples.Add(new Example("B2-masterclass-with-title.gif",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                BadgeLabel = "MASTER CLASS",
                Subtitle = "Intune Master Class",
            }.RenderSpeakerGif(template, pair)));

        // ---- sponsors ---------------------------------------------------------------------
        Png("C1-sponsor-baseline.png", new SoMeGraphicRenderer(),
            r => r.RenderSponsorPng(template, sponsorLogo));

        Png("C2-sponsor-with-tier.png",
            new SoMeGraphicRenderer { SponsorCaption = "Platinum sponsor" },
            r => r.RenderSponsorPng(template, sponsorLogo));

        Png("C3-sponsor-darker.png",
            new SoMeGraphicRenderer { SponsorCaption = "Platinum sponsor", ToneDown = 0.62f },
            r => r.RenderSponsorPng(template, sponsorLogo));

        // C1–C3 carry no event mark at all, which the ELDK26 sponsor layout DOES have (centred top).
        // Added as a fourth file rather than by changing C1, so the comparison he is already making
        // is not altered underneath him.
        Png("C4-sponsor-with-event-logo.png",
            new SoMeGraphicRenderer { SponsorCaption = "Platinum sponsor", SponsorEventLogo = true },
            r => r.RenderSponsorPng(template, sponsorLogo));

        // ---- the white knockout, on the two layouts he chose (A3 + C2) ---------------------
        // "amazing a3, c2. but try example with our white logo instead. not blue" — so these two
        // differ from A3/C2 in the mark ALONE, which is the only way to judge the mark itself.
        Png("A9-speaker-white-logo.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                WhiteEventLogo = true,
                Subtitle = "Trusted data foundations for AI across Microsoft 365, Copilot and AI agents",
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("C5-sponsor-white-logo.png",
            new SoMeGraphicRenderer
            {
                SponsorCaption = "Platinum sponsor",
                SponsorEventLogo = true,
                WhiteEventLogo = true,
            },
            r => r.RenderSponsorPng(template, sponsorLogo));

        // ---- event name + dates ------------------------------------------------------------
        // "can we test adding dates and event name" — on A3, his chosen layout, so the strip is the
        // only difference. D2 isolates the dates alone, in case the name reads as repetition of the
        // logo already top-left.
        Png("D1-speaker-name-and-dates.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = SampleTitle,
                EventName = EventNameSample,
                EventDates = EventDatesSample,
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("D2-speaker-dates-only.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = SampleTitle,
                EventDates = EventDatesSample,
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("D3-sponsor-name-and-dates.png",
            new SoMeGraphicRenderer
            {
                SponsorCaption = "Platinum sponsor",
                SponsorEventLogo = true,
                EventName = EventNameSample,
                EventDates = EventDatesSample,
            },
            r => r.RenderSponsorPng(template, sponsorLogo));

        // ---- more speaker + session-title pairs --------------------------------------------
        // "make a couple of sample with session title and speaker name" — real-shaped titles at
        // different lengths, because the title is the element most likely to break the layout: a
        // short one leaves the column airy, a long one has to wrap without reaching the badge.
        Png("E1-speaker-short-title.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = "Intune Suite: what you actually get",
                EventName = EventNameSample,
                EventDates = EventDatesSample,
            },
            r => r.RenderSpeakerPng(template, secondPhoto, secondSpeakerName));

        Png("E2-speaker-long-title.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = "Defender for Endpoint and Sentinel together: building a detection pipeline "
                    + "that survives contact with a real incident",
                EventName = EventNameSample,
                EventDates = EventDatesSample,
            },
            r => r.RenderSpeakerPng(template, photo, "Morten Bøtkjær-Nilsson"));

        // ---- with the location added ------------------------------------------------------
        // "add Denmark also" / "what makes sense": the full when-and-where strip. F2 is the same
        // thing with the event NAME dropped, since the wordmark is already top-left — that is the
        // one real argument against carrying the name twice.
        Png("F1-speaker-name-dates-location.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = SampleTitle,
                EventName = EventNameSample,
                EventDates = EventDatesSample,
                EventLocation = EventLocationSample,
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("F2-speaker-dates-location.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = SampleTitle,
                EventDates = EventDatesSample,
                EventLocation = EventLocationSample,
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        Png("F3-sponsor-name-dates-location.png",
            new SoMeGraphicRenderer
            {
                SponsorCaption = "Platinum sponsor",
                SponsorEventLogo = true,
                EventName = EventNameSample,
                EventDates = EventDatesSample,
                EventLocation = EventLocationSample,
            },
            r => r.RenderSponsorPng(template, sponsorLogo));

        // ---- the restrained cut ------------------------------------------------------------
        // "it must be suitable for SoMe. i am worried of too much text makes it cramped and noisy."
        // He is right, and the fix is subtraction, not smaller type: the wordmark top-left ALREADY
        // says Experts Live Denmark, so repeating it in the strip buys nothing and costs the room
        // that made F1 collide with the badge. G1 is the recommendation — when and where only.
        // 🔒 G1/G3/G4 render through LockedDesign() — the approved configuration itself, not a copy
        // of its values. If the lock ever changes, these three move with it and cannot drift into
        // showing him something production does not do.
        var g1 = SoMeGraphicRenderer.LockedDesign();
        g1.Subtitle = SampleTitle;
        g1.EventDates = EventDatesSample;
        g1.EventLocation = EventLocationSample;
        examples.Add(new Example("G1-speaker-RECOMMENDED.png",
            g1.RenderSpeakerPng(template, photo, speakerName)));

        // The same thing with no session title, for a plain "X is speaking" post — the quietest the
        // speaker layout gets while still answering when and where.
        Png("G2-speaker-no-title.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                EventDates = EventDatesSample,
                EventLocation = EventLocationSample,
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        // The heavier tone-down that was here is GONE: it was compensating for last year's stage
        // screens, and the horizontal crop removes them outright instead of dimming them.
        var g3 = SoMeGraphicRenderer.LockedDesign();
        g3.SponsorCaption = "Platinum sponsor";
        g3.EventDates = EventDatesSample;
        g3.EventLocation = EventLocationSample;
        examples.Add(new Example("G3-sponsor-RECOMMENDED.png",
            g3.RenderSponsorPng(template, sponsorLogo)));

        // A SIX-speaker session, in the locked design. Rendered because "what happens with 6
        // speakers" is a question about weight and pacing, and both are things to measure and look
        // at rather than reason about: 6 frames is 12 seconds of loop, and a feed scroll may only
        // ever show the first one.
        var g5 = SoMeGraphicRenderer.LockedDesign();
        g5.Subtitle = "Panel: running a community conference on a volunteer budget";
        g5.EventDates = EventDatesSample;
        g5.EventLocation = EventLocationSample;
        examples.Add(new Example("G5-session-six-speakers.gif",
            g5.RenderSpeakerGif(template, new[]
            {
                (photo, speakerName), (secondPhoto, secondSpeakerName),
                (photo, "Ada Lovelace"), (secondPhoto, "Grace Hopper"),
                (photo, "Morten Bøtkjær-Nilsson"), (secondPhoto, "Jens Peter Andersen"),
            })));

        // The locked design as a GIF — "gif works flawless just need the design to be locked down",
        // so the multi-speaker path is rendered in the SAME treatment as G1, not a stale one.
        var g4 = SoMeGraphicRenderer.LockedDesign();
        g4.Subtitle = "Zero Trust in practice: identity, device and data together";
        g4.EventDates = EventDatesSample;
        g4.EventLocation = EventLocationSample;
        examples.Add(new Example("G4-session-two-speakers-RECOMMENDED.gif",
            g4.RenderSpeakerGif(template, pair)));

        // ---- the horizontal crop, at three strengths ---------------------------------------
        // "yes do the horizontal crop" — cutting last year's stage screens out of the photograph.
        // Rendered at three zooms because how far in is enough is a thing to LOOK at, not compute:
        // the screens span roughly the right 40% of the frame, so a timid crop leaves them.
        foreach (var (name, zoom) in new[]
                 {
                     ("H1-sponsor-crop-80", 0.80f),
                     ("H2-sponsor-crop-68", 0.68f),
                     ("H3-sponsor-crop-56", 0.56f),
                 })
        {
            Png($"{name}.png",
                new SoMeGraphicRenderer
                {
                    SponsorCaption = "Platinum sponsor",
                    SponsorEventLogo = true,
                    EventDates = EventDatesSample,
                    EventLocation = EventLocationSample,
                    CropZoom = zoom,
                    CropAnchorX = 0f,   // keep the LEFT of the frame; the screens are on the right
                },
                r => r.RenderSponsorPng(template, sponsorLogo));
        }

        // The same crop on the speaker layout, so the two surfaces stay one photograph.
        Png("H4-speaker-crop-68.png",
            new SoMeGraphicRenderer
            {
                TwoToneRing = true,
                Subtitle = SampleTitle,
                EventDates = EventDatesSample,
                EventLocation = EventLocationSample,
                CropZoom = 0.68f,
                CropAnchorX = 0f,
            },
            r => r.RenderSpeakerPng(template, photo, speakerName));

        return examples;
    }

    /// <summary>Render every variant and write it into <paramref name="folder"/>.</summary>
    public IReadOnlyList<string> WriteTo(
        string folder, byte[] template, byte[] photo, byte[] secondPhoto, byte[] sponsorLogo)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (var e in Build(template, photo, secondPhoto, sponsorLogo))
        {
            var path = Path.Combine(folder, e.FileName);
            File.WriteAllBytes(path, e.Content);
            written.Add(path);
        }
        return written;
    }
}
