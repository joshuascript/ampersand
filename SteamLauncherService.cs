using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// Runs a command under the Steam client's own AppArmor profile, by handing it to
/// the launcher service pressure-vessel keeps running alongside Steam.
///
/// This is the ONLY way ampersand enters the container. Spawning run-in-steamrt4
/// straight off the filesystem does not work on Ubuntu 24.04+, and fails in a way
/// that misreports its own cause: with
/// kernel.apparmor_restrict_unprivileged_userns=1 the kernel exempts exactly two
/// things, /usr/bin/bwrap and Steam's own tree. pressure-vessel prefers the
/// runtime's srt-bwrap, which is neither, so it runs unconfined, cannot write
/// uid_map, and falls back to /usr/bin/bwrap - whose profile stacks every child
/// into unpriv_bwrap with no CAP_SYS_ADMIN. The nested srt-bwrap then dies with
/// "No permissions to create a new namespace", blaming kernel switches that are
/// fine. See docs/steamrt4-userns-apparmor.md.
///
/// Sent through this service the command runs as a child of Steam, whose profile
/// is flags=(unconfined) and grants userns to its whole tree, so srt-bwrap
/// succeeds on the first attempt and the fallback is never taken. Verified:
/// `--bus-name=<resolved> -- cat /proc/self/attr/current` reports the Steam
/// profile, not the caller's.
/// </summary>
internal static class SteamLauncherService
{
	/// <summary>
	/// The name older Steam clients register, and the prefix of the per-instance
	/// name newer ones register instead. Never connect to it on the assumption it
	/// is there - see <see cref="TryResolveBusName"/>.
	/// </summary>
	public const string BusName = "com.steampowered.PressureVessel.LaunchAlongsideSteam";

	/// <summary>
	/// The variable Steam puts in the environment of every process it starts,
	/// holding the exact name of the launcher service belonging to the running
	/// client. It is the first thing steam-runtime-launch-client's
	/// --alongside-steam reads; a plain desktop session has no such variable.
	/// </summary>
	public const string AlongsideSteamVariable = "SRT_LAUNCHER_SERVICE_ALONGSIDE_STEAM";

	private const string BusNameArgument = "--bus-name=";
	private const string InstancePrefix = BusName + ".Instance";

	/// <summary>
	/// Whether the service is up. This is the authoritative test - `pgrep steam`
	/// is not, because the client can be running while the service is not (an old
	/// Steam build, or one still starting).
	/// </summary>
	public static bool IsAvailable( SteamRt4Install install ) =>
		TryResolveBusName( install, out _ );

	/// <summary>
	/// Resolves the name the launcher service is actually reachable at. False
	/// when no service belonging to a running Steam client is on the session bus.
	///
	/// The bare name is not enough any more. The current steamrt64 client starts
	/// its service as
	///   x86_64-linux-gnu-srt-launcher-service --alongside-steam
	///       --bus-name=com.steampowered.PressureVessel.LaunchAlongsideSteam.Instance884065
	/// and owns only that name - the bare one is not on the bus at all. A client
	/// that launched us also exports the exact name as
	/// SRT_LAUNCHER_SERVICE_ALONGSIDE_STEAM, which is what --alongside-steam
	/// reads; without it (a plain desktop session) the flag falls back to the
	/// bare name, finds nothing, and reports the service as down.
	/// </summary>
	public static bool TryResolveBusName( SteamRt4Install install, out string busName )
	{
		busName = BusName;

		if ( !File.Exists( install.LaunchClient ) )
			return false;

		var inherited = Environment.GetEnvironmentVariable( AlongsideSteamVariable );

		// Ask which names are on the bus instead of connecting: --alongside-steam
		// would try the inherited name (absent here) and then the bare one (not
		// registered), and its "unable to connect" is indistinguishable from
		// Steam being closed.
		if ( !Run( new[] { install.LaunchClient, "--list" }, 5000, out var output ) )
		{
			// A client that cannot list still hands the name down to its children.
			if ( string.IsNullOrEmpty( inherited ) )
				return false;

			busName = inherited;
			return true;
		}

		var names = new List<string>();

		foreach ( var line in output.Split( '\n' ) )
		{
			var trimmed = line.Trim();

			if ( !trimmed.StartsWith( BusNameArgument, StringComparison.Ordinal ) )
				continue;

			var name = trimmed.Substring( BusNameArgument.Length );

			// Only exact launcher names. --list also reports
			// org.freedesktop.portal.Flatpak, and a prefix match would take
			// ...LaunchAlongsideSteam.Anything for the service itself.
			if ( name == BusName || name.StartsWith( InstancePrefix, StringComparison.Ordinal ) )
				names.Add( name );
		}

		if ( names.Count == 0 )
			return false;

		// The service of the client that started this process, if there is one.
		// Verified against the bus because a client restart leaves the inherited
		// name pointing at a service that has gone away.
		if ( !string.IsNullOrEmpty( inherited ) && names.Contains( inherited ) )
		{
			busName = inherited;
			return true;
		}

		// A client that also registers the bare name means that one by
		// "alongside Steam", so prefer it.
		if ( names.Contains( BusName ) )
			return true;

		// Otherwise each client instance registered its own name. Usually one;
		// after a client restart the old service can linger, so take the highest
		// PID - the client the user just started.
		busName = names[0];
		var newest = -1;

		foreach ( var name in names )
		{
			var number = int.TryParse( name.Substring( InstancePrefix.Length ), out var pid ) ? pid : -1;
			if ( number > newest )
			{
				newest = number;
				busName = name;
			}
		}

		return true;
	}

