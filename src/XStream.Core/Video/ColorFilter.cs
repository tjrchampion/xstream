namespace XStream.Core.Video;

/// <summary>Colour-blind correction for the picture (daltonisation: makes colours that look alike tell apart, it doesn't simulate).</summary>
public enum ColorFilter
{
    Off,
    /// <summary>Red-green, missing or weak green (M) cones. The most common kind.</summary>
    Deuteranopia,
    /// <summary>Red-green, missing or weak red (L) cones.</summary>
    Protanopia,
    /// <summary>Blue-yellow, missing or weak blue (S) cones.</summary>
    Tritanopia,
}

/// <summary>
/// The daltonisation maths. Correction is linear, so it collapses to one 3x3 matrix on linear RGB:
///   out = (I + k * E * (I - S)) * c
/// S simulates what the viewer sees, so (I - S) * c is the part of the colour they lose; E moves that lost part into
/// channels they can see (Fidaner, Lin and Ozguven's error redistribution, as used by daltonize.org and most
/// game accessibility filters); k is the strength (0 = identity, 1 = full). The GPU renderers apply it per pixel.
/// </summary>
public static class ColorFilters
{
    // Simulation matrices: Machado, Oliveira and Fernandes 2009, "A Physiologically-based Model for Simulation of
    // Color Vision Deficiency" (IEEE TVCG 15(6)), severity 1.0, for linear RGB. Rows sum to 1, so greys stay grey.
    public static readonly double[,] SimProtanopia =
    {
        { 0.152286, 1.052583, -0.204868 },
        { 0.114503, 0.786281, 0.099216 },
        { -0.003882, -0.048116, 1.051998 },
    };
    public static readonly double[,] SimDeuteranopia =
    {
        { 0.367322, 0.860646, -0.227968 },
        { 0.280085, 0.672501, 0.047413 },
        { -0.011820, 0.042940, 0.968881 },
    };
    public static readonly double[,] SimTritanopia =
    {
        { 1.255528, -0.076749, -0.178779 },
        { -0.078411, 0.930809, 0.147602 },
        { 0.004733, 0.691367, 0.303900 },
    };

    // Error redistribution. Red-green: the lost red/green difference is added to green and blue (both still seen).
    // Blue-yellow: the lost blue is added to red and green.
    private static readonly double[,] ShiftRedGreen = { { 0, 0, 0 }, { 0.7, 1, 0 }, { 0.7, 0, 1 } };
    private static readonly double[,] ShiftBlueYellow = { { 1, 0, 0.7 }, { 0, 1, 0.7 }, { 0, 0, 0 } };

    public static double[,]? Simulation(ColorFilter f) => f switch
    {
        ColorFilter.Protanopia => SimProtanopia,
        ColorFilter.Deuteranopia => SimDeuteranopia,
        ColorFilter.Tritanopia => SimTritanopia,
        _ => null,
    };

    /// <summary>The correction matrix (row-major, linear RGB). Identity for Off or strength 0.</summary>
    public static double[,] Correction(ColorFilter f, double strength)
    {
        var m = Identity();
        if (Simulation(f) is not { } s || strength <= 0) return m;
        var e = f == ColorFilter.Tritanopia ? ShiftBlueYellow : ShiftRedGreen;
        double k = Math.Min(1, strength);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int i = 0; i < 3; i++) sum += e[r, i] * ((i == c ? 1 : 0) - s[i, c]);
                m[r, c] += k * sum;
            }
        return m;
    }

    /// <summary>
    /// For the shaders' constant buffer: three float4 rows (the fourth of each unused), or null when the filter is off,
    /// so the shader skips it and Off costs nothing.
    /// </summary>
    public static float[]? ShaderRows(ColorFilter f, double strength)
    {
        if (f == ColorFilter.Off || strength <= 0) return null;
        var m = Correction(f, strength);
        var rows = new float[12];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++) rows[r * 4 + c] = (float)m[r, c];
        return rows;
    }

    public static double[] Apply(double[,] m, double[] c) =>
    [
        m[0, 0] * c[0] + m[0, 1] * c[1] + m[0, 2] * c[2],
        m[1, 0] * c[0] + m[1, 1] * c[1] + m[1, 2] * c[2],
        m[2, 0] * c[0] + m[2, 1] * c[1] + m[2, 2] * c[2],
    ];

    private static double[,] Identity() => new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
}
