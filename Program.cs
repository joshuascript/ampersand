using System;
using Avalonia;

namespace Ampersand;

internal static class Program
{
	[STAThread]
	private static int Main( string[] args )
	{
		return BuildAvaloniaApp().StartWithClassicDesktopLifetime( args );
	}

	private static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.LogToTrace();
	}
}
