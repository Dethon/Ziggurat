import { describe, expect, it } from "vitest";
import { formatGrams, rationBounds, shownTotal } from "./carbs";
import { rationText } from "./format";
import { translator } from "./i18n";

// A safety property: the total she reads equals the sum of the rows she reads under it.
describe("shownTotal", () => {
  const rows = (...figures: [number, number, number][]) =>
    figures.map(([carbsLow, carbs, carbsHigh]) => ({ carbsLow, carbs, carbsHigh }));
  const shownSum = (items: ReturnType<typeof rows>, pick: (i: (typeof items)[number]) => number) =>
    items.reduce((sum, item) => sum + Number(formatGrams(pick(item))), 0);

  it("adds the rows as they are shown, not the unrounded figures behind them", () => {
    // 20.4 + 40.4 = 60.8 would show 61; the rows show 20 and 40.
    const items = rows([20.4, 33.6, 53.8], [40.4, 56, 89.6]);
    const total = shownTotal(items);
    expect(total.low).toBe(60);
    expect(total.low).toBe(shownSum(items, (i) => i.carbsLow));
    expect(total.best).toBe(shownSum(items, (i) => i.carbs));
    expect(total.high).toBe(shownSum(items, (i) => i.carbsHigh));
  });

  it("holds for any mix of rows", () => {
    let seed = 7;
    const random = () => ((seed = (seed * 16807) % 2147483647) / 2147483647) * 80;
    for (let run = 0; run < 200; run++) {
      const items = rows(...Array.from({ length: 1 + (run % 6) }, () => {
        const best = random();
        return [best * 0.6, best, best * 1.6] as [number, number, number];
      }));
      const total = shownTotal(items);
      expect(total.best).toBe(shownSum(items, (i) => i.carbs));
      expect(total.low).toBe(shownSum(items, (i) => i.carbsLow));
      expect(total.high).toBe(shownSum(items, (i) => i.carbsHigh));
    }
  });

  it("makes a day's total the sum of its meals as shown", () => {
    const meals = [rows([10.4, 10.4, 10.4]), rows([20.4, 20.4, 20.4])];
    const day = meals.reduce((sum, items) => sum + shownTotal(items).best, 0);
    expect(day).toBe(30);
  });
});

describe("rationBounds", () => {
  it("widens a range of rations outward to the half", () => {
    expect(rationBounds({ low: 21, high: 56 }, 10)).toEqual({ low: 2, high: 6 });
    expect(rationBounds({ low: 38, high: 100 }, 10)).toEqual({ low: 3.5, high: 10 });
    expect(rationBounds({ low: 20, high: 50 }, 10)).toEqual({ low: 2, high: 5 });
  });

  it("invents no range of rations for an exact amount", () => {
    const en = translator("en");
    expect(rationText({ low: 6, best: 6, high: 6 }, 10, "en", en)).toBe("0.5\u00a0rations of 10\u00a0g");
    expect(rationText({ low: 61, best: 61, high: 61 }, 10, "en", en)).toBe("6\u00a0rations of 10\u00a0g");
  });

  it("is what the rations line says", () => {
    expect(rationText({ low: 21, best: 35, high: 56 }, 10, "en", translator("en"))).toBe("3.5 rations of 10 g (2–6)");
  });
});
