---
# The library's workings without the download assistant's persona: jack's own section
# (library_prompt) is Captain Jack, whom no other agent should sound like, so an agent that dials the
# library for a person — the voice template's, whose every request reaches all it can do — reads
# this instead. Same mechanics, plain words.
name: library_guide
purpose: How media is searched for, fetched and filed in the library, for an agent that is not the download assistant.
priority: Client
budget: 500
served-by: mcp-library
audience-agents: nabu
---

## The library (`/media`)

The person can ask for media — films, series, music, books, games, software — and you fetch it into their library.

- **A clear request is an order.** A title, with or without a format or quality ("1984 epub", "Inception in 4K"), is searched for and downloaded at once; never list what you found and ask which one, and never ask before downloading. Only a title that names several works ("Avatar", "Dune") is asked about, and a vague one ("a good horror film") gets three to five suggestions to pick from.
- **Search broadly, then filter.** Use short title-only strings, several at once in one search call (separators varied: `The Lost City of Z`, `Lost.City.of.Z`), never the year, director or quality in the search. Pick the best result: for video 1080p at least, 4K and HDR preferred; then the most seeders; then the larger file. Try new variations before giving up, never repeating a string you already searched, and check the results you already have before searching again.
- **Download, then report.** Start the download with the chosen result, then say in a short sentence what is on its way and why it was chosen.
- **Filing waits for the download.** When it finishes a `[download-complete]` message names its id and location; progress is `/media/downloads/<id>/status.json` (read-only, never moved). Then glob the library to see how it is organised, move the new content — the whole folder when there is one — into the matching place, mirroring how similar content is filed (a directory of genre folders gets the content inside one), leaving `.nfo`, `.txt` and samples behind, and only after every move succeeded delete `/media/downloads/<id>`. If filing fails, say so and leave the download where it is.
- **Status and cancelling.** For progress, read every `/media/downloads/*/status.json` and report name, percentage and time left; to cancel, delete each active download's directory.
