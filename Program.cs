using System;
using System.IO;
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

	/// <summary>
	/// Prints distro detection and the resulting install hints without touching
	/// anything. Takes an optional os-release path so mappings for other
	/// distros can be validated from fixture files - e.g.
	/// ampersand --probe-distro /tmp/opencode/os-release-steamos
	/// Note: only the os-release source is overridable; binary probes and the
	/// ostree-booted marker always describe this machine.
	/// </summary>
	public const string ProbeDistroArgument = "--probe-distro";

	/// <summary>
	/// Creates (or, with <see cref="RevertCaseAliasesArgument"/>, removes) the
	/// lowercase image-source links the Linux content build needs. Runs
	/// automatically before Setup.sh; these modes exist so a checkout can be
	/// primed or cleaned by hand - see CaseAliases.
	/// </summary>
	public const string CaseAliasesArgument = "--case-aliases";

	/// <summary>
	/// Removes exactly the links <see cref="CaseAliasesArgument"/> and
	/// <see cref="RepairContentTwinsArgument"/> created, using Ampersand's own
	/// journal. The GUI exposes the same operation as the sidebar's "Remove
	/// case aliases" tool.
	/// </summary>
	public const string RevertCaseAliasesArgument = "--revert-case-aliases";

	/// <summary>
	/// Reports lowercase twin directories that a shader <c>#include</c> can be
	/// resolved into (the compiled-output twin shadowing the authored
	/// directory). Read-only; exits 1 when one is not linked yet, which is what
	/// the build preflight does before Setup.sh. See ContentTwins.
	/// </summary>
	public const string ContentTwinsArgument = "--content-twins";

	/// <summary>
	/// Links the real directory's shader includes into a shadowing twin, under
	/// both spellings. Additive and journaled, so
	/// <see cref="RevertCaseAliasesArgument"/> removes exactly those links.
	/// </summary>
	public const string RepairContentTwinsArgument = "--repair-content-twins";

	/// <summary>
	/// Marks game/bin/managed as verified. For a tree built outside Ampersand -
	/// <c>SboxBuild build</c> by hand, or the engine's own Setup.sh - so the next
	/// launch does not have to be told to trust it. Takes an optional repo path
	/// (apps/build.sh passes one), otherwise the saved/derived root.
	/// </summary>
	public const string RecordManagedArgument = "--record-managed";

	/// <summary>
	/// Printed for --help and for an unrecognised switch. The GUI is the default
	/// when there are no arguments, so an unknown switch - the case when a script
	/// newer than the binary beside it passes a flag the binary predates - used
	/// to open a window instead of failing. In a build script that window blocks
	/// the build until it is closed, and closing it reads as success.
	/// </summary>
	private const string SupportedOptions =
		"  --dependency-check               ldd sweep, host and steamrt4\n"
		+ "  --bootstrap [--skip-deps]        build through the engine's Setup.sh\n"
		+ "  --probe-distro [os-release]      distro detection, no side effects\n"
		+ "  --case-aliases [ROOT] [--dry-run]\n"
		+ "  --revert-case-aliases [ROOT] [--dry-run]\n"
		+ "  --content-twins [ROOT]\n"
		+ "  --repair-content-twins [ROOT] [--dry-run]\n"
		+ "  --record-managed [ROOT]";

	[STAThread]
	private static int Main( string[] args )
	{
		if ( args.Length > 0 && args[0] == DependencyCheckArgument )
			return DependencyCheckMain();
		if ( args.Length > 0 && args[0] == BootstrapArgument )
			return BootstrapMain( args );
		if ( args.Length > 0 && args[0] == ProbeDistroArgument )
			return ProbeDistroMain( args );
		if ( args.Length > 0 && args[0] == CaseAliasesArgument )
			return CaseAliasesMain( args, revert: false );
		if ( args.Length > 0 && args[0] == RevertCaseAliasesArgument )
			return CaseAliasesMain( args, revert: true );
		if ( args.Length > 0 && args[0] == ContentTwinsArgument )
			return ContentTwinsMain( args, repair: false );
		if ( args.Length > 0 && args[0] == RepairContentTwinsArgument )
			return ContentTwinsMain( args, repair: true );
		if ( args.Length > 0 && args[0] == RecordManagedArgument )
			return RecordManagedMain( args );

		// Anything else that looks like a switch is a mistake, not a request to
		// open the window. Fail closed and say what exists: a GUI started by
		// accident is either invisible (headless, exit 134) or blocks a script.
		if ( args.Length > 0 && args[0].StartsWith( "-", StringComparison.Ordinal ) )
		{
			if ( args[0] is "--help" or "-h" )
			{
				Console.WriteLine( "ampersand - s&box launcher" );
				Console.WriteLine( "usage: ampersand [--help]" );
				Console.WriteLine( SupportedOptions );
				return 0;
			}

			Console.Error.WriteLine( "ampersand: unknown option " + args[0] );
			Console.Error.WriteLine( "supported:" );
			Console.Error.WriteLine( SupportedOptions );
			return 2;
		}

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

		var code = Bootstrap.Run( root, Console.WriteLine, skipDeps );

		Console.WriteLine();
		Console.Write( "Press Enter to close." );
		Console.ReadLine();

		return code;
	}

	/// <summary>
	/// One-shot access to the case-alias pass for a checkout built outside the
	/// launcher. Deliberately no "Press Enter" hold: this is a scripting mode,
	/// unlike the report modes that run inside a terminal window.
	/// </summary>
	private static int CaseAliasesMain( string[] args, bool revert )
	{
		var dryRun = false;
		string? explicitRoot = null;
		for ( var i = 1; i < args.Length; i++ )
		{
			if ( args[i] == "--dry-run" ) { dryRun = true; continue; }
			if ( args[i].StartsWith( "-", StringComparison.Ordinal ) )
			{
				Console.Error.WriteLine( "ampersand: unknown option " + args[i] );
				return 1;
			}

			explicitRoot ??= args[i];
		}

		string? root;
		if ( explicitRoot is not null )
		{
			if ( !Directory.Exists( Path.Combine( explicitRoot, "game" ) ) )
			{
				Console.Error.WriteLine( "ampersand: no game/ under " + explicitRoot );
				return 1;
			}

			root = Path.GetFullPath( explicitRoot );
		}
		else
		{
			root = SboxSettings.Resolve() ?? RepoRoot.Find();
		}

		if ( root is null )
		{
			Console.Error.WriteLine( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			Console.Error.WriteLine( "hint: set it in the launcher (Replace s&box path) or run from the repo checkout" );
			return 1;
		}

		if ( revert )
		{
			var result = CaseAliases.Revert( root, Console.WriteLine, dryRun );
			Console.WriteLine( result.Describe() );
			return result.Skipped > 0 ? 1 : 0;
		}

		var aliases = CaseAliases.Ensure( root, Console.WriteLine, dryRun );
		Console.WriteLine( aliases.Describe() );
		return aliases.Conflicts > 0 ? 1 : 0;
	}

	/// <summary>
	/// The twin preflight and its repair. No "Press Enter" hold: the preflight
	/// runs inside builds and scripts, and the exit code is the answer.
	/// </summary>
	private static int ContentTwinsMain( string[] args, bool repair )
	{
		var dryRun = false;
		string? explicitRoot = null;
		for ( var i = 1; i < args.Length; i++ )
		{
			if ( args[i] == "--dry-run" ) { dryRun = true; continue; }
			if ( args[i].StartsWith( "-", StringComparison.Ordinal ) )
			{
				Console.Error.WriteLine( "ampersand: unknown option " + args[i] );
				return 1;
			}

			explicitRoot ??= args[i];
		}

		string? root;
		if ( explicitRoot is not null )
		{
			// Same contract as --record-managed: a path that is not a checkout
			// is a typo, not an invitation to fall back to the saved one.
			if ( !Directory.Exists( Path.Combine( explicitRoot, "game" ) ) )
			{
				Console.Error.WriteLine( "ampersand: no game/ under " + explicitRoot );
				return 1;
			}

			root = Path.GetFullPath( explicitRoot );
		}
		else
		{
			root = SboxSettings.Resolve() ?? RepoRoot.Find();
		}

		if ( root is null )
		{
			Console.Error.WriteLine( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			Console.Error.WriteLine( "hint: set it in the launcher (Replace s&box path) or run from the repo checkout" );
			return 1;
		}

		if ( repair )
		{
			var repaired = ContentTwins.Repair( root, Console.WriteLine, dryRun );
			Console.WriteLine( repaired.Describe() );

			// A repair that could not record its links leaves no revert path, so
			// it is not a success even though the links work.
			return repaired.Conflicts > 0 || !repaired.Journaled ? 1 : 0;
		}

		var scan = ContentTwins.Scan( root );
		foreach ( var line in ContentTwins.Explain( root, scan ) )
			Console.WriteLine( line );

		Console.WriteLine( scan.Describe() );
		if ( scan.Skipped )
			return 0;

		if ( scan.HasUnrepaired )
		{
			Console.WriteLine( "repair: ampersand " + RepairContentTwinsArgument + "  (adds include links; deletes nothing)" );
			return 1;
		}

		return 0;
	}

	/// <summary>
	/// No "Press Enter" hold: this is a one-shot helper for scripts.
	/// </summary>
	private static int RecordManagedMain( string[] args )
	{
		string? root = null;

		if ( args.Length > 1 )
		{
			// An explicit path must be the tree, not a typo that silently falls
			// back to whatever root the settings happen to hold.
			if ( !Directory.Exists( args[1] ) )
			{
				Console.Error.WriteLine( "ampersand: no such directory: " + args[1] );
				return 1;
			}

			root = Path.GetFullPath( args[1] );
		}

		root ??= SboxSettings.Resolve() ?? RepoRoot.Find();

		if ( root is null || !Directory.Exists( Path.Combine( root, "game" ) ) )
		{
			Console.Error.WriteLine( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			Console.Error.WriteLine( "hint: ampersand " + RecordManagedArgument + " /path/to/sbox" );
			return 1;
		}

		return ManagedTree.Record( root, Console.WriteLine ) ? 0 : 1;
	}

	private static int ProbeDistroMain( string[] args )
	{
		var distro = args.Length > 1
			? SteamRt4Compat.DetectDistro( args[1] )
			: SteamRt4Compat.DetectDistro();

		Console.WriteLine( "family:     " + distro.Family );
		Console.WriteLine( "manager:    " + distro.Manager );
		Console.WriteLine( "escalation: " +
			( distro.Escalation == "" ? ( distro.IsRoot ? "(running as root)" : "(none found)" )
				: "'" + distro.Escalation.Trim() + "'" ) );
		Console.WriteLine( "atomic:     " + ( distro.Atomic ? "yes" : "no" ) );

		if ( distro.Caveat != "" )
			Console.WriteLine( "caveat:    " + distro.Caveat.Trim() );

		foreach ( var library in SteamRt4Compat.RequiredLibraries )
			Console.WriteLine( "required " + library + " -> " + distro.FormatInstall(
				SteamRt4Compat.PackageFor( distro.Family, library ) ) );

		foreach ( var library in SteamRt4Compat.BestEffortLibraries )
			Console.WriteLine( "optional " + library + " -> " + distro.FormatInstall(
				SteamRt4Compat.PackageFor( distro.Family, library ) ) );

		return 0;
	}

	private static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.LogToTrace();
	}
}
