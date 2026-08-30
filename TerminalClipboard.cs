using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input;
using AvaloniaTerminal;

namespace Ampersand;

/// <summary>
/// Copy and paste for the output pane.
///
/// AvaloniaTerminal ships no clipboard key bindings at all, and actively
/// swallows the gestures: OnKeyDown turns any Ctrl+letter into a control byte
/// and sets Handled, so Ctrl+C reaches the engine as 0x03 and Ctrl+V as 0x16.
/// Shift is never consulted on that path, so Ctrl+Shift+C is not a way out -
/// it sends 0x03 too.
///
/// Avalonia's KeyboardDevice runs KeyBindings across the whole visual ancestor
/// chain BEFORE raising the key event, so a binding here pre-empts that
/// swallow.
///
/// Ctrl+C keeps its terminal meaning by construction rather than by a branch:
/// its command reports CanExecute only while there is a selection, and
/// KeyBinding.TryHandle leaves the event unhandled when CanExecute is false. So
/// with nothing selected the key falls through to the control untouched and the
/// engine still gets its SIGINT.
/// </summary>
internal static class TerminalClipboard
{
	public static void Attach( TerminalControl terminal )
	{
		var copy = new TerminalCommand( () => terminal.HasSelection, async () =>
		{
			// CopySelection() is a pure getter - only the async form writes to
			// the clipboard. Clearing afterwards is what keeps the NEXT Ctrl+C
			// an interrupt rather than a second copy.
			await terminal.CopySelectionAsync();
			terminal.Model?.ClearSelection();
		} );

		// HasSelection is a DirectProperty, so it notifies. Without this the
		// binding would answer CanExecute from whatever the selection was when
		// the window opened.
		terminal.PropertyChanged += ( _, e ) =>
		{
			if ( e.Property == TerminalControl.HasSelectionProperty )
				copy.RaiseCanExecuteChanged();
		};

		Bind( terminal, Key.C, copy );

		Bind( terminal, Key.V, new TerminalCommand(
			() => true, () => terminal.PasteFromClipboardAsync() ) );

		Bind( terminal, Key.A, new TerminalCommand( () => true, () =>
		{
			terminal.SelectAll();
			return Task.CompletedTask;
		} ) );
	}

	private static void Bind( TerminalControl terminal, Key key, ICommand command )
	{
		terminal.KeyBindings.Add( new KeyBinding
		{
			Gesture = new KeyGesture( key, KeyModifiers.Control ),
			Command = command
		} );
	}

	/// <summary>
	/// The smallest ICommand that will do. CanExecute is a live predicate
	/// rather than a cached flag, because for Ctrl+C it decides whether the
	/// keystroke is a copy or an interrupt.
	/// </summary>
	private sealed class TerminalCommand : ICommand
	{
		private readonly Func<bool> canExecute;
		private readonly Func<Task> execute;

		public TerminalCommand( Func<bool> canExecute, Func<Task> execute )
		{
			this.canExecute = canExecute;
			this.execute = execute;
		}

		public event EventHandler? CanExecuteChanged;

		public bool CanExecute( object? parameter )
		{
			return canExecute();
		}

		/// <summary>
		/// async void is what ICommand.Execute gives us, so nothing is left to
		/// observe the task - a clipboard the compositor refuses must not take
		/// the launcher down with it.
		/// </summary>
		public async void Execute( object? parameter )
		{
			try
			{
				await execute();
			}
			catch
			{
				// A clipboard that will not answer is not worth a crash.
			}
		}

		public void RaiseCanExecuteChanged()
		{
			CanExecuteChanged?.Invoke( this, EventArgs.Empty );
		}
	}
}
