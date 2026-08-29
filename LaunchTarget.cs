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
		Terminal.Model.SizeChanged += ( _, _ ) =>
			Runner.Resize( Terminal.Model.Terminal.Cols, Terminal.Model.Terminal.Rows );
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
