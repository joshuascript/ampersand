using System;
using System.Collections.Generic;
using System.IO;

namespace Ampersand;

/// <summary>
/// Runs a launch script in the user's own terminal emulator instead of the
/// attached pane.
///
/// The awkward part is that some emulators fork and return immediately, which
/// would lose the child: gnome-terminal is a D-Bus client to
/// gnome-terminal-server and returns in ~110ms without --wait. Each entry below
/// therefore carries whatever flag makes the emulator stay attached; most do
/// not fork at all and need nothing.
/// </summary>
internal static class SystemTerminal
{
	private static readonly (string Exe, string[] Args)[] Candidates =
	{
		// Forks unless told otherwise.
		( "gnome-terminal", new[] { "--wait", "--" } ),
		( "konsole", new[] { "--nofork", "-e" } ),

		// These run the child directly.
		( "ptyxis", new[] { "--" } ),
		( "alacritty", new[] { "-e" } ),
		( "kitty", new[] { "--" } ),
		( "wezterm", new[] { "start", "--" } ),
		( "foot", new[] { "-e" } ),
		( "xfce4-terminal", new[] { "-x" } ),
		( "tilix", new[] { "-e" } ),
		( "terminator", new[] { "-x" } ),
		( "xterm", new[] { "-e" } ),

		// Debian/Ubuntu alternatives symlink; last because it could be any of
		// the above and we cannot know which flags it honours.
		( "x-terminal-emulator", new[] { "-e" } )
	};

	/// <summary>The emulator we would use, or null when none is installed.</summary>
	public static string? Detect()
	{
		foreach ( var (exe, _) in Candidates )
		{
			if ( Which( exe ) is not null )
				return exe;
		}

		return null;
	}

	/// <summary>
	/// Builds the argv that runs <paramref name="command"/> in a terminal
	/// window. Returns false when no emulator is installed.
	/// </summary>
	public static bool TryBuild( IReadOnlyList<string> command, out List<string> argv, out string? emulator )
	{
		argv = new List<string>();
		emulator = null;

		foreach ( var (exe, args) in Candidates )
		{
			var path = Which( exe );

			if ( path is null )
				continue;

			emulator = exe;
			argv.Add( path );
			argv.AddRange( args );
			argv.AddRange( command );
			return true;
		}

		return false;
	}

	private static string? Which( string exe )
	{
		var path = Environment.GetEnvironmentVariable( "PATH" );

		if ( string.IsNullOrEmpty( path ) )
			return null;

		foreach ( var directory in path.Split( ':', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var candidate = Path.Combine( directory, exe );

			if ( File.Exists( candidate ) )
				return candidate;
		}

		return null;
	}
}
