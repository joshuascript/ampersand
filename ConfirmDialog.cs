using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Ampersand;

/// <summary>
/// A yes/no prompt. Hand-built in the same style as the rest of the window - the
/// app has no XAML and no dialog framework, and this is the only question it
/// needs to ask.
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

		var cancelButton = new Button { Content = cancel, IsCancel = true };
		cancelButton.Click += ( _, _ ) => dialog.Close( false );

		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness( 0, 16, 0, 0 )
		};
		buttons.Children.Add( cancelButton );
		buttons.Children.Add( confirmButton );

		var body = new StackPanel { Margin = new Thickness( 16 ) };
		body.Children.Add( text );
		body.Children.Add( buttons );

		dialog.Content = body;

		return dialog.ShowDialog<bool>( owner );
	}
}
