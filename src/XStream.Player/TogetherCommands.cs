namespace XStream.Player;

public enum TogetherCommandKind { Share, Stop, Remove, Reply }

/// <summary>
/// Play together commands the launcher sends to the stream on stdin (one line each), so its Play together page can do
/// what the stream menu's panel does:
///   together share            start sharing (the invite follows in a status event)
///   together stop             stop sharing; everyone who joined is told
///   together remove &lt;slot&gt;    remove the guest in that controller slot (1 = Player 2)
///   together reply &lt;code&gt;     accept a guest's reply code (the rest of the line, as pasted)
/// </summary>
public sealed record TogetherCommand(TogetherCommandKind Kind, int Slot = -1, string Code = "")
{
    public const string Word = "together";

    public static TogetherCommand? TryParse(string? line)
    {
        if (line is null) return null;
        var t = line.Trim();
        if (!t.StartsWith(Word + " ", StringComparison.Ordinal)) return null;
        var rest = t[(Word.Length + 1)..].TrimStart();
        int sp = rest.IndexOf(' ');
        string verb = sp < 0 ? rest : rest[..sp];
        string arg = sp < 0 ? "" : rest[(sp + 1)..].Trim();
        return verb switch
        {
            "share" when arg.Length == 0 => new TogetherCommand(TogetherCommandKind.Share),
            "stop" when arg.Length == 0 => new TogetherCommand(TogetherCommandKind.Stop),
            // Slots 1-3: slot 0 is always the host's own controller.
            "remove" when int.TryParse(arg, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int slot)
                          && slot is >= 1 and <= 3 => new TogetherCommand(TogetherCommandKind.Remove, slot),
            "reply" when arg.Length > 0 => new TogetherCommand(TogetherCommandKind.Reply, Code: arg),
            _ => null,
        };
    }

    /// <summary>The stdin line. A pasted reply code may have been wrapped over lines: it goes on one (the code parser joins the words).</summary>
    public string ToLine() => Kind switch
    {
        TogetherCommandKind.Share => $"{Word} share",
        TogetherCommandKind.Stop => $"{Word} stop",
        TogetherCommandKind.Remove => $"{Word} remove {Slot.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        _ => $"{Word} reply " + string.Join(' ', Code.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)),
    };
}
