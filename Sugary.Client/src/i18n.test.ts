import { describe, expect, it } from "vitest";
import { dictionaries, type StringKey } from "./i18n";

const { en, es } = dictionaries;
const keys = Object.keys(en) as StringKey[];
const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort();

// Strings that read the same in both languages on purpose.
const SAME_IN_BOTH = new Set<StringKey>(["grams", "signInTitle", "dose_basal", "unitsShort", "unitsCount", "minutesShort", "hoursShort", "exerciseRange"]);

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
