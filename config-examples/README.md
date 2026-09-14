# Configuration examples

Starter files for running your own edition. Everything here is a **placeholder** — nothing in this
folder is anybody's real data. Copy what you need into a `config/` folder at the repository root,
rename it for your edition, and fill in your own values.

`config/` is where the web app and the Functions app look for per-edition files at runtime. Both
projects copy `config/*.json` (and the `config/tasks`, `config/welcome`, `config/content`,
`config/integrations` and `config/email-templates` sub-folders) into their build and publish output,
so a file you add there is deployed with the code.

> **Keep your real `config/` out of any public fork.** It will hold your organizers' addresses,
> coupon codes and supplier details. The upstream project keeps its own `config/` in a private
> repository for exactly that reason.

## The files

| Example | Copy to | Read by the app? | Override the path with the app setting |
|---|---|---|---|
| [`event.example.json`](event.example.json) | `config/event.<edition>.json` | **Yes** — edition identity, dates, venue, rooms, levels, resources page, placeholders used in task text and e-mails | `EventConfig__EventConfigPath` |
| [`sponsor.example.json`](sponsor.example.json) | `config/sponsor.<edition>.json` | **Yes** — sponsor deadline rules, webshop product classification, contact roles, swag catalogue, booth specs. The sponsor order pull reports an error while this file is missing. | `SponsorConfig__SponsorConfigPath` |
| [`speaker-deadlines.example.json`](speaker-deadlines.example.json) | `config/speaker-deadlines.<edition>.json` | **Yes** — the due date of every speaker task, and the Get-Started deadline. Missing file = no speaker deadline tasks are seeded. | `SpeakerDeadlines__ConfigPath` |
| [`signal-groups.example.json`](signal-groups.example.json) | `config/signal-groups.<edition>.json` | **Yes** — optional Signal group invite links. Missing file = the "Join Signal groups" step is hidden. | `SignalGroups__ConfigPath` |
| [`sessionize.custom.sample.json`](sessionize.custom.sample.json) | *(app settings)* | Bound from the `Sessionize` configuration section — set `Sessionize__Enabled`, `Sessionize__View`, `Sessionize__EndpointId` on both apps, or paste the block into a git-ignored `appsettings.Development.json` locally. | — |
| [`volunteer-guidance.sample.json`](volunteer-guidance.sample.json) | *(app settings)* | Bound from the `VolunteerGuidance` section. Leave `ApiKey` empty to use the built-in heuristic (no AI call). | — |
| [`templates/emails/`](templates/emails/) | — | Historical copies for reference. The e-mail templates the app actually renders ship in the repository-root [`templates/emails/`](../templates/emails/); put per-edition overrides in `config/email-templates/`. | — |

The default paths in code still name the upstream edition (`config/event.eldk27.json` and so on). Either
name your files that way, or set the app settings above to your own file names — on **both** the web
app and the Functions app, because scheduled jobs read the same files.

All other integration settings (e-mail relay, WooCommerce, Zoho, SharePoint, LinkedIn, ERP, ...) are
**app settings**, not files: each is bound from its configuration section (`Email`, `WooCommerce`,
`Zoho`, `SharePoint`, `LinkedIn`, `EconomicErp`, ...). Every integration is optional and stays off until
configured. Secrets are never put in a file — store them in the Key Vault that `infra/main.bicep`
creates and reference them from app settings. The secret inventory is in
[`docs/DESIGN.md` §17](../docs/DESIGN.md#17-configuration--key-vault-reference).

## Content you write yourself

These are Markdown files your organizers own. They are not shipped with the public template, because
they are one event's wording, addresses and suppliers:

| Folder | What it holds | If it is missing |
|---|---|---|
| `config/tasks/eldk27/{participant,speaker,sponsor}/*.md` | The body text of every built-in task (hotel, dinner, swag, presentations, booth layout, ...) | The task still exists and is tracked, but its page shows no instructions |
| `config/welcome/eldk27/<role>.md` | The welcome step of each role's Get-Started wizard | That role simply has no welcome step |
| `config/content/eldk27/<slug>.md` | Information pages under `/Info/<slug>` (key dates, addresses, session guidelines, ...) | The page is not available |

⚠️ The edition folder name (`eldk27`) is currently fixed in code for these three folders, so use it as
the folder name for now. Each task definition names its body file; see
`src/CommunityHub.Core/Tasks/Definitions/` for the list.
