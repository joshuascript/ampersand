using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ampersand;

internal enum ManagedTreeState
{
	/// <summary>No managed build in this checkout - nothing to verify.</summary>
	Absent,

	/// <summary>No usable record, so the tree's provenance is unknown.</summary>
	Unknown,

	/// <summary>Matches the last build Ampersand verified.</summary>
	Verified,

	/// <summary>Differs from the last verified build.</summary>
	Changed
}

/// <summary><see cref="ManagedTreeState"/> plus the human-readable why.</summary>
internal readonly record struct ManagedTreeCheck( ManagedTreeState State, string Detail );

/// <summary>
/// Fingerprint of <c>game/bin/managed</c> and the launcher apphosts, recorded
/// after a build Ampersand completed and checked before every launch.
/// <para>
/// The build directory is shared state: the engine's <c>BuildManaged</c> step
/// deletes and repopulates it, and every engine project also writes there
/// because its output path is pinned at <c>../../game/bin/managed</c> with no
/// configuration in the path. So a Debug <c>dotnet build</c> of a test project,
/// an interrupted build, or two builds at once can leave a tree that no single
/// compile produced. One measured instance had
/// <c>Sandbox.UI.StyleSheet.AddWatcher</c> with a <c>string</c> local declared
/// as <c>int64</c>, spliced in from a second compile of the same assembly: the
/// file looked fine, the build reported success, and the launcher died two
/// seconds into boot with <c>InvalidProgramException</c>.
/// </para>
/// <para>
/// This is the cheap half of the answer - file count, newest assembly mtime,
/// the hash of <c>Sandbox.Engine.dll</c> and the apphost stamps - because it has
/// to run before every launch and a full invalid-IL scan does not (~60s,
/// ~232k methods). It cannot prove the tree is correct; it proves nothing wrote
/// it since a build Ampersand saw finish, which is the actual failure mode.
/// </para>
/// <para>
/// The record lives next to the build lock in the checkout's <c>.git</c> (see
/// <see cref="BuildLock.DirectoryFor"/>) rather than in the ampersand cache:
/// it describes this tree, and every ampersand install pointed at the checkout
/// must agree on it.
/// </para>
/// </summary>
internal static class ManagedTree
{
	/// <summary>Bump when the shape below changes; an old record then reads as Unknown.</summary>
	private const int CurrentVersion = 1;

	/// <summary>
	/// The launchers in <c>game/</c> that Boot the managed tree. Rewritten by the
	/// same build step, and cheap to stamp.
	/// </summary>
	private static readonly string[] Apphosts = { "sbox", "sbox-dev", "sbox-launcher", "sbox-server" };

	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static string RecordPath( string repoRoot ) =>
		Path.Combine( BuildLock.DirectoryFor( repoRoot ), "sbox-managed.json" );

	/// <summary>
	/// Compares the tree with the record. Cheap: a directory listing, one hash,
	/// and a handful of stat calls.
	/// </summary>
	public static ManagedTreeCheck Check( string repoRoot )
	{
		var managed = Path.Combine( repoRoot, "game", "bin", "managed" );
		var engine = Path.Combine( managed, "Sandbox.Engine.dll" );

		if ( !File.Exists( engine ) )
			return new ManagedTreeCheck( ManagedTreeState.Absent, "no managed assemblies under " + managed );

		Snapshot current;
		try
		{
			current = Capture( repoRoot );
		}
		catch ( Exception e )
		{
			return new ManagedTreeCheck( ManagedTreeState.Unknown, "could not read the managed tree - " + e.Message );
		}

		var path = RecordPath( repoRoot );
		if ( !File.Exists( path ) )
			return new ManagedTreeCheck( ManagedTreeState.Unknown, "no build record at " + path );

		Snapshot? saved;
		try
		{
			saved = JsonSerializer.Deserialize<Snapshot>( File.ReadAllText( path ) );
		}
		catch
		{
			saved = null;
		}

		if ( saved is null || saved.Version != CurrentVersion )
			return new ManagedTreeCheck( ManagedTreeState.Unknown,
				"the build record at " + path + " is unreadable or from an older ampersand" );

		var differences = Differences( saved, current );

		return differences.Count == 0
			? new ManagedTreeCheck( ManagedTreeState.Verified, string.Empty )
			: new ManagedTreeCheck( ManagedTreeState.Changed, string.Join( "\n", differences ) );
	}

