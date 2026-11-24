using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ampersand;

/// <summary>
/// Build S&Box through the engine's own <c>Setup.sh</c>, with ampersand's
/// native-dependency sweep as a post-flight report.
/// <para>
/// History: this used to be a full port of the old
/// <c>sbox-public/bootstrap.sh</c> (fetch natives → ldd sweep → drive SboxBuild
/// step by step). The engine now owns that flow: <c>Setup.sh</c> runs the
/// <c>bootstrap</c> stage (git hooks, per-platform artifact download, bindings,
/// managed, shaders, content) and its git hooks keep artifacts fresh after
/// pulls - so duplicating the sequence here only drifted. What Setup.sh does
/// not do is the readable per-binary <c>ldd</c> report, which stays here (and
/// in DependencyCheck) as ampersand-specific value. Launched from the sidebar
/// in a real terminal (Program.BootstrapArgument) with SGR colour.
/// </para>
/// <para>
/// Returns 0 when the build finished, 1 when it stopped early - a failed
/// stage, or a preflight that refused to start (a running build, a shadowing
/// shader-include twin). The GUI ignores the code (the terminal is the
/// report); <c>--bootstrap</c> forwards it so scripts can tell.
/// </para>
/// </summary>
internal static class Bootstrap
{
	public static int Run( string repoRoot, Action<string> emit, bool skipDeps = false )
	{
		emit( Ansi.Bold + Ansi.White + "=== build s&box ===" + Ansi.NoBold + Ansi.Reset );
		emit( Ansi.Dim + "repo  " + Ansi.Reset + repoRoot );
		emit( "" );

		var setupSh = Path.Combine( repoRoot, "Setup.sh" );
		if ( !File.Exists( setupSh ) )
		{
			emit( Ansi.Red + "  Setup.sh not found: " + setupSh + Ansi.Reset );
			emit( Ansi.Dim + "  This checkout predates the engine's Setup.sh workflow - pull sbox-public first." + Ansi.Reset );
			return 1;
		}

		if ( Which( "dotnet" ) is null )
		{
			emit( Ansi.Red + "  dotnet not on PATH - cannot run Setup.sh." + Ansi.Reset );
			emit( Ansi.Dim + "  Install .NET 10 SDK: https://dotnet.microsoft.com/download" + Ansi.Reset );
			return 1;
		}

		// --- shader include twins ---------------------------------------------
		// The content build publishes compiled outputs into lowercased paths, so
		// a lowercase directory can sit beside the authored shader directory
		// that holds the includes. The preprocessor resolves an #include through
		// that twin and then only looks inside it, which is how one empty twin
		// failed 47 of 105 shaders with a misleading "file not found" while the
		// include was sitting next to it. Checked before the lock and before the
		// verified tree is invalidated: nothing has been written yet, so a stop
		// here must not cost the user a rebuild prompt.
		var twins = ContentTwins.Scan( repoRoot );
		if ( twins.Skipped )
		{
			emit( Ansi.Dim + "  " + twins.Describe() + Ansi.Reset );
		}
		else
		{
			// A partial scan is not a clean one: say so rather than let an
			// unreadable directory look like "nothing to report".
			if ( twins.Unreadable > 0 )
			{
				emit( Ansi.Yellow + $"  content twins: {twins.Unreadable} directories could not be read -"
					+ " the check is incomplete" + Ansi.Reset );
			}

			if ( twins.HasUnrepaired )
			{
				var unrepaired = twins.Unrepaired;
				emit( Ansi.Red + "  content twins: " + unrepaired.Count + " lowercased director"
					+ ( unrepaired.Count == 1 ? "y shadows" : "ies shadow" ) + " a shader include directory" + Ansi.Reset );

				foreach ( var line in ContentTwins.Explain( repoRoot, twins ) )
					emit( Ansi.Red + "  " + line + Ansi.Reset );

				emit( "" );
				emit( Ansi.Dim + "  Shader includes are resolved by the engine's own directory walker, not by open(), so the"
					+ " casefold shim cannot rescue them." + Ansi.Reset );
				emit( Ansi.Dim + "  Repair links the real include files into the twin under both spellings - no compiled"
					+ " output is touched:" + Ansi.Reset );
				emit( "  ampersand " + Program.RepairContentTwinsArgument + "   " + Ansi.Dim
					+ "(or " + Program.ContentTwinsArgument + " to list; revert with " + Program.RevertCaseAliasesArgument + ")" + Ansi.Reset );
				emit( "" );
				emit( Ansi.Dim + "  The build did not start and game/bin/managed was not touched." + Ansi.Reset );
				return 1;
			}
		}

		// --- single-flight build ---------------------------------------------
		// Setup.sh deletes and repopulates game/bin/managed, and any other
		// build in this checkout writes the same files. Two of them at once can
		// splice one assembly from both compiles, which is how a launcher ends
		// up dying with InvalidProgramException on its next boot. See BuildLock.
		var buildLock = BuildLock.TryAcquire( repoRoot, out var lockProblem, out var busy );
		if ( buildLock is null )
		{
			emit( Ansi.Red + "  " + ( busy ? "another build is already running" : "cannot start the build" ) + Ansi.Reset );
			emit( Ansi.Dim + "  " + lockProblem + Ansi.Reset );

			if ( busy )
			{
				emit( Ansi.Dim + "  wait for it to finish, then run this again - building now would put a"
					+ " second writer on game/bin/managed" + Ansi.Reset );
			}

			return 1;
		}

		// Held to the end of the run, report included: the point is that no
		// other build starts while this one is still finishing, and the report
		// is seconds.
		using var held = buildLock;
		emit( Ansi.Dim + "  build lock: " + BuildLock.PathFor( repoRoot ) + Ansi.Reset );

		// From here the managed tree is about to be rewritten. Forget what was
		// verified first, so a Ctrl-C or a failed step leaves it unverified and
		// the next launch rebuilds instead of trusting half a build.
		ManagedTree.Invalidate( repoRoot );

		// --- toolchain exec bits (fresh downloads lose them) ------------------
		// The GUI prompts before launching; this silent re-check covers direct
		// `ampersand --bootstrap` runs and repairs with a log trail instead of
		// a second question. The engine only fixes contentbuilder itself, so
		// resourcecompiler needs us here.
		var nonExec = ToolchainExec.FindNonExecutable( repoRoot );
		if ( nonExec.Count > 0 )
		{
			emit( Ansi.Dim + "  toolchain: restoring exec bit on " + string.Join( ", ", nonExec ) + Ansi.Reset );
			if ( !ToolchainExec.TryFix( nonExec, emit ) )
				emit( Ansi.Yellow + "  toolchain: some files could not be fixed - the content step may fail" + Ansi.Reset );
			emit( "" );
		}

		// --- lowercase source aliases (native content compiler case bug) ------
		// The native resource compiler opens a texture by its lowercased
		// identity path, while sources keep their authored case; the identity
		// tree only holds published outputs, so a material whose properties
		// point at a raw image cannot find it. Per-file image links fill that
		// hole and are invisible to the ContentBuilder inventory (raw bitmaps
		// are not sources it compiles). See CaseAliases.
		var aliases = CaseAliases.Ensure( repoRoot, msg => emit( Ansi.Dim + "  " + msg + Ansi.Reset ) );
		emit( ( aliases.Conflicts > 0 || aliases.Note is not null ? Ansi.Yellow : Ansi.Dim )
			+ "  " + aliases.Describe() + Ansi.Reset );
		emit( "" );

		// --- engine setup (heavy lifting lives here) --------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- sh Setup.sh ---" + Ansi.NoBold + Ansi.Reset );

		// Case-insensitive asset fallback, required for the build rather than
		// opt-in: the model compiler opens FBX and child-model .meta references
		// at the case they were authored with, and the engine content has
		// several that only resolve on a case-insensitive filesystem. Without
		// the shim citizen fails 4 assets and the run ends "Content: Failed" for
		// a reason that is nowhere in the checkout. The Casefold toggle only
		// decides whether launches get the shim too, so this ignores it.
		Dictionary<string, string>? setupEnv = null;
		var casefoldSo = OperatingSystem.IsLinux() ? EnsureCasefoldLibrary( emit ) : null;
		var shimMissing = OperatingSystem.IsLinux() && casefoldSo is null;
		if ( casefoldSo is not null )
		{
			var existing = Environment.GetEnvironmentVariable( "LD_PRELOAD" );
			setupEnv = new Dictionary<string, string>
			{
				["LD_PRELOAD"] = string.IsNullOrEmpty( existing ) ? casefoldSo : casefoldSo + ":" + existing
			};
			emit( Ansi.Dim + "  casefold shim: " + casefoldSo + Ansi.Reset );
		}
		// Via sh, not ./. : Setup.sh is stored without the exec bit upstream.
		var setupCode = RunProcess( "sh", new[] { "Setup.sh" }, repoRoot, emit, setupEnv );
		emit( "" );

		if ( setupCode != 0 )
		{
			emit( Ansi.Red + $"  setup failed (exit {setupCode})" + Ansi.Reset );
			emit( Ansi.Dim + "  Rerun with --verbose output: sh Setup.sh --verbose (in the checkout)." + Ansi.Reset );
			emit( Ansi.Dim + "  The managed tree stays unverified: the next launch will ask to rebuild"
				+ " rather than trust a half-written build." + Ansi.Reset );

			if ( shimMissing )
			{
				emit( Ansi.Yellow + "  Note: the casefold shim was unavailable for this run. Without it the content"
					+ " step fails on the engine's wrong-case asset references (citizen: 4 assets) for reasons that"
					+ " are not in this checkout - fix the shim before treating this as a content bug." + Ansi.Reset );
			}

			return 1;
		}

		// The build owns game/bin/managed again. Fingerprint it now, so a
		// second writer afterwards (a Debug dotnet build, Setup.sh by hand) is
		// caught at the next launch instead of surfacing as a mystery crash.
		ManagedTree.Record( repoRoot, line => emit( Ansi.Dim + "  " + line + Ansi.Reset ) );

		emit( Ansi.Green + "  setup OK" + Ansi.Reset );
		emit( "" );

		// The content and shader stages write lowercased directories, so a twin
		// can appear during the run that was not there when it started. The
		// build itself succeeds; the next shader compile is what fails, so say
		// so now instead of letting the user find 47 "file not found" errors.
		var afterBuild = ContentTwins.Scan( repoRoot );
		if ( !afterBuild.Skipped && afterBuild.HasUnrepaired )
		{
			emit( Ansi.Red + "  content twins appeared during the build: the next shader compile will fail" + Ansi.Reset );
			foreach ( var line in ContentTwins.Explain( repoRoot, afterBuild ) )
				emit( Ansi.Red + "  " + line + Ansi.Reset );

			emit( Ansi.Dim + "  repair now: ampersand " + Program.RepairContentTwinsArgument
				+ " (links the real includes into the twin; deletes nothing)" + Ansi.Reset );
			emit( "" );
		}

		if ( skipDeps )
		{
			emit( Ansi.Dim + "  --skip-deps: skipping the native dependency report" + Ansi.Reset );
			emit( "" );
		}
		else
		{
			// --- native dependency report (ampersand's own check) --------------
			emit( Ansi.Bold + Ansi.Cyan + "--- native dependencies in game/bin/linuxsteamrt64 ---" + Ansi.NoBold + Ansi.Reset );
			var nativeDepsResult = CheckNativeDeps( repoRoot, emit );

			if ( nativeDepsResult == 1 )
			{
				emit( "" );
				emit( Ansi.Yellow + "  Missing libraries above are prebuilt binaries that cannot be rebuilt here -" + Ansi.Reset );
				emit( Ansi.Yellow + "  the build above still succeeded, but the editor will not run until they resolve." + Ansi.Reset );
			}
			emit( "" );
		}

		emit( Ansi.Bold + Ansi.White + "=== build done ===" + Ansi.NoBold + Ansi.Reset );
		return 0;
	}

