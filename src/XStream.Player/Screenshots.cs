using SkiaSharp;

namespace XStream.Player;

/// <summary>Saves stream screenshots as PNG in Pictures/XStream.</summary>
public static class Screenshots
{
    public static string Folder
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Directory.CreateDirectory(Path.Combine(pictures, "Vexel")).FullName;
        }
    }

    /// <summary>RGBA pixels (as read back from the window) to a PNG file in Pictures/XStream. Returns its path.</summary>
    public static string Save(byte[] rgba, int width, int height, string consoleName)
    {
        string safe = string.Concat(consoleName.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-')).Trim();
        return SaveTo(rgba, width, height, Path.Combine(Folder, $"Vexel {(safe.Length > 0 ? safe + " " : "")}{DateTime.Now:yyyy-MM-dd HH.mm.ss}.png"));
    }

    public static string SaveTo(byte[] rgba, int width, int height, string path)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), Math.Min(rgba.Length, info.BytesSize));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var f = File.Create(path);
        data.SaveTo(f);
        return path;
    }
}

/// <summary>Where instant-replay clips go: Videos/Vexel (Movies/Vexel on macOS).</summary>
public static class Clips
{
    public static string Folder
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videos))
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsMacOS() ? "Movies" : "Videos");
            return Directory.CreateDirectory(Path.Combine(videos, "Vexel")).FullName;
        }
    }

    public static string NewPath(string consoleName)
    {
        string safe = string.Concat(consoleName.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-')).Trim();
        return Path.Combine(Folder, $"Vexel {(safe.Length > 0 ? safe + " " : "")}{DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
    }
}
