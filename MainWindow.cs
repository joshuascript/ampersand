using System;
using System.Collections.Generic;
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
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaTerminal;

namespace Ampersand;

internal sealed class MainWindow : Window
{
	// The grid a terminal is CONSTRUCTED with, before the control has been laid
	// out and measured its own cell. It is transient - the first layout pass
	// overwrites it (a 1040x660 window measures 107x16) - so nothing may be
	// derived from it. The pty is opened at the terminal's live size instead;
	// see Launch().
	private const int Columns = 200;
	private const int Rows = 50;

	private readonly AvaloniaList<LaunchTarget> targets = new();

	private readonly ListBox targetList;
	private readonly Panel terminalHost;
	private readonly TerminalControl diagnostics;
	private readonly TextBlock statusText;
	private readonly Button clearButton;
	private readonly Button stopButton;
	private readonly Button depCheckButton;
	private readonly CheckBox steamRuntime;
	private readonly CheckBox detachToggle;

	private LaunchTarget? selected;
	private bool updatingCheckbox;
	private bool showingDiagnostics;

	public MainWindow()
	{
		Title = "s&box launcher";
		Width = 1040;
		Height = 660;
		RequestedThemeVariant = ThemeVariant.Dark;
		Background = TerminalTheme.Background;

		diagnostics = CreateTerminal();
		terminalHost = new Panel();
		terminalHost.Children.Add( diagnostics );

		foreach ( var (name, script) in new[]
		{
			( "sbox", "sbox.sh" ),
			( "sbox-dev", "sbox-dev.sh" ),
			( "sbox-server", "sbox-server.sh" )
		} )
		{
			var terminal = CreateTerminal();
			terminal.IsVisible = false;
			terminalHost.Children.Add( terminal );
			targets.Add( new LaunchTarget( name, script, terminal ) );
		}

		LoadMetadata();

		targetList = new ListBox
		{
			ItemsSource = targets,
			Background = Brushes.Transparent,
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
		// Detach for the run you are about to start.
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

		// Detach is decided BEFORE launching: once the process owns a pty we
		// cannot hand its stream to a terminal that starts later.
		detachToggle = new CheckBox
		{
			Content = "Detach to system terminal",
			IsEnabled = false,
			VerticalAlignment = VerticalAlignment.Center
		};
		detachToggle.IsCheckedChanged += ( _, _ ) =>
		{
			if ( !updatingCheckbox && selected is not null )
				selected.Detach = detachToggle.IsChecked == true;
		};

		var toggles = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 16,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness( 12, 0 )
		};
		toggles.Children.Add( detachToggle );
		toggles.Children.Add( steamRuntime );

		statusText = new TextBlock
		{
			Text = "idle",
			VerticalAlignment = VerticalAlignment.Center,
			Margin = new Thickness( 8, 0 )
		};

		clearButton = new Button { Content = "Clear", Margin = new Thickness( 4, 4 ) };
		clearButton.Click += ( _, _ ) => ClearCurrentBuffer();

		stopButton = new Button { Content = "Stop", IsEnabled = false, Margin = new Thickness( 4, 4, 8, 4 ) };
		stopButton.Click += ( _, _ ) => selected?.Runner.Stop();

		var outputHeader = new Grid
		{
			ColumnDefinitions = new ColumnDefinitions( "*,Auto,Auto" ),
			Background = TerminalTheme.ToolbarPanel
		};
		outputHeader.Children.Add( statusText );
		Grid.SetColumn( clearButton, 1 );
		outputHeader.Children.Add( clearButton );
		Grid.SetColumn( stopButton, 2 );
		outputHeader.Children.Add( stopButton );

		var bottom = new Grid { RowDefinitions = new RowDefinitions( "Auto,*" ) };
		bottom.Children.Add( outputHeader );
		Grid.SetRow( terminalHost, 1 );
		bottom.Children.Add( terminalHost );

		var right = new Grid { RowDefinitions = new RowDefinitions( "50*,8*,42*" ) };
		var topPanel = Surface( targetList, TerminalTheme.TargetPanel, new Thickness( 0, 0, 0, 1 ) );
		var midPanel = Surface( toggles, TerminalTheme.ToolbarPanel, new Thickness( 0, 0, 0, 1 ) );
		var bottomPanel = Surface( bottom, TerminalTheme.Background, new Thickness( 0 ) );

		right.Children.Add( topPanel );
		Grid.SetRow( midPanel, 1 );
		right.Children.Add( midPanel );
		Grid.SetRow( bottomPanel, 2 );
		right.Children.Add( bottomPanel );

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
		}

