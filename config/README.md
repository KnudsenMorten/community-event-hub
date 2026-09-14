# Per-edition configuration and content

This folder holds everything about **your** event that is not code: edition settings, sponsor rules,
speaker deadlines, the text of every built-in task, the welcome step of each role's Get-Started wizard,
the information pages, and the integration field maps. The public template ships it filled with a
**neutral default set** (see [`PUBLIC-TEMPLATE.md`](PUBLIC-TEMPLATE.md)), so a fresh install works
from the first start — edit the files for your event.

Both the web app and the Functions app copy `config/*.json` and the `tasks`, `welcome`, `content`,
`integrations` and `email-templates` sub-folders into their build and publish output, so what is here
is deployed with the code.

> **Once this holds your real event, keep your fork private** — it will contain your organizers'
> addresses, coupon codes and supplier details.

## Settings files

| File | Read by the app? | Point the app at another file with the app setting |
|---|---|---|
| [`event.eldk27.json`](event.eldk27.json) | **Yes** — edition identity, dates, venue, rooms, levels, the Resources page, and the placeholders used in task text and e-mails | `EventConfig__EventConfigPath` |
| [`sponsor.eldk27.json`](sponsor.eldk27.json) | **Yes** — sponsor deadline rules, webshop product classification, contact roles, swag catalogue, event-platform sponsor categories, booth specs | `SponsorConfig__SponsorConfigPath` |
| [`speaker-deadlines.eldk27.json`](speaker-deadlines.eldk27.json) | **Yes** — the due date of every speaker task, and the Get-Started deadline | `SpeakerDeadlines__ConfigPath` |
| [`signal-groups.eldk27.json`](signal-groups.eldk27.json) | **Yes** — Signal group invite links. **Delete it** if you do not use Signal; that hides the step | `SignalGroups__ConfigPath` |
| [`integrations/*.fieldmap.json`](integrations/) | **Yes** for the Sessionize and Zoho Backstage maps (loaded fail-soft over built-in defaults); the webshop and ERP maps document their mapping | — |
| [`sessionize.eldk27.custom.sample.json`](sessionize.eldk27.custom.sample.json) | No — a sample of the `Sessionize` **app settings** (`Sessionize__EndpointId`, …) | — |
| [`volunteer-guidance.sample.json`](volunteer-guidance.sample.json) | No — a sample of the `VolunteerGuidance` app settings; leave `ApiKey` empty for the built-in heuristic | — |

The file names above are the paths the code reads by default. Keep them, or set the app settings to your
own names — on **both** the web app and the Functions app, because scheduled jobs read the same files.

Integration credentials and connection settings (e-mail relay, WooCommerce, Zoho, SharePoint, LinkedIn,
ERP, …) are **app settings**, not files, each bound from its configuration section; every integration
is optional and stays off until configured. Secrets never go in a file — store them in the Key Vault
that `infra/main.bicep` creates. The secret inventory is in
[`docs/DESIGN.md` §17](../docs/DESIGN.md#17-configuration--key-vault-reference).

## Content you edit

| Folder | What it holds | If a file is missing |
|---|---|---|
| `tasks/eldk27/{participant,speaker,sponsor}/*.md` | The body text of every built-in task (hotel, dinner, swag, presentations, booth layout, …). `{{placeholders}}` resolve from `event.eldk27.json` | The task still exists, but its page shows no instructions |
| `welcome/eldk27/<role>.md` | The welcome step of each role's Get-Started wizard | That role has no welcome step |
| `content/eldk27/<slug>.md` | Information pages under `/Info/<slug>` (introduction, key dates, addresses, good to know, session guidelines, wayfinding, …) | The page is not available |
| `email-templates/<key>.html` *(optional)* | Your own version of a shipped e-mail template from `templates/emails/` | The shipped template is used |

The survey definitions (call-for-speakers topics and the three post-event surveys) live next to the
code in `src/CommunityHub/App_Data/Surveys/`.

⚠️ The edition folder name `eldk27` is currently fixed in code for the three content folders, so keep
it as the folder name. Each task definition names its body file; see
`src/CommunityHub.Core/Tasks/Definitions/`.