	/// <summary>
	/// Prefixes a command so the service runs it.
	///
	/// Connects by resolved name rather than --alongside-steam: see
	/// <see cref="TryResolveBusName"/>. Resolution is repeated here rather than
	/// handed in because every caller would otherwise have to pair the two calls
	/// correctly, and a probe is one short exec. Falls back to the bare name when
	/// nothing resolves, so the failure is the client's own "unable to connect".
	///
	/// The environment MUST be passed explicitly. The child inherits the SERVICE's
	/// environment - Steam's - and not the caller's; that is what --pass-env exists
	/// for. Verified: SBOX_TEST=x set on the client is unset in the child, and
	/// present only when handed over as --env. Anything relying on inheritance
	/// would fail silently, which is why this takes the dictionary rather than
	/// reading Environment itself.
	///
	/// Inheriting Steam's environment is correct, not a compromise: run-in-steamrt4
	/// goes through pressure-vessel-unruntime, which strips Steam's LD_LIBRARY_PATH,
	/// LD_PRELOAD and PATH back off before pressure-vessel-wrap. That is the same
	/// path Steam itself takes to launch a game.
	///
	/// Never pass --terminate: it terminates the launcher SERVICE - Steam's - not
	/// the child.
	/// </summary>
	public static List<string> Wrap(
		SteamRt4Install install,
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> environment )
	{
		var busName = TryResolveBusName( install, out var resolved ) ? resolved : BusName;

		var wrapped = new List<string> { install.LaunchClient, BusNameArgument + busName };

		// The service's own cwd otherwise, which is wherever Steam was started.
		if ( !string.IsNullOrEmpty( workingDirectory ) )
			wrapped.Add( "--directory=" + workingDirectory );

		foreach ( var pair in environment )
			wrapped.Add( "--env=" + pair.Key + "=" + pair.Value );

		wrapped.Add( "--" );
		wrapped.AddRange( command );

		return wrapped;
	}

	/// <summary>
	/// Runs a command to completion, merging both streams. False when it could not
	/// be started, timed out, or exited non-zero.
	/// </summary>
	private static bool Run( IReadOnlyList<string> command, int timeoutMs, out string output )
	{
		output = string.Empty;

		var info = new ProcessStartInfo
		{
			FileName = command[0],
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};

		for ( var i = 1; i < command.Count; i++ )
			info.ArgumentList.Add( command[i] );

		try
		{
			using var process = Process.Start( info );

			if ( process is null )
				return false;

			output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
			process.WaitForExit( timeoutMs );

			return process.HasExited && process.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}
}
