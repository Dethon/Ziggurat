namespace Domain.DTOs.WebChat;

// The home screen icon of a space, and of a built app, filled with its accent colour (or a built app's
// own drawing, Badged): one frame, served by the chat client's host (/icon.svg) and the apps server, so
// the two never drift. Anything
// but a hex colour draws the default accent, so a colour in a query string can never become markup.
public static class SpaceIcon
{
    public static string Svg(string? color)
    {
        var fill = SpaceConfig.IsValidHexColor(color) ? color! : SpaceConfig.DefaultAccentColor;
        // The cat is drawn, not typed: as text it was whatever font the device had for Canadian syllabics,
        // and empty boxes where it had none.
        return Framed($"""<path fill="{fill}" transform="translate(11.85 311.68) scale(0.17305)" d="M326.3 0Q255.5 0 204.2 -19.7Q152.9 -39.3 119 -74.2Q85.1 -109 69 -155.4Q52.9 -201.8 52.9 -256.3Q52.9 -308 69.4 -354Q85.9 -400 119.2 -435.2Q152.5 -470.5 202 -490.4Q251.6 -510.4 317.7 -510.4H439.9V-387.9H313.1Q273.7 -387.9 244 -372.2Q214.4 -356.6 198 -326.7Q181.5 -296.9 181.5 -254.9Q181.5 -208.7 198.8 -179.6Q216.2 -150.5 249.3 -136.7Q282.5 -122.8 329.4 -122.8H865.3V0ZM1359 -726Q1556.2 -726 1650.1 -632.1Q1743.9 -538.1 1743.9 -341.8V0H1597V-337.2Q1597 -474.5 1540.2 -536.7Q1483.4 -598.9 1359.7 -598.9Q1235.4 -598.9 1174.7 -534.4Q1113.9 -469.9 1113.9 -336.9Q1113.9 -108.4 1269.2 -108.4Q1327.8 -108.4 1359.7 -139.1Q1391.7 -169.7 1391.7 -226.2Q1391.7 -281 1367.4 -308.9Q1343 -336.9 1295.7 -336.9Q1276.5 -336.9 1254.8 -330.4Q1233.1 -324 1220.6 -313.2L1193.8 -399.5Q1212 -419 1249.4 -431.6Q1286.8 -444.2 1326 -444.2Q1417.3 -444.2 1467.9 -387.7Q1518.5 -331.1 1518.5 -229.5Q1518.9 -116.9 1449.5 -52.5Q1380.1 12 1257.4 12Q1114.2 12 1042.4 -75.9Q970.5 -163.7 970.5 -336.9Q970.5 -526.7 1070.3 -626.4Q1170 -726 1359 -726ZM2203.2 13.2Q2114.5 13.2 2051.2 -12.4Q1987.9 -38 1947.7 -90Q1907.5 -142 1888.5 -221.1Q1869.5 -300.1 1869.5 -407.1V-714H2003.4L2203.8 -591.3L2403.8 -714H2537.7V-420.5Q2537.7 -318.4 2522.1 -237.8Q2506.4 -157.2 2469.2 -101.3Q2432 -45.3 2367.1 -16.1Q2302.2 13.2 2203.2 13.2ZM2390.8 -572.4 2215.7 -465.1H2192.5L2016.4 -573.4V-372Q2016.4 -285.3 2034.9 -228.1Q2053.4 -170.9 2094.5 -142.6Q2135.6 -114.3 2203.8 -114.3Q2272.6 -114.3 2313.2 -142.6Q2353.8 -170.9 2372.3 -228.1Q2390.8 -285.3 2390.8 -372Z"/>""");
    }

    // An app's own drawing (AppIcon), framed as every app's icon is: put in as a picture, never as markup,
    // so nothing of it runs or loads and none of its ids meets the frame's.
    public static string Badged(byte[] drawing) =>
        Framed($"""<image href="data:image/svg+xml;base64,{Convert.ToBase64String(drawing)}" x="0" y="0" width="512" height="512"/>""");

    // The square, the cut the corner badge sits in, and the badge: the Ziggurat mark every app's icon carries.
    private static string Framed(string drawing) =>
        $$"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
              <defs>
                <clipPath id="cut"><path clip-rule="evenodd" d="M0 0H512V512H0Z M318 418a100 100 0 1 0 200 0a100 100 0 1 0 -200 0Z"/></clipPath>
              </defs>
              <g clip-path="url(#cut)">
                {{drawing}}
              </g>
              <g>
                <circle cx="418" cy="418" r="82" fill="#16192a"/>
                <g fill="#fbbf24" transform="translate(350.8 353.6) scale(5.6)"><path d="M9 3h6v4H9z"/><path d="M6 9h12v4H6z"/><path d="M3 15h18v5H3z"/></g>
              </g>
            </svg>
            """;
}