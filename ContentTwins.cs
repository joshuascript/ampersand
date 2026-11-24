using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ampersand;

/// <summary>
/// Lowercase twin directories that shadow a shader include tree.
/// <para>
/// The shader preprocessor resolves an <c>#include</c> through the engine's
/// own directory walker, not through <c>open</c>: it lists the parent and
/// matches the component itself. The content build publishes compiled outputs
/// into lowercased directories, so when a shader directory has one
/// (<c>.../common/ddgi/</c> beside <c>.../common/DDGI/</c>) the walker can land
/// in the twin, find none of the includes inside it, and stop - no fallback,
/// and no syscall for the casefold shim to intercept. One such twin took out
/// 47 of 105 shaders with <c>'common/DDGI/DDGI.hlsl' file not found</c> while
/// the include was sitting right next to it.
/// </para>
/// <para>
/// The fix here is additive: link the real directory's include files into the
/// twin under their own spelling and their lowercased one, so the include
/// resolves whichever path the preprocessor lands on. Nothing is deleted and
/// nothing is moved - the compiled outputs stay exactly where the build put
/// them. Inside a twin, subdirectories are resolved case-insensitively and an
/// existing one is reused rather than shadowed by a new spelling: creating a
/// case variant of a directory there would mint a fresh twin pair and the
/// repair would never converge. Links are recorded in
/// <see cref="CaseAliases"/>' journal, so <c>ampersand --revert-case-aliases</c>
/// removes them together with the image aliases and nothing else.
/// </para>
/// <para>
/// Only twins that can actually break an include are reported: the twin holds
/// nothing but published output (<c>*_c</c>, <c>.meta</c>, image links, and -
/// after a repair - the include links this class made), the mixed-case sibling
/// holds include files (<c>.hlsl</c>, <c>.h</c>, <c>.inc</c>, <c>.hlsli</c>,
/// <c>.fxc</c>), and the twin sits inside a shader root (any directory named
/// <c>shaders</c> under <c>game/</c>). The hundreds of lowercase texture and
/// model twins under citizens' assets are the casefold shim's happy path and
/// are never touched. A directory that cannot be read is counted and reported
/// rather than silently treated as clean. A link that resolves outside the
/// checkout is not followed at all: its tree is outside the journal's reach, so
/// a twin there could not be repaired or reverted safely.
/// </para>
/// <para>
/// Diagnostics: <c>ampersand --content-twins</c>, repair with
/// <c>ampersand --repair-content-twins [--dry-run]</c>; escape hatch
/// <c>SBOX_SKIP_CONTENT_TWINS=1</c>.
/// </para>
/// </summary>
internal static class ContentTwins
{
	public const string SkipVariable = "SBOX_SKIP_CONTENT_TWINS";

	/// <summary>
	/// Files a shader preprocessor pulls in with <c>#include</c>. <c>.fxc</c> is
	/// not a typo: the engine's own shaders include Shader Model 3 sources by
	/// that extension (208 directives in this checkout).
	/// </summary>
	private static readonly string[] IncludeExtensions = { ".hlsl", ".h", ".inc", ".hlsli", ".fxc" };

	/// <summary>
	/// Sidecars a published output directory may also hold. They carry no
	/// content the compiler resolves, so they do not make a twin a copy.
	/// </summary>
	private static readonly string[] SidecarExtensions = { ".meta" };

	/// <summary>
	/// One lowercase directory that a shader include can be resolved into.
	/// <see cref="Includes"/> are relative to <see cref="RealDir"/>;
	/// <see cref="Missing"/> counts the link spellings that are not there yet
	/// (each include needs one or two: as written, and with the file name
	/// lowercased).
	/// </summary>
	internal sealed record ShadowingTwin(
		string Root, string TwinDir, string RealDir, IReadOnlyList<string> Includes, int Missing )
	{
		internal bool Repaired => Missing == 0;

		/// <summary>The include path as the preprocessor spells it, from the shader root.</summary>
		internal string IncludePath( string include ) => Path.GetRelativePath( Root, Path.Combine( RealDir, include ) );
	}

