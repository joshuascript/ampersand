using System;
using System.IO;

namespace Ampersand;

internal static class RepoRoot
{
	/// <summary>
	/// The published launcher sits at the repo root, but during development it
	/// runs from ampersand/bin/..., so walk up until the tree looks right.
	/// </summary>
	public static string? Find()
	{
		var dir = new DirectoryInfo( AppContext.BaseDirectory );

		while ( dir is not null )
		{
			if ( Directory.Exists( Path.Combine( dir.FullName, "game" ) )
				&& Directory.Exists( Path.Combine( dir.FullName, "engine" ) ) )
			{
				return dir.FullName;
			}

			dir = dir.Parent;
		}

		return null;
	}
}
