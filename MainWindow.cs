using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace Ampersand;

internal sealed class MainWindow : Window
{
	private readonly AvaloniaList<LaunchTarget> targets = new();

	private readonly ListBox targetList;
	private readonly TextBlock statusText;
	private readonly Button stopButton;
	private readonly Button depCheckButton;
	private readonly CheckBox steamRuntime;
	private readonly CheckBox systemTerminal;

	private LaunchTarget? selected;
	private bool updatingCheckbox;

	public MainWindow()
	{
		Title = "s&box launcher";
		Width = 1040;
		Height = 660;
		RequestedThemeVariant = ThemeVariant.Dark;
		Background = TerminalTheme.Background;

		foreach ( var (name, script) in new[]
		{
			( "sbox", "sbox.sh" ),
			( "sbox-dev", "sbox-dev.sh" ),
			( "sbox-server", "sbox-server.sh" )
		} )
		{
			targets.Add( new LaunchTarget( name, script ) );
		}

		LoadMetadata();

		targetList = new ListBox
		{
			ItemsSource = targets,
			Background = Brushes.Transparent,

			// A LIST, not a grid of cells. Removing the output pane hands this
			// panel the whole height, and the obvious move was to divide it
			// between the targets - but a launch target is a row you pick, and
			// blowing it up into a full-height button makes three scripts look
			// like the three modes of the application. The rows stay their own
			// size and the panel is simply taller.
			ItemTemplate = new FuncDataTemplate<LaunchTarget>( ( _, _ ) =>
			{
				var text = new TextBlock { Margin = new Thickness( 6, 4 ) };
				text.Bind( TextBlock.TextProperty, new Binding( nameof( LaunchTarget.Display ) ) );
				return text;
			}, supportsRecycling: true )
		};

		// Single click SELECTS, double click LAUNCHES. They have to be separate
		// gestures: the toggles are enabled per selection, so if one click did
		// both there would be no moment in which you could set Steam Runtime or
		// the terminal option for the run you are about to start.
		targetList.SelectionChanged += ( _, _ ) => ShowSelected();
		targetList.Tapped += ( _, e ) =>
		{
			if ( IsOnRow( e.Source ) )
				ShowSelected();
		};
		targetList.DoubleTapped += ( _, e ) =>
		{
			// Tapped covers the whole ListBox, empty space included.
			if ( !IsOnRow( e.Source ) )
				return;

			ShowSelected();

			if ( selected is { Runner.IsRunning: false, Preparing: false } target )
				Launch( target );
		};

		steamRuntime = new CheckBox
		{
			Content = "Launch in Steam Runtime",
			IsEnabled = false,
			VerticalAlignment = VerticalAlignment.Center
		};
		steamRuntime.IsCheckedChanged += ( _, _ ) =>
		{
			if ( !updatingCheckbox && selected is not null )
				selected.UseSniper = steamRuntime.IsChecked == true;
		};

		// Decided BEFORE launching, because the emulator has to wrap the command
		// at spawn time - there is nothing to attach to a window opened later.
		systemTerminal = new CheckBox
		{
			Content = "Launch with system terminal",
			IsEnabled = false,
			VerticalAlignment = VerticalAlignment.Center
		};
		systemTerminal.IsCheckedChanged += ( _, _ ) =>
		{
			if ( !updatingCheckbox && selected is not null )
				selected.UseSystemTerminal = systemTerminal.IsChecked == true;
		};

		var toggles = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 16,
			VerticalAlignment = VerticalAlignment.Center
		};
		toggles.Children.Add( systemTerminal );
		toggles.Children.Add( steamRuntime );

		statusText = new TextBlock
		{
			Text = "double-click an app to launch it",
			Foreground = TerminalTheme.Normal,
			FontSize = 12,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness( 10, 0 ),
			TextTrimming = TextTrimming.CharacterEllipsis
		};

		stopButton = new Button { Content = "Stop", IsEnabled = false, Margin = new Thickness( 8, 6, 10, 6 ) };
		stopButton.Click += ( _, _ ) => selected?.Runner.Stop();