	internal readonly record struct TwinScan(
		IReadOnlyList<ShadowingTwin> Twins, int Roots, int Unreadable, bool Skipped, string? Note )
	{
		internal bool HasUnrepaired => Twins.Any( t => !t.Repaired );

		internal IReadOnlyList<ShadowingTwin> Unrepaired => Twins.Where( t => !t.Repaired ).ToList();

		internal string Describe()
		{
			if ( Skipped ) return "content twins: check skipped (" + ( Note ?? SkipVariable ) + ")";

			var suffix = Unreadable > 0 ? $", {Unreadable} unreadable" : string.Empty;
			if ( Twins.Count == 0 ) return "content twins: none (" + Roots + " shader roots" + suffix + ")";

			var repaired = Twins.Count( t => t.Repaired );
			var text = "content twins: " + Twins.Count + " shadowing";
			if ( repaired > 0 ) text += $", {repaired} already repaired";
			return text + suffix;
		}
	}

	internal readonly record struct RepairResult(
		int Twins, int Links, int Already, int Conflicts, int DirsCreated, bool Journaled, bool DryRun, string? Note )
	{
		internal string Describe()
		{
			if ( Note is not null && Links == 0 && Conflicts == 0 )
				return "content twins: " + Note;

			if ( Links == 0 && Conflicts == 0 && DirsCreated == 0 )
				return Already > 0
					? $"content twins: nothing to repair ({Already} links already in place)"
					: "content twins: nothing to repair";

			var twins = Twins == 1 ? "1 twin directory" : Twins + " twin directories";
			var text = DryRun
				? $"content twins: {Links} include links would be created in {twins}"
				: $"content twins repaired: {Links} include links created in {twins}";
			if ( Already > 0 ) text += $", {Already} already";
			if ( DirsCreated > 0 ) text += $", {DirsCreated} dirs created";
			if ( Conflicts > 0 ) text += $", {Conflicts} conflicts";
			if ( !Journaled ) text += " (not journaled - --revert-case-aliases will not remove them)";
			return text;
		}
	}

	// ---- discovery -------------------------------------------------------

	/// <summary>
	/// Finds every directory named <c>shaders</c> under <c>game/</c>. Includes
	/// are resolved from these roots, so a twin anywhere below one can shadow
	/// them; build state (<c>bin</c>, <c>.source2</c>) is skipped. A shader root
	/// reached through a link inside the checkout is followed, one pointing out
	/// of it is not.
	/// </summary>
	internal static IReadOnlyList<string> Roots( string repoRoot, Action<string>? onUnreadable = null )
	{
		var roots = new List<string>();
		var game = Path.Combine( repoRoot, "game" );
		if ( !Directory.Exists( game ) ) return roots;

		void Unreadable( string dir ) => onUnreadable?.Invoke( dir );

		// Pruned before descending: game/bin and game/.source2 hold thousands of
		// build-state directories and never a shader root.
		bool Skip( string dir ) => Path.GetRelativePath( game, dir )
			.Split( Path.DirectorySeparatorChar ).Any( p => p is "bin" or ".source2" );

		// A link and its target both look like shader roots; keep one of each
		// real directory so a twin is reported once, not once per path to it.
		var seen = new HashSet<string>( StringComparer.Ordinal );

		foreach ( var dir in Directories( game, repoRoot, Unreadable, Skip ) )
		{
			if ( !string.Equals( Path.GetFileName( dir ), "shaders", StringComparison.OrdinalIgnoreCase ) ) continue;
			if ( !seen.Add( Canonical( dir ) ) ) continue;

			roots.Add( Path.GetFullPath( dir ) );
		}

		roots.Sort( StringComparer.Ordinal );
		return roots;
	}

