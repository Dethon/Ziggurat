# Inter's ten digits, alone, renamed "Sugary Digits": a plain zero beside Atkinson Hyperlegible Next.
# Usage: python -I make.py <inter-latin-wght-normal.woff2> <out.woff2>
# Regenerate: npm pack @fontsource-variable/inter@5.3.0, then, with fonttools and brotli installed,
#   python -I scripts/make-digits.py <package>/files/inter-latin-wght-normal.woff2 src/fonts/sugary-digits.woff2
import sys
from fontTools import subset
from fontTools.ttLib import TTFont
src, out = sys.argv[1:]
options = subset.Options()
options.flavor = "woff2"
options.layout_features = ["tnum", "lnum"]
options.name_IDs = ["*"]
options.notdef_outline = True
font = TTFont(src)
sub = subset.Subsetter(options)
sub.populate(unicodes=range(0x30, 0x3A))
sub.subset(font)
name = font["name"]
for record in list(name.names):
    if record.nameID in (1, 4, 16, 21):
        record.string = "Sugary Digits"
    elif record.nameID in (6, 20):
        record.string = "SugaryDigits"
    elif record.nameID == 3:
        record.string = "Sugary Digits, subset of Inter 4"
    elif record.nameID == 5:
        record.string = record.toUnicode() + "; Sugary Digits subset"
font.flavor = "woff2"
font.save(out)
print("saved", out)
