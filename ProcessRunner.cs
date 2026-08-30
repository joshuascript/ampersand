using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Porta.Pty;

namespace Ampersand;

/// <summary>
/// Runs a launch script, either on a pty feeding the attached pane, or detached
/// into the user's own terminal emulator.
///
/// The pty matters for more than fidelity: the engine checks whether its output
/// is redirected and only emits ANSI colour when it is talking to a tty. Through
/// a pipe we measured zero escape sequences in 2,462 lines; through a pty, 1,172
/// in the same startup. Colour is the engine's own classification, so we no
/// longer guess severity from text.
///
/// forkpty() makes the child a session leader, so its pid IS its process group.
/// That is what Stop signals - reaching sbox-launcher after sbox-dev hands off,
/// and the engine behind bwrap under the Steam runtime.
/// </summary>
internal sealed class ProcessRunner
{
	private IPtyConnection? connection;
	private Process? detachedProcess;
	private CancellationTokenSource? reader;

	public string Name { get; }
	public bool IsRunning { get; private set; }
	public int? ExitCode { get; private set; }

	/// <summary>Raw bytes from the pty, already on the UI thread.</summary>
	public event Action<byte[], int>? Output;

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
		bool detached,
		int columns,
		int rows )
	{
		if ( IsRunning )
			return;

		ExitCode = null;

		if ( detached )
		{
			StartDetached( command, workingDirectory, extraEnv );
			return;
		}

		StartOnPty( command, workingDirectory, extraEnv, columns, rows );
	}

	private void StartOnPty(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv,
		int columns,
		int rows )
	{
		var environment = new Dictionary<string, string>( extraEnv )
		{
			// Without a sane TERM the engine cannot decide what it may emit.
			["TERM"] = "xterm-256color"
		};

		var options = new PtyOptions
		{
			Name = "xterm-256color",
			Cols = Math.Max( columns, 20 ),
			Rows = Math.Max( rows, 5 ),
			Cwd = workingDirectory,
			App = command[0],
			CommandLine = new List<string>( command ).ToArray(),
			Environment = environment
		};

		IsRunning = true;
		StateChanged?.Invoke( this );

		_ = Task.Run( async () =>
		{
			try
			{
				connection = await PtyProvider.SpawnAsync( options, CancellationToken.None );
			}
			catch ( Exception e )
			{
				Post( () =>
				{
					Notice?.Invoke( "ampersand: could not start - " + e.Message );
					IsRunning = false;
					StateChanged?.Invoke( this );
				} );
				return;
			}

			reader = new CancellationTokenSource();
			var buffer = new byte[16384];

			try
			{
				while ( true )
				{
					var read = await connection.ReaderStream.ReadAsync( buffer, 0, buffer.Length, reader.Token );

					// A pty is a single stream, so EOF is simply a zero read -
					// no two-pipe bookkeeping, and it still outlives the direct
					// child, which is what makes the sbox-dev handoff work.
					if ( read == 0 )
						break;

					var chunk = new byte[read];
					Buffer.BlockCopy( buffer, 0, chunk, 0, read );
					Post( () => Output?.Invoke( chunk, chunk.Length ) );
				}
			}
			catch ( OperationCanceledException )
			{
				// Stop() closed it.
			}
			catch ( IOException )
			{
				// Reading a pty master after the child exits raises EIO on
				// Linux. That IS the normal end of a session, not a fault, so
				// it must not surface as an error line on every clean exit.
			}
			catch ( Exception e )
			{
				Post( () => Notice?.Invoke( "ampersand: output stream ended - " + e.Message ) );
			}

			int? code = null;

			try
			{
				connection.WaitForExit( 3000 );
				code = connection.ExitCode;
			}
			catch
			{
				// Exit status is not always retrievable; leave it unknown.
			}

			Post( () =>
			{
				ExitCode = code;
				IsRunning = false;
				StateChanged?.Invoke( this );
			} );
		} );
	}

	private void StartDetached(
		IReadOnlyList<string> command,
		string workingDirectory,
		IReadOnlyDictionary<string, string> extraEnv )
	{
		if ( !SystemTerminal.TryBuild( command, out var argv, out var emulator ) )
		{
			Notice?.Invoke( "ampersand: no terminal emulator found - install one, or untick Detach." );
			return;
		}

		var info = new ProcessStartInfo
		{
			FileName = argv[0],
			WorkingDirectory = workingDirectory,
			UseShellExecute = false
		};

		for ( var i = 1; i < argv.Count; i++ )
			info.ArgumentList.Add( argv[i] );

		foreach ( var pair in extraEnv )
			info.Environment[pair.Key] = pair.Value;

		try
		{
			detachedProcess = new Process { StartInfo = info, EnableRaisingEvents = true };
			detachedProcess.Exited += ( _, _ ) => Post( () =>
			{
				ExitCode = SafeExitCode( detachedProcess );
				IsRunning = false;
				StateChanged?.Invoke( this );
			} );

			detachedProcess.Start();
		}
		catch ( Exception e )
		{
			Notice?.Invoke( "ampersand: could not launch " + emulator + " - " + e.Message );
			return;
		}

		IsRunning = true;
		Notice?.Invoke( "ampersand: detached into " + emulator + " - output is in that window." );
		StateChanged?.Invoke( this );
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
		if ( !IsRunning )
			return;

		if ( detachedProcess is { HasExited: false } )
		{
			try
			{
				detachedProcess.Kill( true );
			}
			catch
			{
				// Already gone.
			}

			return;
		}

		if ( connection is null )
			return;

		// forkpty made the child a session leader, so pid == pgid and a negative
		// signal reaches the whole tree - including anything it handed off to.
		if ( Kill( -connection.Pid, SIGTERM ) != 0 )
		{
			try
			{
				connection.Kill();
			}
			catch
			{
				// Already gone.
			}
		}
	}

	/// <summary>
	/// Sends keystrokes to the child. This is what makes the pane a real
	/// terminal rather than a viewer - sbox-server's console becomes usable,
	/// and Ctrl+C reaches the process natively.
	/// </summary>
	public void Write( ReadOnlyMemory<byte> data )
	{
		var stream = connection?.WriterStream;

		if ( stream is null || data.IsEmpty )
			return;

		try
		{
			stream.Write( data.Span );
			stream.Flush();
		}
		catch
		{
			// The child has gone; nothing useful to do with the keystroke.
		}
	}

	/// <summary>Tells the child the window changed size, as a terminal would.</summary>
	public void Resize( int columns, int rows )
	{
		try
		{
			connection?.Resize( Math.Max( columns, 20 ), Math.Max( rows, 5 ) );
		}
		catch
		{
			// Not fatal; the child simply keeps its old idea of the size.
		}
	}

	private static void Post( Action action )
	{
		Avalonia.Threading.Dispatcher.UIThread.Post( action );
	}

	private const int SIGTERM = 15;

	[DllImport( "libc", EntryPoint = "kill", SetLastError = true )]
	private static extern int Kill( int pid, int signal );
}