	/// <summary>
	/// Reports the twins that can break an include. Read-only and cheap: the
	/// shader roots are a few hundred directories, not the whole asset tree.
	/// </summary>
	internal static TwinScan Scan( string repoRoot )
	{
		var skip = Environment.GetEnvironmentVariable( SkipVariable );
		if ( !string.IsNullOrEmpty( skip ) && skip != "0" && !skip.Equals( "false", StringComparison.OrdinalIgnoreCase ) )
			return new TwinScan( Array.Empty<ShadowingTwin>(), 0, 0, true, SkipVariable );

		if ( !OperatingSystem.IsLinux() )
			return new TwinScan( Array.Empty<ShadowingTwin>(), 0, 0, true, "Linux only" );

		try
		{
			var unreadable = new HashSet<string>( StringComparer.Ordinal );
			void Unreadable( string dir ) => unreadable.Add( dir );

			var roots = Roots( repoRoot, Unreadable );
			var twins = new List<ShadowingTwin>();

			foreach ( var root in roots )
				ScanRoot( repoRoot, root, twins, Unreadable );

			twins.Sort( ( a, b ) => string.CompareOrdinal( a.TwinDir, b.TwinDir ) );
			return new TwinScan( twins, roots.Count, unreadable.Count, false, null );
		}
		catch ( Exception e )
		{
			return new TwinScan( Array.Empty<ShadowingTwin>(), 0, 0, true, e.Message );
		}
	}

	private static void ScanRoot( string repoRoot, string root, List<ShadowingTwin> twins, Action<string> onUnreadable )
	{
		foreach ( var dir in Directories( root, repoRoot, onUnreadable ) )
		{
			var names = ChildDirectoryNames( dir, onUnreadable );
			foreach ( var name in names )
			{
				// The twin is the all-lowercase one; only a sibling that differs
				// solely in case makes this a pair.
				if ( !string.Equals( name, name.ToLowerInvariant(), StringComparison.Ordinal ) ) continue;

				var variants = names
					.Where( other => !string.Equals( other, name, StringComparison.Ordinal )
						&& string.Equals( other.ToLowerInvariant(), name, StringComparison.Ordinal ) )
					.ToList();
				if ( variants.Count == 0 ) continue;

				var twinDir = Path.Combine( dir, name );

				// A twin that holds any source of its own is a copy, not a shadow.
				if ( !HoldsOnlyOutputs( twinDir, onUnreadable ) ) continue;

				// With three case variants (ddgi, Ddgi, DDGI) the ordinal-first
				// sibling may be an empty output directory while another holds
				// the includes, so look until one does.
				string? realDir = null;
				IReadOnlyList<string>? includes = null;
				foreach ( var candidate in variants )
				{
					var candidateDir = Path.Combine( dir, candidate );
					var found = Includes( candidateDir, onUnreadable );
					if ( found.Count == 0 ) continue;

					realDir = candidateDir;
					includes = found;
					break;
				}

				if ( realDir is null || includes is null ) continue;

				twins.Add( new ShadowingTwin( root, twinDir, realDir, includes,
					CountMissing( twinDir, realDir, includes ) ) );
			}
		}
	}

	/// <summary>
	/// Directories below <paramref name="root"/> (inclusive), visited once each.
	/// Links are followed only when they resolve inside the checkout: a shader
	/// tree can be linked in from elsewhere in the tree, but following one out
	/// of it would have repair write where the journal cannot reach.
	/// </summary>
	private static IEnumerable<string> Directories(
		string root, string repoRoot, Action<string> onUnreadable, Func<string, bool>? skip = null )
	{
		var stack = new Stack<string>();
		stack.Push( root );

		var seen = new HashSet<string>( StringComparer.Ordinal ) { Canonical( root ) };

		while ( stack.Count > 0 )
		{
			var dir = stack.Pop();
			if ( skip is not null && skip( dir ) ) continue;

			yield return dir;

			string[] children;
			try { children = Directory.GetDirectories( dir ); }
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
			{
				onUnreadable( dir );
				continue;
			}

			foreach ( var child in children )
			{
				if ( skip is not null && skip( child ) ) continue;

				var link = CaseAliases.LinkTargetAbsolute( child );
				if ( link is not null )
				{
					if ( !IsUnder( repoRoot, link ) ) continue;
					if ( !seen.Add( Canonical( child ) ) ) continue;
					stack.Push( child );
					continue;
				}

				stack.Push( child );
			}
		}
	}

