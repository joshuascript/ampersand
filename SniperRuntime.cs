using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Ampersand;

internal sealed class SniperInstall
{
	public string Path { get; }
	public string Version { get; }

	public SniperInstall( string path, string version )
	{
		Path = path;
		Version = version;
	}

	public string RunScript => System.IO.Path.Combine( Path, "run-in-sniper" );

	public string RequirementsCheck =>
		System.IO.Path.Combine( Path, "pressure-vessel", "bin", "steam-runtime-check-requirements" );
}

/// <summary>
/// Finds Steam Linux Runtime 3.0 (sniper) and decides whether this host can
/// actually start a container in it.
///
/// Discovery has to cope with every Steam packaging - the Debian package, the
/// upstream tarball, Flatpak and Snap all spell the data root differently - and
/// with the runtime living in any registered library folder rather than the
/// default one.
/// </summary>
internal static class SniperRuntime
{
	public const int SteamAppId = 1628350;

	private static readonly Regex LibraryPath = new( "\"path\"\\s+\"([^\"]*)\"", RegexOptions.Compiled );

	public static SniperInstall? Find()
	{
		foreach ( var library in Libraries() )
		{
			var candidate = Path.Combine( library, "steamapps", "common", "SteamLinuxRuntime_sniper" );

			if ( File.Exists( Path.Combine( candidate, "run-in-sniper" ) ) )
				return new SniperInstall( candidate, ReadVersion( candidate ) );
		}

		return null;
	}

	private static IEnumerable<string> Libraries()
	{
		var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
		var xdgData = Environment.GetEnvironmentVariable( "XDG_DATA_HOME" );

		var roots = new List<string>
		{
			Path.Combine( home, ".steam", "steam" ),
			Path.Combine( home, ".steam", "root" ),
			Path.Combine( home, ".steam", "debian-installation" ),
			Path.Combine( home, ".local", "share", "Steam" ),
			Path.Combine( home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam" ),
			Path.Combine( home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam" ),
			Path.Combine( home, "snap", "steam", "common", ".local", "share", "Steam" ),
			"/usr/share/steam",
			"/usr/local/share/steam"
		};

		if ( !string.IsNullOrEmpty( xdgData ) )
			roots.Insert( 3, Path.Combine( xdgData, "Steam" ) );

		var seen = new HashSet<string>();

		foreach ( var root in roots )
		{
			if ( !Directory.Exists( Path.Combine( root, "steamapps" ) ) )
				continue;

			if ( seen.Add( Resolve( root ) ) )
				yield return root;

			// Every additional library folder the install registers.
			var vdf = Path.Combine( root, "steamapps", "libraryfolders.vdf" );
			if ( !File.Exists( vdf ) )
				continue;

			string text;
			try
			{
				text = File.ReadAllText( vdf );
			}
			catch
			{
				continue;
			}

			foreach ( Match match in LibraryPath.Matches( text ) )
			{
				var library = match.Groups[1].Value;

				if ( Directory.Exists( Path.Combine( library, "steamapps" ) ) && seen.Add( Resolve( library ) ) )
					yield return library;
			}
		}
	}

	private static string Resolve( string path )
	{
		try
		{
			return Directory.ResolveLinkTarget( path, true )?.FullName ?? Path.GetFullPath( path );
		}
		catch
		{
			return path;
		}
	}

	private static string ReadVersion( string installPath )
	{
		try
		{
			foreach ( var line in File.ReadAllLines( Path.Combine( installPath, "VERSIONS.txt" ) ) )
			{
				if ( !line.StartsWith( "depot", StringComparison.Ordinal ) )
					continue;

				var fields = line.Split( '\t', StringSplitOptions.RemoveEmptyEntries );
				if ( fields.Length >= 2 )
					return fields[1].Trim();
			}
		}
		catch
		{
			// fall through
		}

		return "unknown version";
	}

	/// <summary>
	/// pressure-vessel needs bubblewrap and unprivileged user namespaces.
	/// Distros disable those three different ways, so turn a failure into the
	/// command that fixes it rather than an opaque error.
	/// </summary>
	public static bool CheckRequirements( SniperInstall install, out List<string> problems )
	{
		problems = new List<string>();

		var checker = install.RequirementsCheck;
		if ( !File.Exists( checker ) )
		{
			problems.Add( "steam-runtime-check-requirements is missing from " + install.Path );
			return false;
		}

		var output = string.Empty;
		int exitCode;

		try
		{
			using var process = Process.Start( new ProcessStartInfo
			{
				FileName = checker,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			} );

			if ( process is null )
			{
				problems.Add( "could not run " + checker );
				return false;
			}

			output = ( process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd() ).Trim();
			process.WaitForExit( 30000 );
			exitCode = process.HasExited ? process.ExitCode : -1;
		}
		catch ( Exception e )
		{
			problems.Add( "could not run " + checker + " - " + e.Message );
			return false;
		}

		if ( exitCode == 0 )
			return true;

		if ( output.Length > 0 )
			problems.Add( output );

		if ( ReadFlag( "/proc/sys/kernel/apparmor_restrict_unprivileged_userns" ) == 1
			&& !File.Exists( "/etc/apparmor.d/bwrap-userns-restrict" ) )
		{
			problems.Add( "AppArmor is blocking unprivileged user namespaces and there is no bwrap profile. Install one, or:" );
			problems.Add( "    sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0" );
		}

		if ( ReadFlag( "/proc/sys/kernel/unprivileged_userns_clone" ) == 0 )
		{
			problems.Add( "Debian-style userns switch is off:" );
			problems.Add( "    sudo sysctl -w kernel.unprivileged_userns_clone=1" );
		}

		if ( ReadFlag( "/proc/sys/user/max_user_namespaces" ) == 0 )
		{
			problems.Add( "RHEL-style userns limit is zero:" );
			problems.Add( "    sudo sysctl -w user.max_user_namespaces=15000" );
		}

		if ( problems.Count == 0 )
			problems.Add( "steam-runtime-check-requirements failed with exit code " + exitCode );

		return false;
	}

	private static int ReadFlag( string path )
	{
		try
		{
			return int.TryParse( File.ReadAllText( path ).Trim(), out var value ) ? value : -1;
		}
		catch
		{
			return -1;
		}
	}
}