		// One bar, not the old header-plus-strip: with no pane between them there
		// is nothing for two separate surfaces to separate.
		var bar = new Grid { ColumnDefinitions = new ColumnDefinitions( "*,Auto,Auto" ) };
		bar.Children.Add( statusText );
		Grid.SetColumn( toggles, 1 );
		bar.Children.Add( toggles );
		Grid.SetColumn( stopButton, 2 );
		bar.Children.Add( stopButton );

		var right = new Grid { RowDefinitions = new RowDefinitions( "*,Auto" ) };
		var targetPanel = Surface( targetList, TerminalTheme.TargetPanel, new Thickness( 0, 0, 0, 1 ) );
		var barPanel = Surface( bar, TerminalTheme.ToolbarPanel, new Thickness( 0 ) );

		right.Children.Add( targetPanel );
		Grid.SetRow( barPanel, 1 );
		right.Children.Add( barPanel );

		depCheckButton = SidebarButton( "Check for missing dependencies" );
		depCheckButton.Click += ( _, _ ) => RunDependencyCheck();

		var actions = new StackPanel();
		actions.Children.Add( depCheckButton );

		var actionScroll = new ScrollViewer
		{
			Content = actions,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};

		var left = new Grid { RowDefinitions = new RowDefinitions( "Auto,*,Auto" ) };
		left.Children.Add( SidebarHeader( "SHELL SCRIPTS" ) );
		Grid.SetRow( actionScroll, 1 );
		left.Children.Add( actionScroll );

		var footer = SidebarFooter( "SHARE SCRIPTS AT YOUR OWN RISK" );
		Grid.SetRow( footer, 2 );
		left.Children.Add( footer );

		var root = new Grid { ColumnDefinitions = new ColumnDefinitions( "25*,75*" ) };
		root.Children.Add( Surface( left, TerminalTheme.SidebarPanel, new Thickness( 0, 0, 1, 0 ) ) );
		Grid.SetColumn( right, 1 );
		root.Children.Add( right );

		Content = root;

		foreach ( var target in targets )
		{
			var captured = target;

			captured.Runner.StateChanged += _ =>
			{
				captured.NotifyStatusChanged();

				if ( ReferenceEquals( captured, selected ) )
					UpdateStatusBar();
			};

			// The runner's own lines used to be written into that target's pane.
			// There is no pane, so they land on the status bar - and only while
			// that target is the selected one, since the bar describes the
			// selection and a background target overwriting it would be a lie.
			captured.Runner.Notice += line =>
			{
				if ( ReferenceEquals( captured, selected ) )
					statusText.Text = captured.Name + ": " + line;
			};
		}

