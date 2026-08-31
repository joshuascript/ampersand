using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Ampersand;

/// <summary>
/// A yes/no prompt, and the one-button notice built from the same parts. Hand-built
/// in the same style as the rest of the window - the app has no XAML and no dialog
/// framework, and these are the only things it needs to say.
/// </summary>
internal static class ConfirmDialog
{
	/// <summary>
	/// False when the user declines OR closes the window, because Close() with no
	/// argument yields default(bool) - so dismissing the prompt is a decline
	/// rather than a silent accept.
	/// </summary>
	public static Task<bool> Show( Window owner, string title, string message, string confirm, string cancel )
	{
		return Build( owner, title, message, confirm, cancel );
	}

	/// <summary>
	/// A statement rather than a question: one button, and nothing to decide. Used
	/// where ampersand cannot do anything about the condition itself - Steam not
	/// running is the user's to fix, since starting the client for them was tried
	/// and abandoned.
	/// </summary>
	public static Task Notify( Window owner, string title, string message, string dismiss = "OK" )
	{
		return Build( owner, title, message, dismiss, null );
	}

	/// <summary>
	/// The shared body. A null <paramref name="cancel"/> drops the second button,
	/// which is the only difference between the two shapes.
	/// </summary>
	private static Task<bool> Build( Window owner, string title, string message, string confirm, string? cancel )
	{
		var dialog = new Window
		{
			Title = title,
			Width = 460,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			RequestedThemeVariant = ThemeVariant.Dark,
			Background = TerminalTheme.ToolbarPanel
		};

		var text = new TextBlock
		{
			Text = message,
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.Normal,
			FontSize = 12
		};

		var confirmButton = new Button { Content = confirm, IsDefault = true };
		confirmButton.Click += ( _, _ ) => dialog.Close( true );

		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness( 0, 16, 0, 0 )
		};

		if ( cancel is not null )
		{
			// IsCancel only when there is something to cancel; on a notice Escape
			// closes the window anyway, which yields the same default(bool).
			var cancelButton = new Button { Content = cancel, IsCancel = true };
			cancelButton.Click += ( _, _ ) => dialog.Close( false );

			buttons.Children.Add( cancelButton );
		}

		buttons.Children.Add( confirmButton );

		var body = new StackPanel { Margin = new Thickness( 16 ) };
		body.Children.Add( text );
		body.Children.Add( buttons );

		dialog.Content = body;

		return dialog.ShowDialog<bool>( owner );
	}
}
