using Avalonia.Media;

namespace Ampersand;

/// <summary>
/// A fixed dark palette for the whole window, in the manner of a code editor.
///
/// Panels are told apart by BACKGROUND rather than by whitespace: they sit
/// flush against each other, separated only by a 1px rule. The lightness runs
/// as a depth gradient - the sidebar is the shallowest surface and the target
/// panel the deepest, which is the convention in VS Code and the JetBrains IDEs.
///
/// It was named for the terminal pane it was built around. The pane is gone and
/// the name is kept only because every reference in the window uses it; what
/// survives is the panel palette, which was never terminal-specific.
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

	/// <summary>The window behind everything. The deepest surface.</summary>
	public static readonly IBrush Background = new SolidColorBrush( Color.FromRgb( 0x15, 0x17, 0x1F ) );

	/// <summary>Body text.</summary>
	public static readonly IBrush Normal = new SolidColorBrush( Color.FromRgb( 0xD4, 0xD4, 0xD4 ) );

	/// <summary>The launcher's own accent, used for the sidebar glyph.</summary>
	public static readonly IBrush Launcher = new SolidColorBrush( Color.FromRgb( 0x4F, 0xC1, 0xFF ) );
}
