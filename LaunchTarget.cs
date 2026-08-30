using System.ComponentModel;
using AvaloniaTerminal;

namespace Ampersand;

/// <summary>
/// One row in the top panel: a script, the process running it, and its own
/// terminal. Each target owns a TerminalControl so the client and the server can
/// run at once and the pane simply shows whichever row is selected - the control
/// holds its own scrollback, so there is nothing to swap.
/// </summary>
internal sealed class LaunchTarget : INotifyPropertyChanged
{
	public string Name { get; }
	public string ScriptFile { get; }
	public ProcessRunner Runner { get; }
	public TerminalControl Terminal { get; }

	/// <summary>Header read from the script; drives the Steam runtime checkbox.</summary>
	public ScriptMetadata Metadata { get; set; } = new();

	/// <summary>Per-target checkbox state. Persisted in step 6.</summary>
	public bool UseSniper { get; set; }

	/// <summary>Per-target: run in the user's terminal instead of the pane.</summary>
	public bool Detach { get; set; }

	/// <summary>
	/// True from the double-click until the process is actually spawned. The
	/// containerised path can wait minutes on Steam starting, and Runner.IsRunning
	/// is still false throughout - so without this a second double-click would
	/// start a second launch of the same target.
	/// </summary>
	public bool Preparing { get; set; }

	/// <summary>
	/// The grid the pane is currently showing. The control measures its own
	/// cell and resizes on every layout pass, so this is the only honest answer
	/// to "how wide is this terminal" - and it is what the pty must be opened
	/// at, so the engine formats for the width that is actually on screen.
	/// </summary>
	public (int Cols, int Rows) Grid => (Terminal.Model.Terminal.Cols, Terminal.Model.Terminal.Rows);

	/// <summary>Last grid size handed to the pty. See the SizeChanged handler.</summary>
	private (int Cols, int Rows) lastResize;

	public event PropertyChangedEventHandler? PropertyChanged;

	public LaunchTarget( string name, string scriptFile, TerminalControl terminal )
	{
		Name = name;
		ScriptFile = scriptFile;
		Terminal = terminal;
		Runner = new ProcessRunner( name );

		Runner.Output += ( bytes, count ) => Terminal.Model.Feed( bytes, count );
		Runner.Notice += Append;

		// Keystrokes go back to the child, and the child is told when the grid
		// changes shape - the two halves of behaving like a terminal.
		Terminal.Model.UserInput += ( _, e ) => Runner.Write( e.Data );

		// SizeChanged fires on every pixel of a resize drag, including the ones
		// too small to change the grid, and the args already carry the values -
		// so forward only real changes rather than reaching back through the
		// model for them and sending a TIOCSWINSZ and a SIGWINCH per frame.
		Terminal.Model.SizeChanged += ( _, e ) =>
		{
			if ( (e.Cols, e.Rows) == lastResize )
				return;

			lastResize = (e.Cols, e.Rows);
			Runner.Resize( e.Cols, e.Rows );
		};
	}

	public string Display
	{
		get
		{
			if ( Runner.IsRunning )
				return Name + "   \u25cf running";

			if ( Runner.ExitCode is int code )
				return Name + ( code == 0 ? "   exited 0" : "   exited " + code );

			return Name;
		}
	}

	public void NotifyStatusChanged()
	{
		PropertyChanged?.Invoke( this, new PropertyChangedEventArgs( nameof( Display ) ) );
	}

	/// <summary>
	/// Writes one of the launcher's own lines into the terminal, in the accent
	/// colour. CR before LF because the terminal is in raw mode: a bare newline
	/// steps down a row without returning to column zero, which staircases text.
	/// </summary>
	public void Append( string line )
	{
		Terminal.Model.Feed( Ansi.Paint( Ansi.Cyan, line ) + "\r\n" );
	}

	/// <summary>
	/// ESC[2J clears the screen, ESC[3J the scrollback and ESC[H homes the
	/// cursor - what a terminal does for `clear`.
	/// </summary>
	public void Clear()
	{
		Terminal.Model.Feed( Ansi.ClearAll );
	}
}
