using System;
using Avalonia;

namespace Ampersand;

internal static class Program
{
	/// <summary>
	/// Runs the dependency sweep on stdout instead of starting the UI.
	///
	/// The app has no output surface of its own any more, so the sweep is printed
	/// by a second copy of this binary that the window opens inside the user's
	/// terminal emulator. Re-execing rather than piping the text back keeps the
	/// report as it was written - SGR colour and column alignment, both of which
	/// need a real terminal.
	/// </summary>
	public const string DependencyCheckArgument = "--dependency-check";
	public const string BootstrapArgument = "--bootstrap";

	[STAThread]
	private static int Main( string[] args )
	{
		if ( args.Length > 0 && args[0] == DependencyCheckArgument )
			return DependencyCheckMain();
		if ( args.Length > 0 && args[0] == BootstrapArgument )
			return BootstrapMain( args );

		return BuildAvaloniaApp().StartWithClassicDesktopLifetime( args );
	}

	private static int DependencyCheckMain()
	{
		var root = SboxSettings.Resolve() ?? RepoRoot.Find();

		if ( root is null )
		{
			Console.Error.WriteLine( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			Console.Error.WriteLine( "hint: set it in the launcher (Replace s&box path) or run from the repo checkout" );
			return 1;
		}

		DependencyCheck.Run( root, Console.WriteLine );

		// The emulator closes its window the moment the child exits, taking the
		// report with it. Nothing else here is interactive, so this hold is the
		// only thing that makes the output readable.
		Console.WriteLine();
		Console.Write( "Press Enter to close." );
		Console.ReadLine();

		return 0;
	}

	private static int BootstrapMain( string[] args )
	{
		var skipDeps = args.Length > 1 && args[1] == "--skip-deps";
		var root = SboxSettings.Resolve() ?? RepoRoot.Find();

		if ( root is null )
		{
			Console.Error.WriteLine( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			Console.Error.WriteLine( "hint: set it in the launcher (Replace s&box path) or run from the repo checkout" );
			return 1;
		}

		Bootstrap.Run( root, Console.WriteLine, skipDeps );

		Console.WriteLine();
		Console.Write( "Press Enter to close." );
		Console.ReadLine();

		return 0;
	}

	private static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.LogToTrace();
	}
}