		showingDiagnostics = true;
		FeedNotice( "ampersand: select an app above, set any options, then double-click to launch." );

		if ( SystemTerminal.Detect() is null )
			FeedNotice( "ampersand: no terminal emulator found - Detach will stay unavailable." );
	}

	/// <summary>
	/// A terminal for one target. AvaloniaTerminal is an xterm.js port, so it
	/// brings its own scrollback, follow-the-tail behaviour, selection and search
	/// - all of which we previously hand-rolled.
	/// </summary>
	private TerminalControl CreateTerminal()
	{
		// THE control does not create its own model - the host must. Without
		// this assignment Model stays null forever and every Feed is silently
		// discarded, which is exactly what produced a blank pane.
		//
		// Cols and Rows are the construction size only - the control measures
		// its own cell and resizes the grid on the first layout pass.
		//
		// There is deliberately no ReflowOnResize here. The library exposes it
		// and it reads like the flag that governs this, but it is inert: it is
		// never forwarded to the engine, and XTerm.NET 1.0.12 has no reflow to
		// forward it to. Setting it taught us nothing and cost a diagnosis.
		var model = new TerminalControlModel( new TerminalOptions
		{
			Cols = Columns,
			Rows = Rows,
			Scrollback = 10000
		} );

		var terminal = new TerminalControl
		{
			Model = model,
			FontFamily = "DejaVu Sans Mono",
			FontSize = 12,
			Background = TerminalTheme.Background,
			SelectionBrush = TerminalTheme.Selection
		};

		var copy = new MenuItem { Header = "Copy" };
		copy.Click += async ( _, _ ) => await terminal.CopySelectionAsync();

		// CopySelectionAsync is a silent no-op with nothing selected, so the
		// item has to say so rather than looking like it did nothing.
		copy.Bind( IsEnabledProperty, terminal.GetObservable( TerminalControl.HasSelectionProperty ) );

		var paste = new MenuItem { Header = "Paste" };
		paste.Click += async ( _, _ ) => await terminal.PasteFromClipboardAsync();

		var selectAll = new MenuItem { Header = "Select All" };
		selectAll.Click += ( _, _ ) => terminal.SelectAll();

		var clear = new MenuItem { Header = "Clear" };
		clear.Click += ( _, _ ) => ClearCurrentBuffer();

		var menu = new ContextMenu();
		menu.Items.Add( copy );
		menu.Items.Add( paste );
		menu.Items.Add( selectAll );
		menu.Items.Add( new Separator() );
		menu.Items.Add( clear );
		terminal.ContextMenu = menu;

		TerminalClipboard.Attach( terminal );

		return terminal;
	}

	private void ShowTerminal( TerminalControl terminal )
	{
		foreach ( var child in terminalHost.Children )
			child.IsVisible = ReferenceEquals( child, terminal );
	}

	private void FeedDiagnostic( string line )
	{
		diagnostics.Model.Feed( line + "\r\n" );
	}

	/// <summary>One of the launcher's own messages, in the accent colour.</summary>
	private void FeedNotice( string line )
	{
		FeedDiagnostic( Ansi.Paint( Ansi.Cyan, line ) );
	}

	private void ClearCurrentBuffer()
	{
		if ( showingDiagnostics )
		{
			diagnostics.Model.Feed( Ansi.ClearAll );
			return;
		}

		selected?.Clear();
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

		if ( ReferenceEquals( target, selected ) && !showingDiagnostics )
			return;

		showingDiagnostics = false;
		selected = target;
		ShowTerminal( target.Terminal );
		UpdateStatusBar();
		UpdateToggles( target );
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

		// Detach cannot be changed once running, because the stream is already
		// committed to either the pty or the emulator.
		detachToggle.IsChecked = target.Detach;
		detachToggle.IsEnabled = SystemTerminal.Detect() is not null
			&& !target.Runner.IsRunning && !target.Preparing;

		updatingCheckbox = false;
	}

	/// <summary>
	/// Guard around the launch path, and nothing else.
	///
	/// This is reached from a double-click handler, so it is async void: nothing
	/// observes the task, and an exception escaping it is unhandled and takes the
	/// window down. A tool for diagnosing a broken engine must not be killed by
	/// the engine being broken, so a failure is reported into the target's own
	/// pane instead. The prepare path is the live risk - it shells out, copies
	/// megabytes, and waits on Steam.
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
			//
			// Message rather than the whole exception: the terminal is in raw
			// mode and Append supplies one CRLF at the end, so the bare LFs in
			// a stack trace would staircase it across the pane.
			target.Append( "ampersand: launch failed - " + e.Message );
		}
	}

	private async Task LaunchCore( LaunchTarget target )
	{
		var root = RepoRoot.Find();
		if ( root is null )
		{
			target.Append( "ampersand: could not locate the repo root - expected game/ and engine/ above this binary" );
			return;
		}

		var script = Path.Combine( root, "ampersand", "apps", target.ScriptFile );
		if ( !File.Exists( script ) )
		{
			target.Append( "ampersand: launch script not found: " + script );
			return;
		}

		target.Clear();

		var env = new Dictionary<string, string> { ["SBOX_REPO_ROOT"] = root };
		var command = new List<string>();

		// Held across the await below, not just the spawn: PrepareSniper can sit
		// for minutes waiting on Steam, and nothing else marks the target busy.
		target.Preparing = true;

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

		target.Append( "$ " + string.Join( " ", command ) );

		try
		{
			// The pane has already been laid out and measured, so this is the
			// grid on screen. Opening the pty at the constant instead would
			// have the engine format for 200 columns inside a 107-column
			// window, and nothing would tell it otherwise until a resize.
			var grid = target.Grid;
			target.Runner.Start( command, root, env, target.Detach, grid.Cols, grid.Rows );
		}
		catch ( Exception e )
		{
			target.Append( "ampersand: failed to start - " + e.Message );
			return;
		}

		target.NotifyStatusChanged();
		UpdateStatusBar();
		UpdateToggles( target );
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
			target.Append( "ampersand: Steam Linux Runtime 3.0 (sniper) is not installed." );
			target.Append( "    Install it from Steam:  steam steam://install/" + SniperRuntime.SteamAppId );
			return false;
		}

		target.Append( "ampersand: runtime " + install.Path + " (" + install.Version + ")" );

		// Everything below shells out or copies megabytes, so none of it may run
		// on the UI thread - the probe alone can sit five seconds. This is the
		// reason the method is async; awaiting a blocking call would only move
		// the freeze, not remove it.
		if ( !await Task.Run( () => SteamLauncherService.IsAvailable( install ) )
			&& !await StartSteamFor( target, install ) )
		{
			return false;
		}

		var requirements = await Task.Run( () =>
		{
			var met = SniperRuntime.CheckRequirements( install, out var found );
			return (Met: met, Problems: found);
		} );

		if ( !requirements.Met )
		{
			target.Append( "ampersand: this host cannot start a pressure-vessel container." );

			foreach ( var problem in requirements.Problems )
				target.Append( "    " + problem );

			return false;
		}

		var compat = await Task.Run( () =>
		{
			var ready = SniperCompat.Ensure( out var found );
			return (Ready: ready, Problems: found);
		} );

		if ( !compat.Ready )
		{
			foreach ( var problem in compat.Problems )
				target.Append( "ampersand: " + problem );

			return false;
		}

		var cache = SniperCompat.CacheDirectory;

		env["SBOX_IN_SNIPER"] = "1";
		env["SBOX_SNIPER_COMPAT"] = cache;

		// TERM is added here rather than left to ProcessRunner: that one is set on
		// launch-client's own environment, and the child does not inherit it.
		var passed = new Dictionary<string, string>( env ) { ["TERM"] = ProcessRunner.Term };

		command.AddRange( SteamLauncherService.Wrap(
			install,
			new[] { install.RunScript, "--filesystem=" + cache, "--", script },
			root,
			passed ) );

		return true;
	}

	/// <summary>
	/// Offers to start Steam, then waits for its launcher service. Declining, or a
	/// Steam that never comes up, aborts the launch - there is no second route
	/// into the container to fall back to.
	/// </summary>
	private async Task<bool> StartSteamFor( LaunchTarget target, SniperInstall install )
	{
		target.Append( "ampersand: Steam is not running." );
		target.Append( "    The Steam runtime can only be entered under Steam's own AppArmor profile," );
		target.Append( "    so the client has to be up. See docs/sniper-userns-apparmor.md." );

		var start = await ConfirmDialog.Show(
			this,
			"Steam is not running",
			"s&box can only be launched in the Steam runtime while the Steam client is "
				+ "running - the container is entered through Steam's own launcher service.\n\n"
				+ "Start Steam now and wait for it?",
			"Start Steam",
			"Cancel" );

		if ( !start )
		{
			target.Append( "ampersand: launch cancelled." );
			return false;
		}

		if ( !SteamLauncherService.TryStartSteam( out var problem ) )
		{
			target.Append( "ampersand: " + problem );
			return false;
		}

		target.Append( "ampersand: starting Steam..." );

		var ready = await Task.Run( () => SteamLauncherService.WaitForService(
			install,
			TimeSpan.FromMinutes( 2 ),
			line => Dispatcher.UIThread.Post( () => target.Append( "ampersand: " + line ) ) ) );

		if ( !ready )
		{
			target.Append( "ampersand: Steam's launcher service did not appear - launch cancelled." );
			target.Append( "    Check Steam is signed in, then try again." );
			return false;
		}

		target.Append( "ampersand: Steam is up." );
		return true;
	}

	/// <summary>
	/// Guard around the dependency check, for the same reason as Launch: an
	/// async void click handler whose exception would otherwise be unhandled.
	///
	/// Unlike Launch this one has to undo something. The inner try already
	/// covers the sweep, but not the statements ahead of it - and one of those
	/// disables the button, so a throw there would leave the check permanently
	/// greyed out with no way back short of a restart. Re-enabling here is what
	/// makes this more than a log line.
	/// </summary>
	private async void RunDependencyCheck()
	{
		try
		{
			await RunDependencyCheckCore();
		}
		catch ( Exception e )
		{
			depCheckButton.IsEnabled = true;
			statusText.Text = "dependency check failed";
			FeedNotice( "ampersand: dependency check failed - " + e.Message );
		}
	}

	private async Task RunDependencyCheckCore()
	{
		var root = RepoRoot.Find();

		showingDiagnostics = true;
		ShowTerminal( diagnostics );
		diagnostics.Model.Feed( Ansi.ClearAll );

		if ( root is null )
		{
			FeedNotice( "ampersand: could not locate the repo root" );
			return;
		}

		depCheckButton.IsEnabled = false;
		stopButton.IsEnabled = false;
		statusText.Text = "checking dependencies...";

		try
		{
			await Task.Run( () => DependencyCheck.Run( root,
				line => Dispatcher.UIThread.Post( () => FeedDiagnostic( line ) ) ) );

			statusText.Text = "dependency check complete";
		}
		catch ( Exception e )
		{
			FeedNotice( "ampersand: dependency check failed - " + e.Message );
			statusText.Text = "dependency check failed";
		}
		finally
		{
			depCheckButton.IsEnabled = true;
		}
	}

	private void UpdateStatusBar()
	{
		if ( selected is null )
		{
			stopButton.IsEnabled = false;
			statusText.Text = "idle";
			return;
		}

		var runner = selected.Runner;
		stopButton.IsEnabled = runner.IsRunning;

		if ( runner.IsRunning )
			statusText.Text = ( selected.Detach ? "detached - " : "running - " ) + runner.Name;
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
		// shell prompt must not look like. Same family as the terminal itself.
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
	/// a scanned folder (§4b), including ones shared by other people, so the
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
			Text = "\u24d8",
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
