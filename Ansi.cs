namespace Ampersand;

/// <summary>
/// SGR sequences for text the launcher writes into the terminal itself.
///
/// The engine colours its own output, so these are only for our lines - the
/// dependency report and status notices. Codes are the bright set, matching what
/// the engine already uses, so our text sits in the same palette rather than
/// looking like a second scheme layered on top.
/// </summary>
internal static class Ansi
{
	public const string Esc = "\u001b";

	public const string Reset = Esc + "[39;49m";
	public const string Bold = Esc + "[1m";
	public const string NoBold = Esc + "[22m";

	public const string Red = Esc + "[91m";
	public const string Green = Esc + "[92m";
	public const string Yellow = Esc + "[93m";
	public const string Cyan = Esc + "[96m";
	public const string White = Esc + "[97m";

	/// <summary>Bright black, i.e. the dim grey terminals use for de-emphasis.</summary>
	public const string Dim = Esc + "[90m";

	/// <summary>Clears screen and scrollback, then homes the cursor.</summary>
	public const string ClearAll = Esc + "[2J" + Esc + "[3J" + Esc + "[H";

	public static string Paint( string colour, string text )
	{
		return colour + text + Reset;
	}
}
