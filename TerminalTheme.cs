using Avalonia.Media;

namespace Ampersand;

/// <summary>
/// How much a line matters. Drives colour only - nothing is ever hidden.
/// </summary>
internal enum LineKind
{
	/// <summary>Indented continuation; takes the kind of the header above it.</summary>
	Continuation,
	Normal,
	Muted,
	Warning,
	Error,
	/// <summary>The launcher's own messages.</summary>
	Launcher,
	/// <summary>The echoed "$ command" line.</summary>
	Command
}

/// <summary>
/// A fixed dark palette for the whole window, in the manner of a code editor.
///
/// Panels are told apart by BACKGROUND rather than by whitespace: they sit
/// flush against each other, separated only by a 1px rule. The lightness runs
/// as a depth gradient - the sidebar is the shallowest surface and the terminal
/// the deepest, which is the convention in VS Code and the JetBrains IDEs.
/// </summary>
internal static class TerminalTheme
{
	// --- panel surfaces, lightest to darkest -------------------------------

	/// <summary>Left action panel. The shallowest surface.</summary>
	public static readonly IBrush SidebarPanel = new SolidColorBrush( Color.FromRgb( 0x28, 0x2C, 0x3A ) );

	/// <summary>Middle settings strip. Reads as a toolbar, so it sits between.</summary>
	public static readonly IBrush ToolbarPanel = new SolidColorBrush( Color.FromRgb( 0x22, 0x26, 0x34 ) );

	/// <summary>Launch targets. Darker than the sidebar, as asked.</summary>
	public static readonly IBrush TargetPanel = new SolidColorBrush( Color.FromRgb( 0x1B, 0x1E, 0x2A ) );

	/// <summary>The 1px rule between panels.</summary>
	public static readonly IBrush PanelBorder = new SolidColorBrush( Color.FromRgb( 0x36, 0x3C, 0x4E ) );

	/// <summary>Hover fill for sidebar command rows.</summary>
	public static readonly IBrush SidebarHover = new SolidColorBrush( Color.FromRgb( 0x33, 0x39, 0x4C ) );

	/// <summary>Pressed fill for sidebar command rows.</summary>
	public static readonly IBrush SidebarPressed = new SolidColorBrush( Color.FromRgb( 0x3B, 0x43, 0x59 ) );

	/// <summary>Sidebar section headers, in the VS Code manner: uppercase and quiet.</summary>
	public static readonly IBrush HeaderText = new SolidColorBrush( Color.FromRgb( 0x8A, 0x93, 0xA8 ) );

	/// <summary>The advisory footer. Quieter still, but not invisible.</summary>
	public static readonly IBrush FooterText = new SolidColorBrush( Color.FromRgb( 0x7A, 0x84, 0x9C ) );

	/// <summary>Terminal output. The deepest surface.</summary>
	public static readonly IBrush Background = new SolidColorBrush( Color.FromRgb( 0x15, 0x17, 0x1F ) );
	public static readonly IBrush Normal = new SolidColorBrush( Color.FromRgb( 0xD4, 0xD4, 0xD4 ) );

	// The PreJit dumps are 92% of a startup. Pushing them back is the point.
	public static readonly IBrush Muted = new SolidColorBrush( Color.FromRgb( 0x6B, 0x6F, 0x76 ) );

	public static readonly IBrush Warning = new SolidColorBrush( Color.FromRgb( 0xD7, 0xA6, 0x5F ) );
	public static readonly IBrush Error = new SolidColorBrush( Color.FromRgb( 0xF1, 0x4C, 0x4C ) );
	public static readonly IBrush Launcher = new SolidColorBrush( Color.FromRgb( 0x4F, 0xC1, 0xFF ) );
	public static readonly IBrush Command = new SolidColorBrush( Color.FromRgb( 0x98, 0xC3, 0x79 ) );

	public static IBrush BrushFor( LineKind kind )
	{
		return kind switch
		{
			LineKind.Error => Error,
			LineKind.Warning => Warning,
			LineKind.Muted => Muted,
			LineKind.Launcher => Launcher,
			LineKind.Command => Command,
			_ => Normal
		};
	}
}