	private static int RunProcess( string exe, IReadOnlyList<string> args, string workDir, Action<string> emit, IReadOnlyDictionary<string, string>? extraEnv = null )
	{
		var psi = new ProcessStartInfo
		{
			FileName = exe,
			WorkingDirectory = workDir,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach ( var a in args ) psi.ArgumentList.Add( a );
		if ( extraEnv is not null )
		{
			foreach ( var pair in extraEnv )
				psi.Environment[pair.Key] = pair.Value;
		}

		emit( Ansi.Dim + "  $ " + exe + " " + string.Join( " ", args.Select( Quote ) ) + Ansi.Reset );

		try
		{
			using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
			proc.OutputDataReceived += ( _, e ) => { if ( e.Data is not null ) emit( e.Data ); };
			proc.ErrorDataReceived += ( _, e ) => { if ( e.Data is not null ) emit( e.Data ); };
			proc.Start();
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();
			proc.WaitForExit();
			return proc.ExitCode;
		}
		catch ( Exception e )
		{
			emit( Ansi.Red + "  failed to start " + exe + ": " + e.Message + Ansi.Reset );
			return 127;
		}
	}

	private static string Quote( string s ) => s.Contains( ' ' ) ? "\"" + s + "\"" : s;

	/// <summary>
	/// Ensures the case-insensitive asset fallback library exists, building
	/// the vendored source (apps/patches/casefold.c) with gcc when needed.
	/// The build needs it on Linux regardless of the Casefold toggle (that
	/// toggle is about launches), so there is no opt-out here. Returns its
	/// path, or null when gcc is missing, no prebuilt copy exists, or the
	/// build fails; the caller says what that means for the content step
	/// rather than hard-failing setup on the compat layer.
	/// </summary>
	private static string? EnsureCasefoldLibrary( Action<string> emit )
	{
		var overrideSo = Environment.GetEnvironmentVariable( "SBOX_CASEFOLD_SO" );
		var so = string.IsNullOrWhiteSpace( overrideSo ) ? AppPaths.CasefoldLibrary : overrideSo;
		var src = AppPaths.FindCasefoldSource();
		if ( src is null )
		{
			// No source to compare against: a prebuilt copy is all there is.
			if ( File.Exists( so ) ) return so;
			emit( Ansi.Red + "  casefold: vendored source missing (scripts/patches/casefold.c) and no prebuilt shim at"
				+ " " + so + Ansi.Reset );
			emit( Ansi.Red + "  the content step will fail on wrong-case FBX references (citizen: 4 assets) for reasons"
				+ " that are not in this checkout" + Ansi.Reset );
			return null;
		}
		if ( File.Exists( so ) && AppPaths.IsShimUpToDate( src, so ) )
			return so;

		try { Directory.CreateDirectory( Path.GetDirectoryName( so )! ); } catch { }

		emit( Ansi.Dim + "  casefold: building shim with gcc..." + Ansi.Reset );
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "gcc",
				WorkingDirectory = Path.GetDirectoryName( src )!,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			foreach ( var arg in new[] { "-shared", "-fPIC", "-O2", "-o", so, Path.GetFileName( src ), "-ldl" } )
				psi.ArgumentList.Add( arg );
			using var proc = Process.Start( psi );
			if ( proc is null )
			{
				emit( Ansi.Red + "  casefold: could not start gcc, and no prebuilt shim exists" + Ansi.Reset );
				emit( Ansi.Red + "  the content step will fail on wrong-case FBX references (citizen: 4 assets) for reasons"
					+ " that are not in this checkout" + Ansi.Reset );
				return null;
			}
			var output = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
			proc.WaitForExit();
			if ( proc.ExitCode != 0 || !File.Exists( so ) )
			{
				emit( Ansi.Red + $"  casefold: gcc failed (exit {proc.ExitCode}); the content step will fail on wrong-case"
					+ " FBX references for reasons that are not in this checkout" + Ansi.Reset );
				emit( Ansi.Dim + "  " + output.Trim().Replace( "\n", "\n  " ) + Ansi.Reset );
				return null;
			}
		}
		catch ( Exception e )
		{
			emit( Ansi.Red + "  casefold: build failed (" + e.Message + "); the content step will fail on wrong-case"
				+ " FBX references for reasons that are not in this checkout" + Ansi.Reset );
			return null;
		}

		return so;
	}

