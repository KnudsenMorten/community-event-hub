# Default content — replace with your own

Everything in this `config/` folder, and the four survey definitions in
`src/CommunityHub/App_Data/Surveys/`, is the **neutral default set** that ships with the public
Community Event Hub template. It is derived from a real conference's content with every event, venue,
sponsor, person, address, price and date taken out, so a fresh install has working task instructions,
welcome steps, information pages and surveys from the first start.

Edit these files for your event — see [`README.md`](README.md) for what each one does.

## Keep this file

It tells the test suite that `config/` is **not** the upstream conference's private content. While it
exists, the few tests that pin that conference's own wording and values (for example its intro page
coverage and its internal documents) are **skipped**; every other test — including the structural
checks on task bodies, welcome copy, surveys and config shape — runs against whatever is in `config/`,
so edit freely and let the suite tell you if a file no longer parses.
