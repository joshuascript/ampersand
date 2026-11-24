using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Ampersand;

/// <summary>
/// What a <see cref="ConfirmDialog.Choose"/> prompt resolved to. Dismissed is
/// first on purpose: it is the value a dialog closed without a choice returns.
/// </summary>
internal enum ConfirmChoice
{
	Dismissed,
	Confirm,
	Alternate
}

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
	/// Non-zero exit tail dialog: Avalonia dialog showing last ~80 lines
	/// of the log, with actions to open the log file/folder.
	/// Background runs capture everything, so the tail is the failure itself.
	/// Terminal runs are direct (engine-style) so the log holds only the command
	/// header - the output lived in the terminal window, which closes with the
	/// child; the dialog then shows what was run and where.
	/// </summary>
	public static Task<bool> NotifyTail( Window owner, string title, string logPath, int exitCode )
	{
		var tail = RunLog.Tail( logPath, 80, 6000 );

		var dialog = new Window
		{
			Title = title,
			Icon = AppIcon.Icon,
			Width = 760,
			Height = 520,
			CanResize = true,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			RequestedThemeVariant = ThemeVariant.Dark,
			Background = TerminalTheme.ToolbarPanel
		};

		var logView = new SelectableTextBlock
		{
			Text = tail,
			TextWrapping = TextWrapping.Wrap,
			FontFamily = new FontFamily( "DejaVu Sans Mono" ),
			FontSize = 11,
			Foreground = TerminalTheme.Normal
		};

		var scroll = new ScrollViewer
		{
			Content = logView,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			MaxHeight = 340,
			Background = TerminalTheme.Background
		};
		var scrollBorder = new Border
		{
			BorderBrush = TerminalTheme.PanelBorder,
			BorderThickness = new Thickness( 1 ),
			Padding = new Thickness( 8 ),
			Child = scroll
		};

		var pathText = new SelectableTextBlock
		{
			Text = logPath,
			TextWrapping = TextWrapping.Wrap,
			FontSize = 11,
			Foreground = TerminalTheme.FooterText
		};

		var openFolderBtn = new Button { Content = "Open log folder" };
		openFolderBtn.Click += ( _, _ ) =>
		{
			RunLog.TryOpenFolder( logPath );
		};

		var openFileBtn = new Button { Content = "Open log file" };
		openFileBtn.Click += ( _, _ ) =>
		{
			if ( !RunLog.TryOpenFile( logPath ) )
				RunLog.TryOpenFolder( logPath );
		};

		var dismissBtn = new Button { Content = "OK", IsDefault = true };
		dismissBtn.Click += ( _, _ ) => dialog.Close( true );

		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness( 0, 12, 0, 0 )
		};
		buttons.Children.Add( openFolderBtn );
		buttons.Children.Add( openFileBtn );
		buttons.Children.Add( dismissBtn );

		var body = new StackPanel { Margin = new Thickness( 16 ) };
		body.Children.Add( new TextBlock
		{
			Text = $"{title} (exit {exitCode})",
			FontWeight = FontWeight.SemiBold,
			Foreground = TerminalTheme.HeaderText,
			Margin = new Thickness( 0, 0, 0, 6 )
		} );
		body.Children.Add( pathText );
		body.Children.Add( new TextBlock { Height = 8 } );
		body.Children.Add( scrollBorder );
		body.Children.Add( buttons );

		dialog.Content = body;
		return dialog.ShowDialog<bool>( owner );
	}

	/// <summary>
	/// Two ways out plus "I changed my mind": the same prompt as <see cref="Show"/>,
	/// but dismissing the window (Escape, the title bar) is reported separately
	/// from clicking the second button. <see cref="Show"/> cannot tell them
	/// apart, and a dialog whose second button does something irreversible must
	/// not be reachable by reflex-dismissing it.
	/// </summary>
	public static Task<ConfirmChoice> Choose( Window owner, string title, string message, string confirm, string cancel )
	{
		var dialog = new Window
		{
			Title = title,
			Icon = AppIcon.Icon,
			Width = 460,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			RequestedThemeVariant = ThemeVariant.Dark,
			Background = TerminalTheme.ToolbarPanel
		};

		var confirmButton = new Button { Content = confirm, IsDefault = true };
		confirmButton.Click += ( _, _ ) => dialog.Close( ConfirmChoice.Confirm );

		// Deliberately not IsCancel: Escape must not be the same keystroke as
		// choosing the second option. Escape closes the window instead (handled
		// below), and a window closed without a choice returns the default,
		// which is Dismissed.
		var secondButton = new Button { Content = cancel };
		secondButton.Click += ( _, _ ) => dialog.Close( ConfirmChoice.Alternate );

		// Avalonia only routes Escape to a button with IsCancel, and neither
		// button here is one, so the window would otherwise ignore it.
		dialog.KeyDown += ( _, e ) =>
		{
			if ( e.Key == Key.Escape ) dialog.Close();
		};

		var buttons = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness( 0, 16, 0, 0 )
		};
		buttons.Children.Add( secondButton );
		buttons.Children.Add( confirmButton );

		var body = new StackPanel { Margin = new Thickness( 16 ) };
		body.Children.Add( new TextBlock
		{
			Text = message,
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.Normal,
			FontSize = 12
		} );
		body.Children.Add( buttons );

		dialog.Content = body;

		// Dismissed is the default value, which is exactly what a window closed
		// without pressing a button returns.
		return dialog.ShowDialog<ConfirmChoice>( owner );
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
			Icon = AppIcon.Icon,
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