	// ---- native dependency report (kept; Setup.sh has no per-binary report) ----

	/// <summary>
	/// Returns 0 when everything resolves, 1 when something is missing, 2 when the
	/// check could not be run at all.
	/// </summary>
	private static int CheckNativeDeps( string repoRoot, Action<string> emit )
	{
		var binDir = Path.Combine( repoRoot, "game", "bin", "linuxsteamrt64" );

		if ( Which( "ldd" ) is null )
		{
			emit( Ansi.Yellow + "  skipped: ldd not on PATH - it ships in glibc's libc-bin package" + Ansi.Reset );
			return 2;
		}

		if ( !Directory.Exists( binDir ) )
		{
			emit( Ansi.Yellow + $"  skipped: {binDir} does not exist - setup should have produced it" + Ansi.Reset );
			return 2;
		}

		var files = Directory.EnumerateFiles( binDir, "*", SearchOption.AllDirectories )
			.Concat( Directory.EnumerateFiles( binDir, "*", SearchOption.TopDirectoryOnly ) )
			.Distinct()
			.OrderBy( p => p, StringComparer.Ordinal )
			.ToList();

		// Also include symlinks (EnumerateFiles follows them on some runtimes but not reliably directory-wise)
		try
		{
			var withSymlinks = new HashSet<string>( files, StringComparer.Ordinal );
			foreach ( var entry in Directory.EnumerateFileSystemEntries( binDir, "*", SearchOption.AllDirectories ) )
			{
				try
				{
					var fi = new FileInfo( entry );
					if ( fi.LinkTarget is not null || ( fi.Attributes & FileAttributes.ReparsePoint) != 0 )
						withSymlinks.Add( entry );
					else if ( File.Exists( entry ) )
						withSymlinks.Add( entry );
				}
				catch { }
			}
			files = withSymlinks.OrderBy( p => p, StringComparer.Ordinal ).ToList();
		}
		catch { }

		var seenReal = new HashSet<string>( StringComparer.Ordinal );
		var seenCopy = new HashSet<string>( StringComparer.Ordinal );
		var consumers = new Dictionary<string, List<string>>( StringComparer.Ordinal );
		var versionErrors = new List<string>();
		var lddErrors = new List<string>();
		int checkedCount = 0, failed = 0;

		foreach ( var path in files )
		{
			string rel;
			try { rel = Path.GetRelativePath( binDir, path ); }
			catch { rel = Path.GetFileName( path ); }

			string real;
			try { real = Path.GetFullPath( path ); var fi = new FileInfo( path ); if ( fi.LinkTarget is not null ) real = Path.GetFullPath( Path.Combine( Path.GetDirectoryName( path )!, fi.LinkTarget ) ); } catch { real = path; }
			// Resolve symlink target for dedup
			try
			{
				var fi = new FileInfo( path );
				if ( fi.Exists )
				{
					// Use GetFullPath of LinkTarget resolution where possible
					var resolved = fi.ResolveLinkTarget( true );
					if ( resolved is not null ) real = resolved.FullName;
				}
			}
			catch { }

			if ( !seenReal.Add( real ) ) continue;

			if ( !File.Exists( path ) ) continue;
			try
			{
				if ( ( new FileInfo( path ).Attributes & FileAttributes.Directory) != 0 ) continue;
			}
			catch { continue; }

			// ELF magic check: 7f 45 4c 46; skip non-ELF (sh, json, etc.)
			try
			{
				using var fs = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
				var magic = new byte[4];
				if ( fs.Read( magic, 0, 4 ) < 4 ) continue;
				if ( magic[0] != 0x7f || magic[1] != (byte)'E' || magic[2] != (byte)'L' || magic[3] != (byte)'F' )
					continue;
			}
			catch { continue; }

			long size;
			try { size = new FileInfo( path ).Length; } catch { size = 0; }
			var stem = System.Text.RegularExpressions.Regex.Replace( Path.GetFileName( rel ), @"\.so(\.[0-9]+)*$", ".so" );
			var copyKey = Path.GetDirectoryName( rel ) + "|" + stem + "|" + size;
			if ( !seenCopy.Add( copyKey ) ) continue;

			string lddOut;
			try
			{
				var psi = new ProcessStartInfo { FileName = "ldd", WorkingDirectory = binDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
				psi.ArgumentList.Add( path );
				using var p = Process.Start( psi )!;
				lddOut = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
				p.WaitForExit( 5000 );
				if ( lddOut.Contains( "not a dynamic executable", StringComparison.Ordinal ) || lddOut.Contains( "statically linked", StringComparison.Ordinal ) )
					continue;
			}
			catch ( Exception e )
			{
				lddErrors.Add( rel + ": " + e.Message );
				continue;
			}

			checkedCount++;

			var missingThis = new List<string>();
			foreach ( var rawLine in lddOut.Split( '\n' ) )
			{
				var line = rawLine.Trim();
				if ( line.Contains( "=> not found", StringComparison.Ordinal ) )
				{
					var lib = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? line;
					missingThis.Add( lib );
					if ( !consumers.TryGetValue( lib, out var list ) ) consumers[lib] = list = new List<string>();
					list.Add( rel );
				}
				else if ( line.Contains( "version `", StringComparison.Ordinal ) && line.Contains( "' not found", StringComparison.Ordinal ) )
				{
					var idx = line.IndexOf( ':', StringComparison.Ordinal );
					versionErrors.Add( rel + ": " + ( idx >= 0 ? line[( idx + 1 )..].Trim() : line ) );
				}
				else if ( line.Contains( "error while loading", StringComparison.Ordinal ) || line.Contains( "cannot open shared object", StringComparison.Ordinal ) )
				{
					lddErrors.Add( rel + ": " + line );
				}
			}

			if ( missingThis.Count > 0 )
			{
				failed++;
				emit( $"  {Ansi.Red}FAIL{Ansi.Reset}  {rel.PadRight( 34 )} {Ansi.Red}{string.Join( " ", missingThis )}{Ansi.Reset}" );
			}
			else
			{
				emit( $"  {Ansi.Green}OK{Ansi.Reset}    {rel}" );
			}
		}

		if ( checkedCount == 0 )
		{
			emit( Ansi.Yellow + $"  skipped: no dynamically linked binaries found in {binDir}" + Ansi.Reset );
			return 2;
		}

		emit( "" );
		emit( $"  {checkedCount - failed} OK, {failed} with missing libraries." );

		if ( consumers.Count > 0 )
		{
			emit( "" );
			Hr( emit );
			emit( Ansi.Red + Ansi.Bold + "MISSING LIBRARIES" + Ansi.NoBold + Ansi.Reset );
			Hr( emit );
			foreach ( var lib in consumers.Keys.OrderBy( k => k, StringComparer.Ordinal ) )
			{
				var list = consumers[lib];
				emit( $"  {Ansi.Red}{lib}{Ansi.Reset}  {Ansi.Dim}needed by {list.Count}: {string.Join( " ", list )}{Ansi.Reset}" );
			}
		}

		if ( versionErrors.Count > 0 )
		{
			emit( "" );
			Hr( emit );
			emit( Ansi.Red + Ansi.Bold + "UNSATISFIABLE SYMBOL VERSIONS" + Ansi.NoBold + Ansi.Reset );
			Hr( emit );
			emit( Ansi.Dim + "  the library is present but older than the binary needs" + Ansi.Reset );
			foreach ( var line in versionErrors ) emit( $"  {Ansi.Red}{line}{Ansi.Reset}" );
		}

		if ( lddErrors.Count > 0 )
		{
			emit( "" );
			emit( Ansi.Yellow + "  loader errors:" + Ansi.Reset );
			foreach ( var line in lddErrors ) emit( "    " + line );
		}

		if ( consumers.Count == 0 && versionErrors.Count == 0 && lddErrors.Count == 0 ) return 0;
		if ( consumers.Count > 0 || versionErrors.Count > 0 ) return 1;
		return 0;
	}

	private static void Hr( Action<string> emit ) => emit( Ansi.Dim + "--------------------------------------------------------------------------" + Ansi.Reset );

	private static string? Which( string exe )
	{
		var path = Environment.GetEnvironmentVariable( "PATH" );
		if ( string.IsNullOrEmpty( path ) ) return null;
		foreach ( var dir in path.Split( ':', StringSplitOptions.RemoveEmptyEntries ) )
		{
			var cand = Path.Combine( dir, exe );
			if ( File.Exists( cand ) ) return cand;
		}
		return null;
	}
}
