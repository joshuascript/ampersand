using System;
using System.IO;
using System.Text.Json;

namespace Ampersand;

internal sealed record SboxConfig( string SboxRoot );

internal static class SboxSettings
{
	public static string ConfigDirectory
	{
		get
		{
			var dataHome = Environment.GetEnvironmentVariable( "XDG_DATA_HOME" );
			if ( string.IsNullOrEmpty( dataHome ) )
				dataHome = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".local", "share" );
			return Path.Combine( dataHome, "sbox-ampersand" );
		}
	}

	public static string ConfigPath => Path.Combine( ConfigDirectory, "settings.json" );

	/// <summary>
	/// True when path contains game/ and engine/ and game/sbox (matches _common.sh expectation).
	/// </summary>
	public static bool IsValid( string? root )
	{
		if ( string.IsNullOrWhiteSpace( root ) ) return false;
		try
		{
			return Directory.Exists( Path.Combine( root, "game" ) )
				&& Directory.Exists( Path.Combine( root, "engine" ) )
				&& File.Exists( Path.Combine( root, "game", "sbox" ) );
		}
		catch { return false; }
	}

	/// <summary>
	/// Accepts repo root, game/, or game/sbox and normalizes to repo root.
	/// Handles ~, quoted strings, env expansion.
	/// Returns null if cannot be resolved.
	/// </summary>
	public static string? Normalize( string? input )
	{
		if ( string.IsNullOrWhiteSpace( input ) ) return null;

		var s = input.Trim();

		// Strip surrounding quotes
		if ( ( s.StartsWith( "\"" ) && s.EndsWith( "\"" ) ) || ( s.StartsWith( "'" ) && s.EndsWith( "'" ) ) )
			s = s[1..^1].Trim();

		// Expand ~ and env vars
		if ( s.StartsWith( "~" ) )
		{
			var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
			if ( s == "~" ) s = home;
			else if ( s.StartsWith( "~/", StringComparison.Ordinal ) || s.StartsWith( "~\\", StringComparison.Ordinal ) )
				s = Path.Combine( home, s[2..] );
		}

		try { s = Environment.ExpandEnvironmentVariables( s ); } catch { }

		string? candidateDir;

		try
		{
			if ( File.Exists( s ) )
			{
				// If they picked a file (e.g. game/sbox), use its directory.
				candidateDir = Path.GetDirectoryName( Path.GetFullPath( s ) );
				if ( candidateDir is null ) return null;
			}
			else
			{
				candidateDir = Path.GetFullPath( s );
			}
		}
		catch { return null; }

		if ( candidateDir is null ) return null;

		// Walk up at most 3 levels looking for a valid root (covers repo root, game/, game/bin/...).
		var dir = new DirectoryInfo( candidateDir );

		for ( int i = 0; i < 4 && dir is not null; i++ )
		{
			var p = dir.FullName;

			if ( IsValid( p ) )
				return p;

			// If we are inside game/ but not at root, parent might be root.
			// Also if we are at .../game we would have just tested parent anyway on next iteration.
			dir = dir.Parent;
		}

		// No valid root found, return the original directory normalized (for error display).
		// But for validation failure we still return this so caller can show it.
		try { return Path.GetFullPath( candidateDir ); } catch { return candidateDir; }
	}

	public static SboxConfig? Load()
	{
		try
		{
			if ( !File.Exists( ConfigPath ) ) return null;
			var text = File.ReadAllText( ConfigPath );
			if ( string.IsNullOrWhiteSpace( text ) ) return null;

			using var doc = JsonDocument.Parse( text );
			if ( !doc.RootElement.TryGetProperty( "sboxRoot", out var el ) && !doc.RootElement.TryGetProperty( "SboxRoot", out el ) )
				return null;

			var raw = el.GetString();
			if ( string.IsNullOrWhiteSpace( raw ) ) return null;

			var norm = Normalize( raw );
			if ( norm is null ) return new SboxConfig( raw );
			return new SboxConfig( norm );
		}
		catch { return null; }
	}

	public static void Save( string sboxRoot )
	{
		var norm = Normalize( sboxRoot ) ?? sboxRoot;
		var dir = ConfigDirectory;
		Directory.CreateDirectory( dir );

		var payload = new { sboxRoot = norm };
		var json = JsonSerializer.Serialize( payload, new JsonSerializerOptions { WriteIndented = true } );

		var tmp = ConfigPath + ".tmp";
		File.WriteAllText( tmp, json );
		try { File.Move( tmp, ConfigPath, overwrite: true ); }
		catch
		{
			// Fallback: direct write if move fails cross-device
			File.WriteAllText( ConfigPath, json );
			try { File.Delete( tmp ); } catch { }
		}
	}

	/// <summary>
	/// Resolve the s&box root to use: persisted valid -> migration from RepoRoot.Find -> null.
	/// Saves migration result so next start doesn't need to walk.
	/// </summary>
	public static string? Resolve()
	{
		var loaded = Load();
		if ( loaded is not null && IsValid( loaded.SboxRoot ) )
			return loaded.SboxRoot;

		var detected = RepoRoot.Find();
		if ( detected is not null && IsValid( detected ) )
		{
			try { Save( detected ); } catch { }
			return detected;
		}

		// If persisted path exists but is now invalid, still return it for display/stale handling,
		// but Resolve returns null to trigger prompt. Caller can use Load() to get stale path.
		return null;
	}

	public static string? GetStalePersistedPath()
	{
		var loaded = Load();
		return loaded?.SboxRoot;
	}

	public static string ShortenForDisplay( string path, int maxLen = 48 )
	{
		try
		{
			var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
			if ( !string.IsNullOrEmpty( home ) && path.StartsWith( home, StringComparison.Ordinal ) )
				path = "~" + path[home.Length..];
		}
		catch { }

		if ( path.Length <= maxLen ) return path;
		// Middle ellipsis
		var keep = maxLen - 3;
		var head = keep / 2;
		var tail = keep - head;
		return path[..head] + "..." + path[^tail..];
	}
}
