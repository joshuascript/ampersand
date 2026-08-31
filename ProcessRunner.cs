using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ampersand;

/// <summary>
/// Runs a launch script, either in the user's own terminal emulator or in the
/// background with its output discarded.
///
/// There used to be a third mode - a pty feeding an emulated terminal pane inside
/// the window - and it was the largest thing in the app. It is gone. Rebuilding a
/// terminal emulator inside a launcher solved a problem nobody has: every desktop
/// this runs on already has a real one, which does scrollback, selection, reflow
/// and resize better than the reimplementation ever did.
///
/// The finding that justified the pty still stands, and a real emulator now
/// satisfies it for free: the engine only emits ANSI colour when its output is a
/// tty - measured at 0 escape sequences in 2,462 lines through a pipe against
/// 1,172 in the same startup through a pty. An emulator gives the child a real
/// tty, so the colour is the engine's own and there is still no colorizer here.
/// Background mode has no tty and therefore no colour; nothing reads it anyway.
///
/// Stop is honest only in background mode, where the child is ours and Kill( true )
/// reaches its whole tree. An emulator that forks - gnome-terminal is a D-Bus
/// client to gnome-terminal-server - parents the engine somewhere outside our
/// tree, so Stop kills the window we started and not necessarily what is in it.
/// That is why SystemTerminal carries a don't-fork flag per emulator: with
/// --wait the client stays, and killing it takes the session with it.
/// </summary>
internal sealed class ProcessRunner
{
	private Process? process;

	/// <summary>
	/// What the child is told it is talking to. Shared, because the containerised
	/// path has to hand this over a second time as a --env argument: env does not
	/// cross the launcher service by inheritance, and an engine with no TERM emits
	/// no colour.
	/// </summary>
	public const string Term = "xterm-256color";

	public string Name { get; }
	public bool IsRunning { get; private set; }
	public int? ExitCode { get; private set; }

	/// <summary>Launcher-generated status lines, already on the UI thread.</summary>
	public event Action<string>? Notice;

	public event Action<ProcessRunner>? StateChanged;

	public ProcessRunner( string name )
	{
		Name = name;
	}

	public void Start(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv,
		bool useSystemTerminal )
	{
		if ( IsRunning )
			return;

		ExitCode = null;

		if ( useSystemTerminal )
			StartInTerminal( command, workingDirectory, extraEnv );
		else
			StartInBackground( command, workingDirectory, extraEnv );
	}

	private void StartInTerminal(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv )
	{
		if ( !SystemTerminal.TryBuild( command, out var argv, out var emulator ) )
		{
			Notice?.Invoke( "no terminal emulator found - install one, or untick Launch with system terminal." );
			return;
		}

		if ( !TryStart( argv, workingDirectory, extraEnv, redirect: false, out var problem ) )
		{
			Notice?.Invoke( "could not launch " + emulator + " - " + problem );
			return;
		}

		Notice?.Invoke( "running in " + emulator + " - output is in that window." );
		StateChanged?.Invoke( this );
	}

	/// <summary>
	/// No window and no output. The streams are still redirected and drained:
	/// left inherited they would land on whatever stdout ampersand itself was
	/// given, which from a desktop launcher is nothing and from a terminal is
	/// that terminal - neither of them a decision this mode gets to make. An
	/// undrained pipe would also stall the child once its 64K buffer filled,
	/// which is a hang rather than a discard.
	/// </summary>
	private void StartInBackground(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv )
	{
		if ( !TryStart( command, workingDirectory, extraEnv, redirect: true, out var problem ) )
		{
			Notice?.Invoke( "could not start - " + problem );
			return;
		}

		Notice?.Invoke( "running in the background - output is discarded." );
		StateChanged?.Invoke( this );
	}

	private bool TryStart(
		IReadOnlyList<string> argv,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv,
		bool redirect,
		out string problem )
	{
		problem = string.Empty;

		var info = new ProcessStartInfo
		{
			FileName = argv[0],
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = redirect,
			RedirectStandardError = redirect
		};

		for ( var i = 1; i < argv.Count; i++ )
			info.ArgumentList.Add( argv[i] );

		foreach ( var pair in extraEnv )
			info.Environment[pair.Key] = pair.Value;

		try
		{
			process = new Process { StartInfo = info, EnableRaisingEvents = true };

			process.Exited += ( _, _ ) => Post( () =>
			{
				ExitCode = SafeExitCode( process );
				IsRunning = false;
				StateChanged?.Invoke( this );
			} );

			if ( redirect )
			{
				process.OutputDataReceived += ( _, _ ) => { };
				process.ErrorDataReceived += ( _, _ ) => { };
			}

			process.Start();

			if ( redirect )
			{
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
			}
		}
		catch ( Exception e )
		{
			problem = e.Message;
			return false;
		}

		IsRunning = true;
		return true;
	}

	private static int? SafeExitCode( Process? process )
	{
		try
		{
			return process is { HasExited: true } ? process.ExitCode : null;
		}
		catch
		{
			return null;
		}
	}

	public void Stop()
	{
		if ( !IsRunning || process is not { HasExited: false } )
			return;

		try
		{
			process.Kill( true );
		}
		catch
		{
			// Already gone.
		}
	}

	private static void Post( Action action )
	{
		Avalonia.Threading.Dispatcher.UIThread.Post( action );
	}
}
