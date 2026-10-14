# The Sugary benchmark meals

The cases the Sugary eval tier scores the `sugary` agent on. `cases.json` is the standalone Sugary
app's `eval/external/cases.json`, copied unchanged, so a score here and a score there are measured
against the same truth.

`carbs` in every case is what an estimate is scored against: available carbohydrate, excluding
fibre, the number a European label shows.

## The sets

64 photographed meals from three public datasets. Each case name starts with the source and the
dish or file id it came from.

| Set | Meals | What it is | How the truth was obtained | Licence |
|---|---|---|---|---|
| `n5k` | 14 | [Nutrition5k](https://github.com/google-research-datasets/Nutrition5k): cafeteria plates from straight overhead, nothing in frame for scale | every ingredient weighed | CC BY 4.0 |
| `nvr` | 18 | [NutritionVerse-Real](https://www.kaggle.com/datasets/nutritionverse/nutritionverse-real): phone photos of food on dinner plates, four angles each | every item weighed | CC BY-NC-SA 4.0 |
| `snap` | 32 | [SNAPMe](https://agdatacommons.nal.usda.gov/articles/dataset/24856449): phone photos of real home meals beside a 3.8 cm checkerboard card | the eater's own food record, not weighed | CC BY-SA 4.0 |

What to keep in mind when reading a score:

- **Fibre.** `n5k` and `nvr` publish total carbohydrate. Fibre was subtracted using standard table
  values per ingredient, so their `carbs` is approximate by a few grams; `totalCarbs` keeps the
  published figure. `snap` records fibre itself.
- **`snap` is agreement with a person, not accuracy.** Its portions were entered by the eater from
  memory aids, and several records plainly disagree with the photo. Use `n5k` and `nvr` to judge
  accuracy.
- **Dropped rows.** NutritionVerse-Real labels a roast chicken as 64% carbohydrate and several of its
  dish records do not match their photos; those dishes were left out. SNAPMe meals with food left on
  the plate afterwards were left out too.
- **Weight.** `grams` is the edible weight on the plate. The scorecard sets it beside the sum of the
  weights the estimate gave, because portion weight, not carb density, was the standalone
  benchmark's largest source of error.

Each case also carries a `hint` (the few words a person might type, names only) and `truth` (every
food with its weight). The tier sends neither: a case is a photo and nothing else, as the standalone
figures below were measured.

## The photos are not in git

`photos/` is ignored: the photos are large (about 18 MB) and two of the licences restrict
redistribution. A case names its photos relative to this directory (`photos/n5k-1558375886.jpg`),
and the tier skips with that reason when the first one is missing.

They are the standalone app's `eval/external/photos/` (118 files: one per `n5k` and `snap` meal,
four per `nvr` meal), which is also ignored there. On a machine with that checkout, link them:

```bash
ln -s ~/repos/sugary/eval/external/photos Tests/Eval/Sugary/photos
```

Without it, fetch each dataset from the link above and save the photo each case names under that
file name: Nutrition5k's overhead RGB image of the dish id, NutritionVerse-Real's four images of
the dish id as `a` to `d`, SNAPMe's image whose file name starts with the id.

## Running

The tier is never part of a bare run, the full tier, the smoke tier or the changed tier, and it
needs a model named. It spends money:

```bash
ZIGGURAT_EVAL=1 ZIGGURAT_EVAL_MODEL=google/gemini-3.8-flash \
  dotnet test --filter "Category=Eval&Tier=Sugary"
```

One pass is the 64 meals, one estimate each, plus the sugary agent's four scenarios
(`Tests/Eval/Scenarios/SugaryScenarios.cs`) on the same model. Each meal is sent as the Sugary
channel sends an estimate (the standalone app's words for one photo, the `meal_estimate` format, the
photo as an attachment) and its answer is read by the server's own parse and arithmetic. The
scorecard is `.eval-output/scorecard-sugary-<model>.json`: its `benchmark` section has the mean
absolute error, bias, how often the truth is inside the range, estimated against true weight, median
seconds and cost per estimate, overall, per set, per input (photo or video) and per set and input,
then every meal with its items.

| Variable | Effect |
|---|---|
| `ZIGGURAT_EVAL_MODEL` | The model to score (required) |
| `ZIGGURAT_EVAL_SUGARY_PHOTOS` | How many of a meal's photos to send (default 1; the `nvr` meals have four) |
| `ZIGGURAT_EVAL_PROVIDER` | Pin the model to one provider |
| `ZIGGURAT_EVAL_PARALLELISM` | How many meals are in flight at once |

## What the standalone app scored

The comparison a first pass is written against: the standalone app's own benchmark, 2026-10-12,
`google/gemini-3.8-flash` at medium effort, one photo per meal, no hints, $0.42 for the 64 meals.

| Set | Meals | Mean abs. error | Bias | In range | Median time |
|---|---|---|---|---|---|
| n5k | 14 | 12.2 g | -8.3 g | 8 of 14 | 13 s |
| nvr | 18 | 8.1 g | -3.3 g | 8 of 18 | 27 s |
| snap | 32 | 14.8 g | -11.1 g | 16 of 32 | 23 s |
| all | 64 | 12.3 g | -8.3 g | 32 of 64 | 23 s |

Every model tried there scored about 13–14 g from a photo. The Gemini row above was written about
half an hour before the standalone's estimate gained its range floor (0.6× to 1.6× of the best
weight on every item not marked high), which the server applies and this tier scores; the
standalone's last Haiku 5.5 pass, with the floor, held the truth in 39 of 64 at 14.2 g. So read the
mean error against the table and expect the in-range count to be higher here.
