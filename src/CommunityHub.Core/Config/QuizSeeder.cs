using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Config;

/// <summary>
/// Seeds the §171 STARTER quiz pool so a fresh database has playable games: three
/// quizzes — AI, Intune, Security — each with a generic, genuinely educational pool
/// (no customer or person names, ever). Mirrors <see cref="PartyTaskSeeder"/>:
/// idempotent (keyed on <see cref="Quiz.Slug"/> per edition), safe to call on every
/// hub/organizer page-load. Once a quiz exists its questions are NOT re-seeded, so
/// the organizer's edits/extensions are never clobbered — the pool is fully editable
/// afterwards in /Organizer/Quizzes.
/// </summary>
public sealed class QuizSeeder
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public QuizSeeder(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Default play knobs for a shipped quiz (pool of 12, draw 8, 20s/question).</summary>
    public const int DefaultQuestionsPerAttempt = 8;
    public const int DefaultPerQuestionSeconds = 20;
    public const int DefaultBasePoints = 1000;

    /// <summary>
    /// Ensure the three starter quizzes + their pools exist for the edition. Returns
    /// the number of quizzes CREATED (0 when all already present). Idempotent.
    /// </summary>
    public async Task<int> SeedAsync(int eventId, CancellationToken ct = default)
    {
        var existingSlugs = (await _db.Quizzes
                .Where(q => q.EventId == eventId)
                .Select(q => q.Slug)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var now = _clock.GetUtcNow();
        var created = 0;
        var sortOrder = 0;
        foreach (var seed in Pool)
        {
            sortOrder++;
            if (existingSlugs.Contains(seed.Slug)) continue; // idempotent — never re-seed

            var quiz = new Quiz
            {
                EventId = eventId,
                Topic = seed.Topic,
                Title = seed.Title,
                Slug = seed.Slug,
                IsActive = true,
                QuestionsPerAttempt = DefaultQuestionsPerAttempt,
                PerQuestionSeconds = DefaultPerQuestionSeconds,
                BasePoints = DefaultBasePoints,
                SortOrder = sortOrder,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var order = 0;
            foreach (var q in seed.Questions)
            {
                quiz.Questions.Add(new QuizQuestion
                {
                    Prompt = q.Prompt,
                    Options = q.Options,
                    CorrectIndex = q.Correct,
                    Explanation = q.Explanation,
                    IsActive = true,
                    SortOrder = order++,
                });
            }
            _db.Quizzes.Add(quiz);
            created++;
        }

        if (created > 0) await _db.SaveChangesAsync(ct);
        return created;
    }

    private sealed record SeedQuestion(string Prompt, string[] Options, int Correct, string Explanation);
    private sealed record SeedQuiz(QuizTopic Topic, string Slug, string Title, SeedQuestion[] Questions);

    // The shipped pool — generic & educational; NO customer or person names. Each pool
    // is larger than the per-attempt draw so two players rarely get the same set.
    private static readonly SeedQuiz[] Pool =
    {
        new(QuizTopic.Ai, "ai", "AI Basics", new[]
        {
            new SeedQuestion("What does \"LLM\" stand for?",
                new[] { "Large Language Model", "Logical Learning Machine", "Linear Learning Model", "Layered Logic Module" }, 0,
                "LLM = Large Language Model: a model trained on huge amounts of text to predict and generate language."),
            new SeedQuestion("In generative AI, what is a \"prompt\"?",
                new[] { "The model's training data", "The instruction/input you give the model", "A billing unit", "A type of GPU" }, 1,
                "A prompt is the text/instructions you give the model; clearer prompts give better, more relevant answers."),
            new SeedQuestion("What is an AI \"hallucination\"?",
                new[] { "A model crash", "A confident but false or fabricated answer", "An image-only output", "A slow response" }, 1,
                "Models can state wrong information confidently — always verify important facts they produce."),
            new SeedQuestion("Microsoft 365 Copilot grounds its answers primarily in…",
                new[] { "Random web pages", "Your organization's data via Microsoft Graph", "The model's memory of you", "Public social media" }, 1,
                "Copilot combines the LLM with your tenant's data (mail, files, chats) through Microsoft Graph, respecting permissions."),
            new SeedQuestion("Which practice gives BETTER results from an AI assistant?",
                new[] { "Vague one-word prompts", "Clear context and specific instructions", "ALL CAPS", "Asking the same thing repeatedly" }, 1,
                "Specific context, role, format and constraints help the model produce what you actually need."),
            new SeedQuestion("What is a \"token\" when talking about LLMs?",
                new[] { "A login secret", "A chunk of text (word piece) the model processes", "A GPU core", "A licence key" }, 1,
                "Models read and generate text in tokens (roughly word-pieces); usage and limits are measured in tokens."),
            new SeedQuestion("What does \"RAG\" stand for in AI systems?",
                new[] { "Retrieval-Augmented Generation", "Rapid Answer Generator", "Recursive AI Graph", "Random Access Grounding" }, 0,
                "RAG retrieves relevant documents first, then has the model generate an answer grounded in them — reducing hallucination."),
            new SeedQuestion("The \"temperature\" setting on an LLM controls…",
                new[] { "Server cooling", "The randomness/creativity of the output", "The token price", "The context size" }, 1,
                "Lower temperature = more focused/deterministic; higher = more varied/creative output."),
            new SeedQuestion("Which Responsible AI principle is about avoiding unfair bias?",
                new[] { "Fairness", "Throughput", "Latency", "Caching" }, 0,
                "Fairness is a core Responsible AI principle — systems should treat people equitably and avoid harmful bias."),
            new SeedQuestion("Why should you review AI-generated content before relying on it?",
                new[] { "It is always perfect", "It can be inaccurate, biased, or outdated", "To slow things down", "It is required by law everywhere" }, 1,
                "AI output is a draft, not ground truth — a human should check accuracy, context and tone."),
            new SeedQuestion("Best practice with confidential data and public AI chatbots?",
                new[] { "Paste it freely", "Avoid entering it into tools your organization hasn't approved", "Email it first", "Only at night" }, 1,
                "Sensitive data may be stored or used for training by unapproved tools — use sanctioned, data-protected services."),
            new SeedQuestion("\"Fine-tuning\" a model means…",
                new[] { "Cleaning the screen", "Further training it on specific data to specialize it", "Deleting the model", "Lowering the price" }, 1,
                "Fine-tuning adapts a base model to a narrower task/domain by training it further on targeted examples."),
        }),

        new(QuizTopic.Intune, "intune", "Microsoft Intune", new[]
        {
            new SeedQuestion("Microsoft Intune is primarily a…",
                new[] { "Spreadsheet app", "Cloud-based endpoint & mobile device management service", "Firewall appliance", "Backup tape system" }, 1,
                "Intune is a cloud MDM/MAM service for managing devices and apps across Windows, iOS, Android and macOS."),
            new SeedQuestion("What does \"MDM\" stand for?",
                new[] { "Mobile Device Management", "Managed Domain Mode", "Multi-Disk Mirroring", "Microsoft Data Migration" }, 0,
                "MDM = Mobile Device Management: enrolling and managing whole devices (settings, compliance, wipe)."),
            new SeedQuestion("An Intune \"compliance policy\" defines…",
                new[] { "The Wi-Fi password", "Rules a device must meet (encryption, OS version, etc.)", "The user's salary", "A printer queue" }, 1,
                "Compliance policies set the bar a device must meet; non-compliant devices can be blocked via Conditional Access."),
            new SeedQuestion("Conditional Access works with Intune to…",
                new[] { "Defragment disks", "Grant or block access based on device compliance and risk", "Install games", "Speed up the CPU" }, 1,
                "Conditional Access uses Intune's compliance signal to allow access only from healthy, compliant devices."),
            new SeedQuestion("What does \"MAM\" stand for?",
                new[] { "Mobile Application Management", "Managed Access Model", "Multi-App Mode", "Microsoft Account Manager" }, 0,
                "MAM manages and protects company data inside apps (e.g., app protection policies) — even on unenrolled devices."),
            new SeedQuestion("Windows Autopilot is used to…",
                new[] { "Drive cars", "Provision new Windows devices with zero-touch setup", "Auto-reply to email", "Overclock GPUs" }, 1,
                "Autopilot lets a new device set itself up out-of-the-box, enrolled and configured, with minimal IT/user effort."),
            new SeedQuestion("An Intune \"configuration profile\" is used to…",
                new[] { "Bill customers", "Push settings like Wi-Fi, VPN and restrictions to devices", "Write source code", "Schedule meetings" }, 1,
                "Configuration profiles deliver standardized device settings centrally instead of configuring each device by hand."),
            new SeedQuestion("To deploy an app to people in Intune, you assign it to…",
                new[] { "A printer", "A Microsoft Entra (Azure AD) group", "A registry key", "A USB stick" }, 1,
                "Apps, policies and profiles are targeted at Entra ID user or device groups."),
            new SeedQuestion("In Intune, \"Retire\" differs from \"Wipe\" because Retire…",
                new[] { "Resets the whole device", "Removes only company data, leaving personal data", "Deletes the user account", "Reinstalls Windows" }, 1,
                "Retire removes corporate data/apps and leaves personal content; Wipe factory-resets the device."),
            new SeedQuestion("Which portal is the primary place to administer Intune?",
                new[] { "Microsoft Intune admin center", "Control Panel", "Local Group Policy Editor", "Task Scheduler" }, 0,
                "Intune is managed from the cloud-based Microsoft Intune admin center (intune.microsoft.com)."),
            new SeedQuestion("Device \"enrollment\" in Intune means…",
                new[] { "Buying a licence", "Registering a device so Intune can manage it", "Encrypting one file", "Renaming the PC" }, 1,
                "Enrollment establishes the management relationship so policies, apps and compliance can be applied."),
            new SeedQuestion("Which Intune capability helps enforce disk encryption such as BitLocker?",
                new[] { "A device configuration/compliance policy", "A meeting invite", "A mailbox rule", "A screen saver" }, 0,
                "Encryption is enforced and monitored through configuration and compliance policies."),
        }),

        new(QuizTopic.Security, "security", "Security Best Practices", new[]
        {
            new SeedQuestion("What does \"MFA\" stand for?",
                new[] { "Multi-Factor Authentication", "Managed File Access", "Microsoft Firewall Agent", "Main Frame Application" }, 0,
                "MFA requires more than a password (e.g., an app prompt or key), blocking the vast majority of account-takeover attacks."),
            new SeedQuestion("A \"phishing\" attack typically tries to…",
                new[] { "Cool the server room", "Trick you into revealing credentials or clicking malicious links", "Back up your data", "Update your OS" }, 1,
                "Phishing uses fake but convincing messages to steal credentials or deliver malware — verify before you click."),
            new SeedQuestion("The core idea of \"Zero Trust\" is…",
                new[] { "Trust the internal network fully", "Never trust, always verify — every request", "Disable all firewalls", "Share one admin password" }, 1,
                "Zero Trust verifies identity, device and context for every access request, regardless of network location."),
            new SeedQuestion("Which makes the STRONGEST password?",
                new[] { "Your pet's name", "A long, unique passphrase", "12345678", "password1" }, 1,
                "Length and uniqueness beat complexity tricks; a long passphrase stored in a password manager is ideal."),
            new SeedQuestion("You get an unexpected, urgent email to reset your password via a link. You should…",
                new[] { "Click immediately", "Go to the official site/app yourself and verify; don't click the link", "Reply with your password", "Forward it to everyone" }, 1,
                "Urgency + a link is a classic phishing pattern — navigate to the service directly instead of trusting the email."),
            new SeedQuestion("\"Least privilege\" means…",
                new[] { "Everyone is an admin", "Give users only the access they actually need", "Disable logging", "Use shared accounts" }, 1,
                "Limiting rights to what's needed shrinks the damage an attacker (or mistake) can do."),
            new SeedQuestion("What does a VPN primarily provide?",
                new[] { "Faster typing", "An encrypted tunnel for network traffic", "More disk space", "A louder fan" }, 1,
                "A VPN encrypts traffic between your device and the network, protecting it on untrusted connections."),
            new SeedQuestion("Ransomware is malware that…",
                new[] { "Cleans your registry", "Encrypts your data and demands payment", "Speeds up boot", "Defragments disks" }, 1,
                "Ransomware locks/encrypts files and extorts payment — good backups and patching are key defenses."),
            new SeedQuestion("Best protection if a laptop is lost or stolen?",
                new[] { "A strong screensaver", "Full-disk encryption plus the ability to remotely wipe it", "A sticker with your name", "Nothing — it's fine" }, 1,
                "Encryption keeps data unreadable without the key; remote wipe removes company data from the device."),
            new SeedQuestion("Why does promptly installing software updates/patches matter?",
                new[] { "It changes the wallpaper", "Patches fix known security vulnerabilities attackers exploit", "It frees disk space", "It is purely cosmetic" }, 1,
                "Most breaches exploit known, already-patched flaws — timely updates close those doors."),
            new SeedQuestion("\"Social engineering\" attacks work by…",
                new[] { "Breaking encryption math", "Manipulating people into giving up info or access", "Overheating the CPU", "Flooding the printer" }, 1,
                "They target humans (trust, urgency, authority) rather than technology — awareness is the main defense."),
            new SeedQuestion("A password manager helps security by…",
                new[] { "Reusing one password everywhere", "Generating and storing a unique strong password per site", "Emailing passwords to you", "Posting them publicly" }, 1,
                "It lets every account have a long, unique password without you memorizing them — defeating credential reuse attacks."),
        }),
    };
}
