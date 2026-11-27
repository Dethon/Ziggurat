---
# A built app's own instructions, verbatim, for a turn the app asked: the words are the app's, a file
# of its repository, so the document is the hole they go in. They stand where the agent's persona
# would, read in the custom instructions' band, closest to the ask.
name: app_instructions
purpose: A built app's instructions, answering its ask in place of the agent's persona.
priority: CustomInstructions
# The apps server bounds an app's instructions file (./publish refuses one over it), and that bound is
# kept within this budget, so an app inside it is never reported over.
budget: 4000
placeholders: instructions
---

{{instructions}}
