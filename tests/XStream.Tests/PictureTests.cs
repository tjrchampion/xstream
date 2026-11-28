using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Video;

/// <summary>The colour-blind filter's maths, and the settings and keys for the colour filter and mini player.</summary>
static class PictureTests
{
    private static void True(bool c, string what) { if (!c) throw new Exception("expected true: " + what); }
    private static void Near(double expected, double actual, double tol, string what)
    {
        if (Math.Abs(expected - actual) > tol) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    private static readonly ColorFilter[] Filters = [ColorFilter.Deuteranopia, ColorFilter.Protanopia, ColorFilter.Tritanopia];

    private static double Distance(double[] a, double[] b) => Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum());
    private static double[] Clamp(double[] c) => c.Select(v => Math.Clamp(v, 0, 1)).ToArray();

    public static IEnumerable<(string, Action)> All =>
    [
        ("Colour filter: identity when off or at strength 0", () =>
        {
            foreach (var f in Filters.Append(ColorFilter.Off))
            {
                var m = ColorFilters.Correction(f, f == ColorFilter.Off ? 1 : 0);
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++) Near(r == c ? 1 : 0, m[r, c], 1e-12, $"{f} [{r},{c}]");
            }
            True(ColorFilters.ShaderRows(ColorFilter.Off, 1) is null, "off sends no matrix (the shader skips it)");
            True(ColorFilters.ShaderRows(ColorFilter.Deuteranopia, 0) is null, "strength 0 sends no matrix");
        }),
        ("Colour filter: rows sum to 1, so white and greys stay put", () =>
        {
            foreach (var f in Filters)
            {
                var s = ColorFilters.Simulation(f)!;
                for (int r = 0; r < 3; r++) Near(1, s[r, 0] + s[r, 1] + s[r, 2], 2e-6, $"{f} simulation row {r}");
                foreach (var k in new[] { 0.5, 1.0 })
                {
                    var m = ColorFilters.Correction(f, k);
                    for (int r = 0; r < 3; r++) Near(1, m[r, 0] + m[r, 1] + m[r, 2], 1e-5, $"{f} at {k} row {r}");
                    var grey = ColorFilters.Apply(m, [0.4, 0.4, 0.4]);
                    foreach (var v in grey) Near(0.4, v, 1e-5, $"{f} grey");
                }
            }
        }),
        ("Colour filter: strength blends linearly with the identity", () =>
        {
            foreach (var f in Filters)
            {
                var full = ColorFilters.Correction(f, 1);
                var half = ColorFilters.Correction(f, 0.5);
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++) Near(((r == c ? 1 : 0) + full[r, c]) / 2, half[r, c], 1e-12, $"{f} [{r},{c}]");
            }
        }),
        ("Colour filter: a confusable pair separates after correction", () =>
        {
            for (int i = 0; i < Filters.Length; i++)
            {
                var f = Filters[i];
                var s = ColorFilters.Simulation(f)!;
                var m = ColorFilters.Correction(f, 1);
                var (a, b) = TestPattern.ConfusablePairs[i];
                double before = Distance(ColorFilters.Apply(s, a), ColorFilters.Apply(s, b));
                // What the colour-blind viewer sees of the corrected colours (clamped to the screen, as the shader does).
                double after = Distance(ColorFilters.Apply(s, Clamp(ColorFilters.Apply(m, a))), ColorFilters.Apply(s, Clamp(ColorFilters.Apply(m, b))));
                True(Distance(a, b) > 0.3, $"{f}: the pair really differs ({Distance(a, b):F3})");
                True(before < (f == ColorFilter.Tritanopia ? 0.13 : 0.01), $"{f}: looks alike to the viewer before ({before:F4})");
                True(after > Math.Max(0.1, 2 * before), $"{f}: tells apart after ({before:F4} -> {after:F4})");
            }
        }),
        ("Colour filter: shader rows are three float4s", () =>
        {
            var rows = ColorFilters.ShaderRows(ColorFilter.Protanopia, 1)!;
            var m = ColorFilters.Correction(ColorFilter.Protanopia, 1);
            True(rows.Length == 12, "12 floats");
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) Near(m[r, c], rows[r * 4 + c], 1e-6, $"[{r},{c}]");
                Near(0, rows[r * 4 + 3], 0, $"row {r} padding");
            }
        }),
        ("Colour filter and mini player place are saved and loaded", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"xstream-picture-{Environment.ProcessId}.json");
            try
            {
                new AppSettings { ColorFilter = ColorFilter.Tritanopia, ColorFilterStrength = 0.6, MiniPlayerPlace = new ScreenSpot(0, 0.25) }.Save(path);
                var back = AppSettings.Load(path);
                True(back.ColorFilter == ColorFilter.Tritanopia, "filter");
                Near(0.6, back.ColorFilterStrength, 1e-9, "strength");
                True(back.MiniPlayerPlace == new ScreenSpot(0, 0.25), "mini player place");
                True(File.ReadAllText(path).Contains("\"Tritanopia\""), "stored by name");
                var fresh = new AppSettings();
                True(fresh.ColorFilter == ColorFilter.Off && fresh.ColorFilterStrength == 1 && fresh.MiniPlayerPlace is null, "defaults");
            }
            finally { File.Delete(path); }
        }),
        ("F8 (mini player) is Vexel's: not bindable, not sent to the console", () =>
        {
            True(KeyNames.TryParse("F8", out int f8), "F8 has a name");
            True(KeyNames.IsReserved(f8), "F8 is reserved");
            True(KeyboardMouseInput.Vk(65) == 0, "F8 isn't sent as a native key");
            True(KeyboardMouseInput.Vk(64) == 0x76, "F7 still is");
        }),
    ];
}
