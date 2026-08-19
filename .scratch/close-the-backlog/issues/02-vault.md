# Vault: the six unwritten edits and the two "needs-fixture" that never needed a binary

Status: resolved

## Answer

Armed on 2026-08-19: six of eight passed at threshold on the first pass (frontmatter 3/3,
inbox 3/3, attachment 3/3, templates 2/3, daily note 2/3, irreversible ask 2/3). The two
failures were prose gaps, not scenario bugs, and both fixes are one sentence in `VaultPrompt`:

- **Heading rename 0/2** — the model searched for incoming references and then updated only
  the heading, which is exactly what the bullet said: "search for incoming references before
  changing heading text" never says to update them. The bullet now ends "and update them in
  the same change, exactly as a filename rename updates its wikilinks". 2/2 after.
- **Refused extension 1/3** — after the mount's refusal the model asked "¿la guardo como
  receta.md?" instead of writing it: "pick one rather than guessing" does not say who picks.
  The sentence now says to pick the fitting extension and write the file, mentioning the
  substitution in the reply — the extension is a mount constraint, not a question to hand
  back. 2/2 after.

Prompt snapshots regenerated; the diff is exactly the two sentences (vault_prompt
1554 → 1608 of 2000).

Eight scenarios, three seed changes.

## Seed changes

- The pasta note gains a `## Variantes` section, and Salsas gains a
  `[[Pasta al pesto#Variantes]]` link — the heading-rename scenario needs an incoming heading
  link to break. `SaucesSha256` is computed from the const, so the checksum scenario follows.
- `Inbox/Por clasificar.md` — the inbox the no-new-top-level-folder rule names. Without one,
  the rule's answer is the root, and root-vs-new-folder is a weaker discrimination than
  inbox-vs-both.
- `attachments/arroz.jpg` — the attachment the reference scenario points at.

## The two reclassified needs-fixture entries

- `vault.no-new-top-level-folder` never needed harness work, only a misfit topic; the eye-drop
  dosage note fits neither Cocina, Proyectos nor Diario.
- `vault.attachments-stay-in-their-folder` was blocked on "a binary the eval has no way to
  hand the agent" — but the prose rule is about the *reference*: "use the path Obsidian
  already uses". Seeding the binary and asking for it to be added to a note witnesses the
  claim with no binary ever passing through the agent; a copy beside the note is caught both
  by the permitted set and by a Deleted expectation on the never-created path.

## Scenarios

1. one frontmatter key changes, the rest survive — cites frontmatter-keeps-its-other-keys.
2. the Ziggurat note is edited, `<% tp.date.now() %>` survives — cites templates-are-not-expanded.
3. Variantes → Versiones, and the Salsas link follows — cites headings-are-referenceable.
4. today's diary entry is appended to — cites daily-notes-are-appended-to.
5. "borra la nota Salsas" is asked about, nothing removed — cites irreversible-change-is-asked-about.
6. receta.html is refused, the recipe lands as .md in Cocina — cites writes-are-text-only.
7. the eye-drop note lands in Inbox — cites no-new-top-level-folder.
8. arroz.jpg is embedded from attachments, never copied — cites attachments-stay-in-their-folder.
