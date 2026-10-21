# Recipes in the pantry

Status: ready-for-agent

## What

A recipe is something she cooks from foods she keeps: a set of pantry foods in given amounts, and
what the pot makes. It sits in the pantry beside her foods, and a helping of it goes into a meal as
one item, the way a food does.

## Decisions

1. **Yield, per recipe.** Each recipe is measured either by its cooked weight in grams (she weighs
   her helping; carbs per 100 g are the ingredients' carbs over the cooked weight) or by the number
   of servings it makes (she counts helpings; a serving carries the ingredients' carbs over the
   servings). The yield is entered, never derived: ml and by-portion ingredients make a sum of
   grams meaningless, and cooking changes the weight.
2. **Live ingredients.** An ingredient is a pantry food and an amount — in the food's unit for a
   food by weight, a count of its portions for a food by portion. A recipe always uses the
   pantry's current numbers, so changing a food changes every recipe that uses it. A meal item made
   from a recipe keeps its own copy of the numbers, as one made from a food does.
3. **A food in a recipe cannot be deleted.** The pantry says which recipes use it, and the server
   refuses the delete (409) as the backstop.
4. **No nesting.** Ingredients are foods only, not other recipes.
5. **A new ingredient joins the pantry when it is created.** The recipe editor opens the ordinary
   new-food sheet (by hand, barcode or label photo); the food is saved there and then, and becomes
   the ingredient. It stays in the pantry if the recipe is abandoned.
6. **One list.** Recipes are listed with her foods, most recently eaten first, marked with their own
   drawn icon; the picker opened from a meal offers them too.
7. **Use is tracked.** A meal item made from a recipe carries a `recipeId` beside `foodId`, so a
   recipe has `uses` and `lastUsedAt` like a food. Its source is `manual`: no new `ItemSource`.
8. **The math lives in the app.** The server stores and validates a recipe's shape; the app works
   out its carbs from the foods it already loads (`recipes.ts`), which is also what the editor shows
   live as she types.

## Out of scope

The agent does not see recipes yet: no `ISugaryLog`, VFS table or CSV export rows for them, and
`recipeId` is not in the Domain's `SugaryMealItem`.

## Slices

1. Server: `Recipe` with owned ingredients, migration, `/api/recipes` CRUD with validation.
2. Server: a food used by a recipe cannot be deleted.
3. Server: `recipeId` on a meal item; recipes listed with `uses`/`lastUsedAt`.
4. App: `recipes.ts` (carbs of a recipe, its helping as a meal item), types and api.
5. App: recipe editor, the pantry list and picker, adding a helping to a meal.
