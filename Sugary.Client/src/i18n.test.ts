import { describe, expect, it } from "vitest";
import { dictionaries, type StringKey } from "./i18n";

const { en, es } = dictionaries;
const keys = Object.keys(en) as StringKey[];
const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

// Strings that read the same in both languages on purpose.
const SAME_IN_BOTH = new Set<StringKey>(["grams", "dose_basal", "unitsShort", "unitsCount", "minutesShort", "hoursShort", "exerciseRange"]);

// The type system already makes both dictionaries have the same keys (`const es: Strings`); these
// catch what it cannot.
describe("the Spanish strings", () => {
  it("has exactly the English keys", () => {
    expect(Object.keys(es).sort()).toEqual([...keys].sort());
  });

  it.each(keys)("%s uses the same placeholders as the English", (key) => {
    expect(placeholders(es[key])).toEqual(placeholders(en[key]));
  });

  it.each(keys)("%s is written", (key) => {
    expect(es[key].trim()).not.toBe("");
    expect(en[key].trim()).not.toBe("");
  });

  it.each(keys.filter((key) => !SAME_IN_BOTH.has(key)))("%s is not the English left untranslated", (key) => {
    expect(es[key]).not.toBe(en[key]);
  });
});

// The pantry is the household's: every person in it sees and uses the same foods and recipes, so a
// person must not read it as theirs alone, nor delete a food thinking it goes only from their own.
describe("the pantry's words", () => {
  it.each([
    ["en", en, /household/],
    ["es", es, /casa|hogar/],
  ] as const)("say in %s that it is shared", (_, strings, shared) => {
    expect(strings.foodsTitle).toMatch(shared);
    expect(strings.foodDeleteBody).toMatch(shared);
  });
});