	private static string Canonical( string path )
	{
		try { return new DirectoryInfo( path ).ResolveLinkTarget( true )?.FullName ?? Path.GetFullPath( path ); }
		catch { return Path.GetFullPath( path ); }
	}

	private static List<string> ChildDirectoryNames( string dir, Action<string> onUnreadable )
	{
		string[] entries;
		try { entries = Directory.GetFileSystemEntries( dir ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			onUnreadable( dir );
			return new List<string>();
		}

		var names = new List<string>();
		foreach ( var entry in entries )
		{
			if ( CaseAliases.LinkTargetAbsolute( entry ) is not null ) continue;
			try { if ( Directory.Exists( entry ) ) names.Add( Path.GetFileName( entry ) ); }
			catch { }
		}

		names.Sort( StringComparer.Ordinal );
		return names;
	}

	/// <summary>
	/// True when the directory holds nothing but published output: <c>*_c</c>
	/// files, <c>.meta</c> sidecars, links to raw images, include links this
	/// class made, and directories of the same. An empty twin counts - the
	/// walker would still land in it - and repair fills it.
	/// </summary>
	private static bool HoldsOnlyOutputs( string dir, Action<string> onUnreadable )
	{
		string[] entries;
		try { entries = Directory.GetFileSystemEntries( dir ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			onUnreadable( dir );
			return false;
		}

		foreach ( var entry in entries )
		{
			var link = CaseAliases.LinkTargetAbsolute( entry );
			if ( link is not null )
			{
				// Image links the compiler made, and include links an earlier
				// repair made (counting those is what keeps a half-repaired twin
				// visible instead of silently healed). Anything else linked in is
				// a source the twin was given, not an output.
				if ( !CaseAliases.IsImage( link ) && !IsInclude( link ) ) return false;
				continue;
			}

			bool isDirectory;
			try { isDirectory = Directory.Exists( entry ); }
			catch { isDirectory = false; }

			if ( isDirectory )
			{
				if ( !HoldsOnlyOutputs( entry, onUnreadable ) ) return false;
				continue;
			}

			var name = Path.GetFileName( entry );
			if ( !name.EndsWith( "_c", StringComparison.Ordinal ) && !IsSidecar( name ) ) return false;
		}

		return true;
	}

	/// <summary>Include files below the real directory, relative to it.</summary>
	private static IReadOnlyList<string> Includes( string dir, Action<string> onUnreadable )
	{
		var found = new List<string>();
		foreach ( var path in Files( dir, onUnreadable ) )
		{
			if ( !IsInclude( path ) ) continue;
			found.Add( Path.GetRelativePath( dir, path ) );
		}

		found.Sort( StringComparer.Ordinal );
		return found;
	}

	private static IEnumerable<string> Files( string dir, Action<string> onUnreadable )
	{
		foreach ( var current in Directories( dir, dir, onUnreadable ) )
		{
			string[] entries;
			try { entries = Directory.GetFiles( current ); }
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { continue; }

			foreach ( var entry in entries ) yield return entry;
		}
	}

	internal static bool IsInclude( string path )
	{
		var extension = Path.GetExtension( path );
		foreach ( var candidate in IncludeExtensions )
			if ( string.Equals( extension, candidate, StringComparison.OrdinalIgnoreCase ) )
				return true;
		return false;
	}

	private static bool IsSidecar( string name )
	{
		foreach ( var candidate in SidecarExtensions )
			if ( name.EndsWith( candidate, StringComparison.OrdinalIgnoreCase ) )
				return true;
		return false;
	}

	private static bool IsUnder( string root, string path )
	{
		var relative = Path.GetRelativePath( root, path );
		return relative != ".." && !relative.StartsWith( ".." + Path.DirectorySeparatorChar, StringComparison.Ordinal );
	}

	/// <summary>
	/// The twin-side paths an include has to exist at: the real file name and
	/// its lowercased spelling, in the twin directory that corresponds to the
	/// include's own subdirectory. Existing directories are matched
	/// case-insensitively and reused, so a repair never creates a sibling that
	/// differs only in case from one that is already there.
	/// </summary>
	internal static IReadOnlyList<string> LinkPaths( string twinDir, string include )
	{
		var directory = ResolveDirectory( twinDir, Path.GetDirectoryName( include ) );
		var name = Path.GetFileName( include );

		var paths = new List<string>( 2 ) { Path.Combine( directory, name ) };
		var lower = name.ToLowerInvariant();
		if ( !string.Equals( lower, name, StringComparison.Ordinal ) )
			paths.Add( Path.Combine( directory, lower ) );

		return paths;
	}

	private static string ResolveDirectory( string twinDir, string? relative )
	{
		if ( string.IsNullOrEmpty( relative ) || relative == "." ) return twinDir;

		var current = twinDir;
		foreach ( var component in relative.Split( Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries ) )
		{
			string[] children;
			try { children = Directory.GetDirectories( current ); }
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { children = Array.Empty<string>(); }

			var names = new List<string>();
			foreach ( var child in children )
				if ( CaseAliases.LinkTargetAbsolute( child ) is null ) names.Add( Path.GetFileName( child ) );

			var chosen =
				names.FirstOrDefault( n => string.Equals( n, component, StringComparison.Ordinal ) )
				?? names.FirstOrDefault( n => string.Equals( n, component, StringComparison.OrdinalIgnoreCase ) )
				?? component;

			current = Path.Combine( current, chosen );
		}

		return current;
	}

	private static int CountMissing( string twinDir, string realDir, IReadOnlyList<string> includes )
	{
		var missing = 0;
		foreach ( var include in includes )
		{
			var target = Path.GetFullPath( Path.Combine( realDir, include ) );
			foreach ( var path in LinkPaths( twinDir, include ) )
			{
				var link = CaseAliases.LinkTargetAbsolute( path );
				if ( link is null || !CaseAliases.SamePath( link, target ) ) missing++;
			}
		}

		return missing;
	}

	// ---- repair ----------------------------------------------------------

	/// <summary>
	/// Links every include of a shadowing twin into it, under both spellings.
	/// Existing files, foreign links and compiled outputs are left alone; the
	/// created links and directories are journaled with the image aliases (and
	/// with their targets, so a later retargeted link is not mistaken for one
	/// of ours) so one revert removes both. Idempotent.
	/// </summary>
	internal static RepairResult Repair( string repoRoot, Action<string>? log = null, bool dryRun = false )
	{
		if ( !OperatingSystem.IsLinux() )
			return new RepairResult( 0, 0, 0, 0, 0, true, dryRun, "Linux only" );

		var scan = Scan( repoRoot );
		if ( scan.Skipped )
			return new RepairResult( 0, 0, 0, 0, 0, true, dryRun, scan.Note );

		void Report( string message ) => log?.Invoke( message );

		if ( scan.Unreadable > 0 )
		{
			Report( $"content twins: {scan.Unreadable} directories could not be read -"
				+ " a twin below one of them would not be repaired" );
		}

		var files = new List<string>();
		var dirs = new HashSet<string>( StringComparer.Ordinal );
		var targets = new Dictionary<string, string>( StringComparer.Ordinal );
		int twins = 0, linked = 0, already = 0, conflicts = 0, reported = 0;

		foreach ( var twin in scan.Twins )
		{
			var touched = false;
			foreach ( var include in twin.Includes )
			{
				var real = Path.GetFullPath( Path.Combine( twin.RealDir, include ) );
				foreach ( var path in LinkPaths( twin.TwinDir, include ) )
				{
					var existing = CaseAliases.LinkTargetAbsolute( path );
					if ( existing is not null )
					{
						if ( CaseAliases.SamePath( existing, real ) ) already++;
						else
						{
							conflicts++;
							if ( reported++ < 10 ) Report( "content twins: left " + path + " alone (a symlink to something else)" );
						}
						continue;
					}

					if ( File.Exists( path ) || Directory.Exists( path ) )
					{
						conflicts++;
						if ( reported++ < 10 ) Report( "content twins: left " + path + " alone (something is already there)" );
						continue;
					}

					touched = true;
					if ( dryRun )
					{
						linked++;
						continue;
					}

					try
					{
						var missing = MissingParents( twin.TwinDir, path );
						if ( missing is null )
						{
							conflicts++;
							if ( reported++ < 10 ) Report( "content twins: could not create the parent directory of " + path );
							continue;
						}

						foreach ( var dir in missing )
						{
							Directory.CreateDirectory( dir );
							dirs.Add( dir );
						}

						// Relative target: the checkout stays relocatable.
						File.CreateSymbolicLink( path, Path.GetRelativePath( Path.GetDirectoryName( path )!, real ) );
						files.Add( path );
						targets[Path.GetRelativePath( repoRoot, path )] = Path.GetRelativePath( repoRoot, real );
						linked++;
					}
					catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
					{
						conflicts++;
						if ( reported++ < 10 ) Report( "content twins: could not link " + path + " (" + e.Message + ")" );
					}
				}
			}

			if ( touched ) twins++;
		}

		var journaled = true;
		if ( !dryRun && ( files.Count > 0 || dirs.Count > 0 ) )
		{
			var relativeFiles = files.Select( f => Path.GetRelativePath( repoRoot, f ) ).ToList();
			journaled = CaseAliases.AddToJournal(
				repoRoot,
				relativeFiles,
				dirs.Select( d => Path.GetRelativePath( repoRoot, d ) ),
				targets );

			// A failed record is already reported by Describe(); the links exist
			// but there is no revert path, which the caller turns into a non-zero
			// exit.
			if ( journaled )
				CaseAliases.CheckGitIgnore( repoRoot, relativeFiles, Report );
		}

		return new RepairResult( twins, linked, already, conflicts, dirs.Count, journaled, dryRun, null );
	}

	/// <summary>
	/// The directories between <paramref name="root"/> and the target's parent,
	/// outermost first. Null when a plain file blocks the path.
	/// </summary>
	private static List<string>? MissingParents( string root, string target )
	{
		var parent = Path.GetDirectoryName( target );
		if ( parent is null ) return null;

		var missing = new List<string>();
		for ( var dir = parent; dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName( dir ) )
		{
			if ( Directory.Exists( dir ) ) break;
			if ( File.Exists( dir ) ) return null;
			missing.Add( dir );
		}

		if ( missing.Count == 0 && !Directory.Exists( parent ) ) return null;
		missing.Reverse();
		return missing;
	}

	// ---- diagnostics -----------------------------------------------------

	/// <summary>
	/// Short lines that name the twin, the directory it shadows and the include
	/// that will fail, in the shape the shader compiler prints it. Used by the
	/// build preflight and the launch gate.
	/// </summary>
	internal static IReadOnlyList<string> Explain( string repoRoot, TwinScan scan )
	{
		var lines = new List<string>();
		foreach ( var twin in scan.Unrepaired )
		{
			var twinRelative = Path.GetRelativePath( repoRoot, twin.TwinDir );
			var realRelative = Path.GetRelativePath( repoRoot, twin.RealDir );
			lines.Add( twinRelative + " shadows " + realRelative );

			var first = twin.IncludePath( twin.Includes[0] );
			lines.Add( $"  the next shader compile fails with: fatal error: '{first}' file not found" );
			if ( twin.Includes.Count > 1 )
				lines.Add( $"  ({twin.Includes.Count} include files in that directory need a link)" );

			var total = twin.Includes.Sum( include => LinkPaths( twin.TwinDir, include ).Count );
			if ( twin.Missing > 0 )
				lines.Add( $"  {twin.Missing} of {total} link spellings are missing" );
		}

		return lines;
	}
}