	/// <summary>
	/// Stores the current tree as verified. Called once a build completed, and
	/// when the user explicitly vouches for a tree they built another way.
	/// </summary>
	public static bool Record( string repoRoot, Action<string>? emit = null )
	{
		try
		{
			var snapshot = Capture( repoRoot );
			snapshot.RecordedAt = DateTimeOffset.Now.ToString( "O" );

			var path = RecordPath( repoRoot );
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );

			// Unique per process: two recorders can run at once (a build
			// finishing while the GUI records a tree the user vouched for), and
			// a shared .tmp would let each write the other's bytes.
			var temp = path + "." + Environment.ProcessId + ".tmp";
			File.WriteAllText( temp, JsonSerializer.Serialize( snapshot, JsonOptions ) );
			File.Move( temp, path, true );

			emit?.Invoke( "managed tree verified: " + snapshot.Summary() );
			return true;
		}
		catch ( Exception e )
		{
			emit?.Invoke( "could not record the managed tree - " + e.Message );
			return false;
		}
	}

	/// <summary>
	/// Forgets the record. Called before a build starts, so a build that is
	/// cancelled, killed, or fails leaves the tree unverified rather than
	/// looking like the last good one.
	/// </summary>
	public static void Invalidate( string repoRoot )
	{
		try { File.Delete( RecordPath( repoRoot ) ); } catch { }
	}

	private static Snapshot Capture( string repoRoot )
	{
		var managed = Path.Combine( repoRoot, "game", "bin", "managed" );
		var dlls = Directory.GetFiles( managed, "*.dll" );

		long newest = 0;
		foreach ( var dll in dlls )
			newest = Math.Max( newest, File.GetLastWriteTimeUtc( dll ).Ticks );

		var engine = Path.Combine( managed, "Sandbox.Engine.dll" );

		var snapshot = new Snapshot
		{
			Version = CurrentVersion,
			DllCount = dlls.Length,
			DllNewestTicks = newest,
			EngineSize = new FileInfo( engine ).Length,
			EngineHash = Hash( engine )
		};

		foreach ( var name in Apphosts )
		{
			var path = Path.Combine( repoRoot, "game", name );
			if ( !File.Exists( path ) )
				continue;

			var info = new FileInfo( path );
			snapshot.Apphosts[name] = new FileStamp { Size = info.Length, Ticks = info.LastWriteTimeUtc.Ticks };
		}

		return snapshot;
	}

	private static string Hash( string path )
	{
		using var stream = File.OpenRead( path );
		return Convert.ToHexString( SHA256.HashData( stream ) );
	}

	private static List<string> Differences( Snapshot saved, Snapshot current )
	{
		var differences = new List<string>();

		if ( saved.DllCount != current.DllCount )
			differences.Add( $"assembly count {saved.DllCount} -> {current.DllCount}" );

		if ( saved.EngineSize != current.EngineSize
			|| !string.Equals( saved.EngineHash, current.EngineHash, StringComparison.Ordinal ) )
			differences.Add( "Sandbox.Engine.dll differs (" + Short( saved.EngineHash ) + " -> " + Short( current.EngineHash ) + ")" );

		foreach ( var name in Apphosts )
		{
			saved.Apphosts.TryGetValue( name, out var before );
			current.Apphosts.TryGetValue( name, out var after );

			if ( before is null && after is null )
				continue;

			if ( before is null )
				differences.Add( "game/" + name + " appeared" );
			else if ( after is null )
				differences.Add( "game/" + name + " is gone" );
			else if ( before.Size != after.Size || before.Ticks != after.Ticks )
				differences.Add( "game/" + name + " was rewritten" );
		}

		// Only worth saying when nothing else moved: a rebuild changes hashes
		// too, so this is a touch, not a build.
		if ( differences.Count == 0 && saved.DllNewestTicks != current.DllNewestTicks )
			differences.Add( "the assemblies were touched without changing Sandbox.Engine.dll" );

		return differences;
	}

	private static string Short( string hash ) => hash.Length >= 8 ? hash[..8].ToLowerInvariant() : hash;

	/// <summary>What is recorded; kept flat so the file stays readable by hand.</summary>
	private sealed class Snapshot
	{
		public int Version { get; set; }
		public int DllCount { get; set; }
		public long DllNewestTicks { get; set; }
		public long EngineSize { get; set; }
		public string EngineHash { get; set; } = "";
		public Dictionary<string, FileStamp> Apphosts { get; set; } = new();
		public string RecordedAt { get; set; } = "";

		public string Summary() =>
			$"{DllCount} assemblies, Sandbox.Engine.dll {Short( EngineHash )}, {RecordedAt}";
	}

	private sealed class FileStamp
	{
		public long Size { get; set; }
		public long Ticks { get; set; }
	}
}