		if ( SystemTerminal.Detect() is null )
			statusText.Text = "no terminal emulator found - launches will run with their output discarded";
	}

	/// <summary>True when a pointer event landed on a row rather than the empty
	/// space below the list.</summary>
	private static bool IsOnRow( object? source )
	{
		return source is Visual visual && visual.FindAncestorOfType<ListBoxItem>( true ) is not null;
	}

	private void ShowSelected()
	{
		if ( targetList.SelectedItem is not LaunchTarget target )
			return;

		if ( ReferenceEquals( target, selected ) )
			return;

		selected = target;
		UpdateStatusBar();
	}

	private void UpdateToggles( LaunchTarget target )
	{
		updatingCheckbox = true;

		switch ( target.Metadata.Sniper )
		{
			case SniperMode.Never:
				steamRuntime.IsChecked = false;
				steamRuntime.IsEnabled = false;
				break;

			case SniperMode.Always:
				steamRuntime.IsChecked = true;
				steamRuntime.IsEnabled = false;
				break;

			default:
				steamRuntime.IsChecked = target.UseSniper;
				steamRuntime.IsEnabled = true;
				break;
		}

		// Cannot be changed once running: the choice is baked into the command
		// that was already spawned.
		systemTerminal.IsChecked = target.UseSystemTerminal;
		systemTerminal.IsEnabled = SystemTerminal.Detect() is not null
			&& !target.Runner.IsRunning && !target.Preparing;

		updatingCheckbox = false;
	}

	/// <summary>
	/// Guard around the launch path, and nothing else.
	///
	/// This is reached from a double-click handler, so it is async void: nothing
	/// observes the task, and an exception escaping it is unhandled and takes the
	/// window down. A tool for diagnosing a broken engine must not be killed by
	/// the engine being broken. The prepare path is the live risk - it shells
	/// out, copies megabytes, and waits on Steam.
	/// </summary>
	private async void Launch( LaunchTarget target )
	{
		try
		{
			await LaunchCore( target );
		}
		catch ( Exception e )
		{
			// Preparing is already cleared by LaunchCore's finally, whichever
			// way it left.
			await Fail( target, "Launch failed", e.Message );
		}
	}

	private async Task LaunchCore( LaunchTarget target )
	{
		var root = RepoRoot.Find();
		if ( root is null )
		{
			await Fail( target, "Repo root not found",
				"ampersand could not locate the s&box tree - it expects game/ and engine/ "
					+ "somewhere above this binary." );
			return;
		}

		var script = Path.Combine( root, "ampersand", "apps", target.ScriptFile );
		if ( !File.Exists( script ) )
		{
			await Fail( target, "Launch script missing", "Not found:\n\n" + script );
			return;
		}

		var env = new Dictionary<string, string> { ["SBOX_REPO_ROOT"] = root };
		var command = new List<string>();

		// Held across the await below, not just the spawn: PrepareSniper can sit
		// for minutes waiting on Steam, and nothing else marks the target busy.
		target.Preparing = true;
		target.NotifyStatusChanged();
		UpdateStatusBar();

		try
		{
			if ( target.UseSniper )
			{
				if ( !await PrepareSniper( target, root, script, command, env ) )
					return;
			}
			else
			{
				command.Add( script );
			}
		}
		finally
		{
			target.Preparing = false;
		}

		try
		{
			target.Runner.Start( command, root, env, target.UseSystemTerminal );
		}
		catch ( Exception e )
		{
			await Fail( target, "Launch failed", e.Message );
			return;
		}

		target.NotifyStatusChanged();
		UpdateStatusBar();
	}

	/// <summary>
	/// Builds the containerised command, and refuses to build one at all unless
	/// Steam is up.
	///
	/// Steam running is a hard requirement, not a preference: the container is
	/// entered through the launcher service running alongside the client, because
	/// that is the only context in which the runtime's own bwrap is allowed to
	/// create a user namespace. See SteamLauncherService.
	/// </summary>
	private async Task<bool> PrepareSniper(
		LaunchTarget target,
		string root,
		string script,
		List<string> command,
		Dictionary<string, string> env )
	{
		var install = SniperRuntime.Find();

		if ( install is null )
		{
			await Fail( target, "Steam Linux Runtime not installed",
				"Steam Linux Runtime 3.0 (sniper) is not installed.\n\n"
					+ "Install it from Steam:  steam steam://install/" + SniperRuntime.SteamAppId );
			return false;
		}

		statusText.Text = target.Name + ": runtime " + install.Version;

		// Everything below shells out or copies megabytes, so none of it may run
		// on the UI thread - the probe alone can sit five seconds. This is the
		// reason the method is async; awaiting a blocking call would only move
		// the freeze, not remove it.
		if ( !await Task.Run( () => SteamLauncherService.IsAvailable( install ) ) )
		{
			await Fail( target, "Steam is not running",
				"s&box can only be launched in the Steam runtime while the Steam client is "
					+ "running - the container is entered through Steam's own launcher service.\n\n"
					+ "Start Steam, wait for it to sign in, then try again." );
			return false;
		}

		var requirements = await Task.Run( () =>
		{
			var met = SniperRuntime.CheckRequirements( install, out var found );
			return (Met: met, Problems: found);
		} );

		if ( !requirements.Met )
		{
			await Fail( target, "Container cannot start",
				"This host cannot start a pressure-vessel container.\n\n"
					+ string.Join( "\n", requirements.Problems ) );
			return false;
		}

		var compat = await Task.Run( () =>
		{
			var ready = SniperCompat.Ensure( out var found );
			return (Ready: ready, Problems: found);
		} );

		if ( !compat.Ready )
		{
			await Fail( target, "Compat libraries unavailable", string.Join( "\n", compat.Problems ) );
			return false;
		}

		var cache = SniperCompat.CacheDirectory;

		env["SBOX_IN_SNIPER"] = "1";
		env["SBOX_SNIPER_COMPAT"] = cache;

		// TERM is added here rather than left to the emulator: this one is set on
		// launch-client's own environment, and the child does not inherit it. It
		// is handed over unconditionally - in background mode there is no tty, so
		// the engine suppresses colour regardless of what TERM says.
		var passed = new Dictionary<string, string>( env ) { ["TERM"] = ProcessRunner.Term };

		command.AddRange( SteamLauncherService.Wrap(
			install,
			new[] { install.RunScript, "--filesystem=" + cache, "--", script },
			root,
			passed ) );

		return true;
	}

	/// <summary>
	/// How a launch failure is reported now that there is no pane to print it
	/// into: the status bar keeps the short form, and a dialog carries the part
	/// that needs more than one line. Both, because the bar is a glance and the
	/// dialog is the explanation.
	/// </summary>
	private async Task Fail( LaunchTarget target, string title, string message )
	{
		statusText.Text = target.Name + ": " + title.ToLowerInvariant();
		UpdateStatusBar();

		await ConfirmDialog.Notify( this, title, message );
	}

	/// <summary>
	/// Runs the dependency sweep in the user's terminal emulator, by re-execing
	/// this binary with --dependency-check (see Program).
	///
	/// The sweep used to print into the diagnostics pane. Re-execing rather than
	/// running it in-process and piping the text somewhere is what keeps the
	/// report intact: it is coloured with SGR sequences and aligned in columns,
	/// both of which need a real terminal - and this way the app keeps no output
	/// surface of its own at all.
	/// </summary>
	private async void RunDependencyCheck()
	{
		try
		{
			var root = RepoRoot.Find();
			if ( root is null )
			{
				await ConfirmDialog.Notify( this, "Repo root not found",
					"ampersand could not locate the s&box tree - it expects game/ and engine/ "
						+ "somewhere above this binary." );
				return;
			}

			// Null only for a single-file host that cannot report its own path;
			// Assembly.Location is empty in that case too, so there is nothing
			// better to fall back to.
			var self = Environment.ProcessPath;
			if ( self is null )
			{
				await ConfirmDialog.Notify( this, "Cannot re-exec",
					"ampersand could not determine its own path, so it cannot run the "
						+ "dependency check in a terminal." );
				return;
			}

			if ( !SystemTerminal.TryBuild( new[] { self, Program.DependencyCheckArgument },
					out var argv, out var emulator ) )
			{
				await ConfirmDialog.Notify( this, "No terminal emulator",
					"The dependency check prints a coloured report, so it needs a terminal "
						+ "window - and no emulator was found on PATH.\n\n"
						+ "Install one (gnome-terminal, konsole, alacritty, kitty, foot, xterm...), "
						+ "or run it yourself:\n\n"
						+ self + " " + Program.DependencyCheckArgument );
				return;
			}

			var info = new ProcessStartInfo
			{
				FileName = argv[0],
				WorkingDirectory = root,
				UseShellExecute = false
			};

			for ( var i = 1; i < argv.Count; i++ )
				info.ArgumentList.Add( argv[i] );

			Process.Start( info );
			statusText.Text = "dependency check running in " + emulator;
		}
		catch ( Exception e )
		{
			statusText.Text = "dependency check failed - " + e.Message;
		}
	}

	private void UpdateStatusBar()
	{
		if ( selected is null )
		{
			stopButton.IsEnabled = false;
			return;
		}

		var runner = selected.Runner;
		stopButton.IsEnabled = runner.IsRunning;

		if ( selected.Preparing )
			statusText.Text = "preparing - " + runner.Name;
		else if ( runner.IsRunning )
			statusText.Text = ( selected.UseSystemTerminal ? "running in a terminal - " : "running - " )
				+ runner.Name;
		else if ( runner.ExitCode is int code )
			statusText.Text = "exited " + code + " - " + runner.Name;
		else
			statusText.Text = "idle - " + runner.Name;

		UpdateToggles( selected );
	}

	private void LoadMetadata()
	{
		var root = RepoRoot.Find();
		if ( root is null )
			return;

		foreach ( var target in targets )
		{
			var script = Path.Combine( root, "ampersand", "apps", target.ScriptFile );

			if ( !File.Exists( script ) )
				continue;

			target.Metadata = ScriptMetadata.Read( script );
			target.UseSniper = target.Metadata.Sniper == SniperMode.Always;
		}
	}

	/// <summary>
	/// A sidebar command row: flush to the panel edges, no chrome of its own,
	/// and a shell glyph in the accent colour. Fluent puts a button's fill on
	/// the templated ContentPresenter, so the hover and pressed states have to
	/// be overridden there rather than on the Button.
	/// </summary>
	private static Button SidebarButton( string text )
	{
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

		// ">_" is the conventional shell glyph and, unlike an emoji, cannot
		// fail to render on any font the system happens to have.
		//
		// The family has to be a real one. "monospace" is a fontconfig alias,
		// not a family, so Avalonia does not resolve it and the glyph silently
		// falls back to the proportional UI font - which is the one thing a
		// shell prompt must not look like.
		row.Children.Add( new TextBlock
		{
			Text = ">_",
			FontFamily = new FontFamily( "DejaVu Sans Mono" ),
			FontSize = 12,
			Foreground = TerminalTheme.Launcher,
			VerticalAlignment = VerticalAlignment.Center
		} );

		row.Children.Add( new TextBlock
		{
			Text = text,
			FontSize = 12,
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.Normal,
			VerticalAlignment = VerticalAlignment.Center
		} );

		var button = new Button
		{
			Content = row,
			Background = Brushes.Transparent,
			BorderThickness = new Thickness( 0 ),
			CornerRadius = new CornerRadius( 0 ),
			Padding = new Thickness( 10, 7 ),
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Left
		};

		button.Styles.Add( PresenterFill( ":pointerover", TerminalTheme.SidebarHover ) );
		button.Styles.Add( PresenterFill( ":pressed", TerminalTheme.SidebarPressed ) );

		return button;
	}

	private static Style PresenterFill( string state, IBrush fill )
	{
		var style = new Style( x => x.OfType<Button>().Class( state ).Template().OfType<ContentPresenter>() );
		style.Setters.Add( new Setter( ContentPresenter.BackgroundProperty, fill ) );
		return style;
	}

	/// <summary>
	/// An uppercase section header on the toolbar surface, in the manner of the
	/// VS Code sidebar. Pairs with the all-caps advisory at the foot.
	/// </summary>
	private static Border SidebarHeader( string text )
	{
		return new Border
		{
			Background = TerminalTheme.ToolbarPanel,
			BorderBrush = TerminalTheme.PanelBorder,
			BorderThickness = new Thickness( 0, 0, 0, 1 ),
			Padding = new Thickness( 10, 6 ),
			Child = new TextBlock
			{
				Text = text,
				FontSize = 11,
				FontWeight = FontWeight.SemiBold,
				Foreground = TerminalTheme.HeaderText
			}
		};
	}

	/// <summary>
	/// Standing advisory. The left panel runs arbitrary executables dropped into
	/// a scanned folder (spec 4b), including ones shared by other people, so the
	/// warning is permanent rather than a dismissible prompt.
	/// </summary>
	private static Border SidebarFooter( string text )
	{
		var row = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 6
		};

		row.Children.Add( new TextBlock
		{
			Text = "ⓘ",
			FontSize = 13,
			Foreground = TerminalTheme.FooterText,
			VerticalAlignment = VerticalAlignment.Center
		} );

		row.Children.Add( new TextBlock
		{
			Text = text,
			FontSize = 10,
			TextWrapping = TextWrapping.Wrap,
			Foreground = TerminalTheme.FooterText,
			VerticalAlignment = VerticalAlignment.Center
		} );

		return new Border
		{
			Background = TerminalTheme.ToolbarPanel,
			BorderBrush = TerminalTheme.PanelBorder,
			BorderThickness = new Thickness( 0, 1, 0, 0 ),
			Padding = new Thickness( 10, 7 ),
			Child = row
		};
	}

	/// <summary>
	/// A flush panel: no margin, its own background, and a 1px rule only on the
	/// edges that face another panel - so adjacent borders never double up.
	/// </summary>
	private static Border Surface( Control child, IBrush background, Thickness border )
	{
		return new Border
		{
			Background = background,
			BorderBrush = TerminalTheme.PanelBorder,
			BorderThickness = border,
			Child = child
		};
	}
}
