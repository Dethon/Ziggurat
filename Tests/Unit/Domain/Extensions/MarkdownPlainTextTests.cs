using Domain.Extensions;
using Shouldly;

namespace Tests.Unit.Domain.Extensions;

// A conversation's preview in the list is one line of what was last said, read as text: cut from
// the reply as the model wrote it, it showed "1. **Receta** (`/vault/notas/receta.md`)" and
// "``` # Compra leche ```".
public sealed class MarkdownPlainTextTests
{
    [Theory]
    [InlineData("1. **Receta** (`/vault/notas/receta.md`): el ingrediente", "1. Receta (/vault/notas/receta.md): el ingrediente")]
    [InlineData("El ingrediente secreto es __pimentón__ de *la Vera*.", "El ingrediente secreto es pimentón de la Vera.")]
    [InlineData("Contenido:\n```\n# Compra\n\nleche, huevos\n```", "Contenido: # Compra leche, huevos")]
    [InlineData("## Hecho\n\n- una\n- dos", "Hecho una dos")]
    [InlineData("Mira [la Wikipedia](https://es.wikipedia.org/wiki/Azafrán) y ~~no~~ esto", "Mira la Wikipedia y no esto")]
    [InlineData("> citado\nsin cita", "citado sin cita")]
    public void Markdown_ReadsAsTheTextItSays(string markdown, string text) =>
        markdown.MarkdownToPlainText().ShouldBe(text);

    [Theory]
    [InlineData("2 * 3 * 4 = 24")]
    [InlineData("snake_case_name and file_name.md")]
    [InlineData("precio: 5 € - 10 €")]
    public void TextThatOnlyLooksLikeMarkdown_IsLeftAsItIs(string text) =>
        text.MarkdownToPlainText().ShouldBe(text);
}