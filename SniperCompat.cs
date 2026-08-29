using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Ampersand;

/// <summary>
/// Sniper does not ship four libraries .NET 10 needs, so they are copied out of
/// the host once and cached:
///
///   libunwind.so.8, libunwind-x86_64.so.8   libcoreclr, libclrjit,
///       libmscordaccore and libmscordbi all DT_NEEDED them. Without these the
///       runtime dies with "Failed to create CoreCLR, HRESULT: 0x80008088".
///
///   libcrypto.so.3, libssl.so.3             sniper is OpenSSL 1.1. Without
///       these Bootstrap.Init throws a TypeInitializationException out of
///       Interop.Crypto, by way of LiteDB asking for SHA1.
///
/// The cache is handed to scripts as $SBOX_SNIPER_COMPAT. It is appended to
/// LD_LIBRARY_PATH inside the container by _common.sh, because LD_LIBRARY_PATH
/// set outside is discarded by pressure-vessel.
/// </summary>
internal static class SniperCompat
{
	public static readonly string[] RequiredLibraries =
	{
		"libunwind.so.8",
		"libunwind-x86_64.so.8",
		"libcrypto.so.3",
		"libssl.so.3"
	};

	public static string CacheDirectory
	{
		get
		{
			var cacheHome = Environment.GetEnvironmentVariable( "XDG_CACHE_HOME" );

			if ( string.IsNullOrEmpty( cacheHome ) )
			{
				cacheHome = Path.Combine(
					Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".cache" );
			}

			return Path.Combine( cacheHome, "sbox-ampersand", "sniper-compat" );
		}
	}

	/// <summary>
	/// Populates the cache from the host. Returns false and fills
	/// <paramref name="problems"/> when a library cannot be found anywhere,
	/// naming the package that provides it on this distro.
	/// </summary>
	public static bool Ensure( out List<string> problems )
	{
		problems = new List<string>();

		var cache = CacheDirectory;

		try
		{
			Directory.CreateDirectory( cache );
		}
		catch ( Exception e )
		{
			problems.Add( "could not create " + cache + " - " + e.Message );
			return false;
		}

		var missing = new List<string>();

		foreach ( var library in RequiredLibraries )
		{
			var destination = Path.Combine( cache, library );

			if ( File.Exists( destination ) )
				continue;

			var source = FindHostLibrary( library );

			if ( source is null )
			{
				missing.Add( library );
				continue;
			}

			try
			{
				// Copy rather than symlink: a link would point at a host path
				// that does not exist inside the container.
				File.Copy( source, destination, true );
			}
			catch ( Exception e )
			{
				problems.Add( "could not cache " + library + " - " + e.Message );
				return false;
			}
		}

		if ( missing.Count == 0 )
			return true;

		problems.Add( "sniper needs these libraries and this host does not have them:" );

		foreach ( var library in missing )
			problems.Add( "    " + library + "   install " + PackageFor( library ) );

		return false;
	}

	/// <summary>
	/// ldconfig knows where the loader would find a library on any distro,
	/// which beats guessing between /usr/lib/x86_64-linux-gnu, /usr/lib64 and
	/// /usr/lib. The directory sweep is a fallback for when it is unavailable.
	/// </summary>
	private static string? FindHostLibrary( string soname )
	{
		foreach ( var line in RunLdconfig() )
		{
			var trimmed = line.Trim();

			if ( !trimmed.StartsWith( soname + " ", StringComparison.Ordinal ) )
				continue;

			// libunwind.so.8 (libc6,x86-64) => /usr/lib/x86_64-linux-gnu/libunwind.so.8
			if ( !trimmed.Contains( "x86-64", StringComparison.Ordinal ) )
				continue;

			var arrow = trimmed.IndexOf( "=>", StringComparison.Ordinal );
			if ( arrow < 0 )
				continue;

			var path = trimmed[( arrow + 2 )..].Trim();

			if ( File.Exists( path ) )
				return path;
		}

		foreach ( var directory in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/lib/x86_64-linux-gnu", "/usr/lib" } )
		{
			var candidate = Path.Combine( directory, soname );

			if ( File.Exists( candidate ) )
				return candidate;
		}

		return null;
	}

	private static IEnumerable<string> RunLdconfig()
	{
		string output;

		try
		{
			using var process = Process.Start( new ProcessStartInfo
			{
				FileName = "ldconfig",
				ArgumentList = { "-p" },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			} );

			if ( process is null )
				return Array.Empty<string>();

			output = process.StandardOutput.ReadToEnd();
			process.WaitForExit( 15000 );
		}
		catch
		{
			return Array.Empty<string>();
		}

		return output.Split( '\n' );
	}

	private static string PackageFor( string soname )
	{
		var family = DistroFamily();
		var openssl = soname.StartsWith( "libssl", StringComparison.Ordinal )
			|| soname.StartsWith( "libcrypto", StringComparison.Ordinal );

		return family switch
		{
			"fedora" => openssl ? "openssl-libs" : "libunwind",
			"arch" => openssl ? "openssl" : "libunwind",
			"suse" => openssl ? "libopenssl3" : "libunwind8",
			_ => openssl ? "libssl3 (or openssl)" : "libunwind8"
		};
	}

	private static string DistroFamily()
	{
		try
		{
			foreach ( var line in File.ReadAllLines( "/etc/os-release" ) )
			{
				if ( !line.StartsWith( "ID=", StringComparison.Ordinal )
					&& !line.StartsWith( "ID_LIKE=", StringComparison.Ordinal ) )
				{
					continue;
				}

				var value = line[( line.IndexOf( '=' ) + 1 )..].Trim( '"', ' ' );

				if ( value.Contains( "fedora" ) || value.Contains( "rhel" ) )
					return "fedora";

				if ( value.Contains( "arch" ) )
					return "arch";

				if ( value.Contains( "suse" ) )
					return "suse";
			}
		}
		catch
		{
			// fall through
		}

		return "debian";
	}
}
