using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ampersand;

/// <summary>
/// Ampersand port of sbox-public/bootstrap.sh.
/// <para>
/// Fetches prebuilt natives, validates their shared-library dependencies on the
/// host, then drives SboxBuild through build / build-shaders / build-content -
/// the same sequence the shell script does, but as a first-class Tool that can be
/// launched from the sidebar in a real terminal (Program.BootstrapArgument) with
/// SGR colour and proper logging.
/// </para>
/// <para>
/// Order matters: natives are fetched before the ldd sweep so the check sees what
/// the build is about to use, exactly as bootstrap.sh does (Steps/Build.cs:19 and
/// bootstrap.sh:12-16). build-shaders and build-content are best-effort on Linux
/// and only warn on failure.
/// </para>
/// </summary>
internal static class Bootstrap
{
	public static void Run( string repoRoot, Action<string> emit, bool skipDeps = false )
	{
		emit( Ansi.Bold + Ansi.White + "=== bootstrap ===" + Ansi.NoBold + Ansi.Reset );
		emit( Ansi.Dim + "repo  " + Ansi.Reset + repoRoot );
		emit( "" );

		var sboxBuildProj = Path.Combine( repoRoot, "engine", "Tools", "SboxBuild", "SboxBuild.csproj" );
		if ( !File.Exists( sboxBuildProj ) )
		{
			emit( Ansi.Red + "  SboxBuild not found: " + sboxBuildProj + Ansi.Reset );
			emit( Ansi.Dim + "  Is this a valid s&box checkout (game/ + engine/)?" + Ansi.Reset );
			return;
		}

		if ( Which( "dotnet" ) is null )
		{
			emit( Ansi.Red + "  dotnet not on PATH - cannot run SboxBuild." + Ansi.Reset );
			emit( Ansi.Dim + "  Install .NET 10 SDK: https://dotnet.microsoft.com/download" + Ansi.Reset );
			return;
		}

		int nativeDepsResult = 0;

		if ( !skipDeps )
		{
			// --- fetch natives ------------------------------------------------
			emit( Ansi.Bold + Ansi.Cyan + "--- fetching native binaries ---" + Ansi.NoBold + Ansi.Reset );
			var fetchCode = RunSboxBuild( repoRoot, sboxBuildProj, new[] { "download-public-artifacts", "--native-only" }, emit );
			if ( fetchCode != 0 )
			{
				emit( Ansi.Yellow + "  warning: download failed - checking whatever is already on disk" + Ansi.Reset );
			}
			emit( "" );

			// --- check native deps --------------------------------------------
			emit( Ansi.Bold + Ansi.Cyan + "--- checking native dependencies in game/bin/linuxsteamrt64 ---" + Ansi.NoBold + Ansi.Reset );
			nativeDepsResult = CheckNativeDeps( repoRoot, emit );

			if ( nativeDepsResult == 1 )
			{
				emit( "" );
				emit( Ansi.Yellow + "  These are prebuilt binaries that cannot be rebuilt here, so the managed build" + Ansi.Reset );
				emit( Ansi.Yellow + "  below will still succeed - but the editor will not run until they resolve." + Ansi.Reset );
				emit( Ansi.Dim + "  Continuing anyway (non-interactive / -y)." + Ansi.Reset );
			}
			emit( "" );
		}
		else
		{
			emit( Ansi.Dim + "  --skip-deps: not fetching or checking natives" + Ansi.Reset );
			emit( "" );
		}

		// --- build ------------------------------------------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- sboxbuild build --config Developer ---" + Ansi.NoBold + Ansi.Reset );
		var buildCode = RunSboxBuild( repoRoot, sboxBuildProj, new[] { "build", "--config", "Developer" }, emit );
		emit( "" );

		if ( buildCode != 0 )
		{
			emit( Ansi.Red + $"  build failed (exit {buildCode})" + Ansi.Reset );
			emit( "" );
			emit( Ansi.Dim + "Native dependency issues above do not cause this - managed build failures are separate." + Ansi.Reset );
			return;
		}

		emit( Ansi.Green + "  build OK" + Ansi.Reset );
		emit( "" );

		// --- build-shaders (best-effort) -------------------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- sboxbuild build-shaders ---" + Ansi.NoBold + Ansi.Reset );
		var shadersCode = RunSboxBuild( repoRoot, sboxBuildProj, new[] { "build-shaders" }, emit );
		if ( shadersCode != 0 )
			emit( Ansi.Yellow + "  warning: build-shaders failed (not supported on Linux yet), continuing" + Ansi.Reset );
		else
			emit( Ansi.Green + "  build-shaders OK" + Ansi.Reset );
		emit( "" );

		// --- build-content (best-effort) -------------------------------------
		emit( Ansi.Bold + Ansi.Cyan + "--- sboxbuild build-content ---" + Ansi.NoBold + Ansi.Reset );
		var contentCode = RunSboxBuild( repoRoot, sboxBuildProj, new[] { "build-content" }, emit );
		if ( contentCode != 0 )
			emit( Ansi.Yellow + "  warning: build-content failed (not supported on Linux yet), continuing" + Ansi.Reset );
		else
			emit( Ansi.Green + "  build-content OK" + Ansi.Reset );
		emit( "" );

		emit( Ansi.Bold + Ansi.White + "=== bootstrap done ===" + Ansi.NoBold + Ansi.Reset );
		if ( nativeDepsResult == 1 )
			emit( Ansi.Yellow + "  (with missing native libraries - editor will not run until they resolve)" + Ansi.Reset );
	}

	private static int RunSboxBuild( string repoRoot, string proj, string[] sboxArgs, Action<string> emit )
	{
		var argv = new List<string> { "run", "--project", proj, "--" };
		argv.AddRange( sboxArgs );
		return RunProcess( "dotnet", argv, repoRoot, emit );
	}

	private static int RunProcess( string exe, IReadOnlyList<string> args, string workDir, Action<string> emit )
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

	// ---- native dependency check (port of bootstrap.sh check_native_deps) ----

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
			emit( Ansi.Yellow + $"  skipped: {binDir} does not exist - the fetch above did not produce it" + Ansi.Reset );
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
