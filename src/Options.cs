namespace FsCopilot.Exerciser;

/// <summary>
/// What the window would otherwise be clicked into place for. A scripted run wants the
/// session already up: --join takes the code the app shows, --relay the relay both
/// sides use, --panel a key or identifier to select, --window a simulator window title.
///
/// --press x,y sends one press at those rect fractions once the session is live, which is
/// how a scripted run checks the wire without a hand on the mouse. --popout starts waiting
/// for the pop-out keypress straight away, as pressing the button does.
///
///   FsCopilot.Exerciser.exe --relay localhost --join BNCHA001 --panel DisplayUnits --press 0.25,0.5
/// </summary>
public sealed record Options(string? Relay, string? Join, string? Panel, string? Window, string? Press, bool PopOut)
{
    public static Options Parse(string[] args)
    {
        string? Value(string flag)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new Options(Value("--relay"), Value("--join"), Value("--panel"), Value("--window"), Value("--press"),
            args.Any(a => string.Equals(a, "--popout", StringComparison.OrdinalIgnoreCase)));
    }

    public static Options Current { get; set; } = new(null, null, null, null, null, false);
}
