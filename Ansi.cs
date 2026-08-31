namespace Ampersand;

/// <summary>
/// SGR sequences for text the launcher writes into the terminal itself.
///
/// The only thing ampersand still prints as text is the dependency report, which
/// runs in a real terminal (Program.DependencyCheckArgument), so these end up
/// interpreted by the emulator rather than by anything in this app. Codes are the
/// bright set, matching what the engine already uses, so the report sits in the
/// same palette rather than looking like a second scheme layered on top.
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
}
