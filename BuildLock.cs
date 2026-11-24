using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ampersand;

/// <summary>
/// Exclusive lock around a build of the s&amp;box checkout, held for the whole
/// Setup.sh run.
/// <para>
/// The engine's own build deletes and repopulates <c>game/bin/managed</c> in one
/// step (<c>engine/Tools/SboxBuild/Steps/BuildManaged.cs</c>), while every
/// engine project pins its output there with no configuration in the path
/// (<c>&lt;OutputPath&gt;../../game/bin/managed&lt;/OutputPath&gt;</c>). A second
/// build - any <c>dotnet build</c> of an engine or test project, including a
/// Debug one - therefore writes the same files, and two copies of
/// <c>Sandbox.Engine.dll</c> landing in the same file can splice per-method
/// signature blobs from one compile with code from the other. The result is a
/// tree where exactly one method has invalid IL: it passes every file check and
/// only appears as <c>InvalidProgramException</c> when the launcher happens to
/// JIT that method, well after the build.
/// </para>
/// <para>
/// This lock cannot stop a build Ampersand does not drive, but it removes the
/// two-writers case Ampersand can cause (two Build S&amp;Box clicks, a build
/// started from another terminal while the GUI one runs). Whether a foreign
/// build happened is what <see cref="ManagedTree"/> answers afterwards.
/// </para>
/// <para>
/// Advisory (BSD) lock via <c>flock(2)</c>, not <see cref="FileShare.None"/>:
/// the loose <c>apps/build.sh</c> takes the same lock with <c>flock(1)</c>, so
/// both writers must use the same mechanism. The fd is close-on-exec, so the
/// lock belongs to the process that took it and nothing else: the kernel drops
/// it when that process dies, and the build's children cannot pin it after the
/// build. That last part is not theoretical - the engine builds through
/// <c>dotnet</c>, whose MSBuild worker nodes (<c>/nodeReuse:true</c>) outlive
/// the build for about fifteen minutes, and a lock they inherited reads as
/// "another build is running" long after the last one finished.
/// </para>
/// </summary>
internal sealed class BuildLock : IDisposable
{
	// flock(2) operations; LOCK_EX is 2 and LOCK_NB 4 on every Linux ABI.
	private const int LockExclusive = 2;
	private const int LockNonBlocking = 4;

	// open(2) flags and the creation mode, likewise stable across Linux
	// architectures. O_CLOEXEC keeps the lock out of the build's descendants;
	// see the class remarks for why inheriting it is worse than the case it
	// would cover (a launcher killed alone while Setup.sh keeps running - the
	// managed-tree record is already invalidated then, so the next launch is
	// gated and the next build is the recovery).
	private const int OpenReadWrite = 0x2;
	private const int OpenCreate = 0x40;
	private const int OpenCloseOnExec = 0x80000;   // O_CLOEXEC
	private const int Mode0666 = 0x1B6;   // still subject to umask
	private const int WouldBlock = 11;    // EAGAIN/EWOULDBLOCK

	private int handle;

	private BuildLock( int handle ) => this.handle = handle;

	/// <summary>
	/// The directory the lock record and the managed-tree fingerprint live in:
	/// <c>.git</c> when it is a real directory (inside a checkout, never shows
	/// up in <c>git status</c> and survives <c>git clean</c>), otherwise the
	/// repo root - a worktree's <c>.git</c> is a file, and a repo without git
	/// still needs somewhere stable. apps/build.sh resolves the same path.
	/// </summary>
	public static string DirectoryFor( string repoRoot )
	{
		var git = Path.Combine( repoRoot, ".git" );
		return Directory.Exists( git ) ? git : repoRoot;
	}

	/// <summary>
	/// <c>AMPERSAND_BUILD_LOCK</c> overrides the path for tests and for pinned
	/// CI checkouts; apps/build.sh honours the same variable.
	/// </summary>
	public static string PathFor( string repoRoot )
	{
		var custom = Environment.GetEnvironmentVariable( "AMPERSAND_BUILD_LOCK" );
		return string.IsNullOrEmpty( custom )
			? Path.Combine( DirectoryFor( repoRoot ), "sbox-build.lock" )
			: custom;
	}

	/// <summary>
	/// Takes the lock, or returns null and says why. Never waits: a second
	/// build started behind a running one would only be a second writer on
	/// <c>game/bin/managed</c>, and its owner is looking at a terminal that can
	/// explain the situation better than a silent multi-minute queue.
	/// <paramref name="busy"/> separates "another build has it" from "the lock
	/// file itself cannot be used", which need different advice.
	/// </summary>
	public static BuildLock? TryAcquire( string repoRoot, out string problem, out bool busy )
	{
		problem = string.Empty;
		busy = false;

		// flock is a Linux call; everywhere else the build does not run at all.
		if ( !OperatingSystem.IsLinux() )
			return new BuildLock( -1 );

		var path = PathFor( repoRoot );

		// Raw open(2) rather than FileStream: .NET's Unix FileStream emulates
		// Windows sharing with flock(2) itself and refuses to even open a file
		// another process holds, which would report a perfectly good lock as an
		// unreadable path.
		var fd = open( path, OpenReadWrite | OpenCreate | OpenCloseOnExec, Mode0666 );

		if ( fd < 0 )
		{
			var errno = Marshal.GetLastPInvokeError();
			problem = "cannot open " + path + " - " + Marshal.GetPInvokeErrorMessage( errno );
			return null;
		}

		if ( flock( fd, LockExclusive | LockNonBlocking ) != 0 )
		{
			var errno = Marshal.GetLastPInvokeError();
			close( fd );

			busy = errno == WouldBlock;

			problem = busy
				? "another build holds " + path
				: "cannot lock " + path + " - " + Marshal.GetPInvokeErrorMessage( errno );

			return null;
		}

		return new BuildLock( fd );
	}

	public void Dispose()
	{
		if ( handle < 0 )
			return;

		close( handle );
		handle = -1;
	}

	[DllImport( "libc", SetLastError = true )]
	private static extern int flock( int fd, int operation );

	[DllImport( "libc", SetLastError = true, CharSet = CharSet.Ansi )]
	private static extern int open( string path, int flags, int mode );

	[DllImport( "libc", SetLastError = true )]
	private static extern int close( int fd );
}
