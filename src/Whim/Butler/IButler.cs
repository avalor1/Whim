using System;

namespace Whim;

/// <summary>
/// The butler is responsible for using the <see cref="IWorkspaceManager"/> and <see cref="IMonitorManager"/>
/// to handle events from the <see cref="IWindowManager"/> to update the assignment of <see cref="IWindow"/>s
/// to <see cref="IWorkspace"/>s, and <see cref="IWorkspace"/>s to <see cref="IMonitor"/>s.
/// </summary>
public interface IButler : IButlerChores
{
	/// <summary>
	/// The pantry is responsible for mapping <see cref="IWindow"/>s to <see cref="IWorkspace"/>s
	/// to <see cref="IMonitor"/>s.
	///
	/// The pantry can only be set prior to <see cref="Initialize"/>.
	///
	/// Defaults to <see cref="ButlerPantry"/>.
	/// </summary>
	IButlerPantry Pantry { get; set; }

	/// <summary>
	/// Description of how an <see cref="IWindow"/> has been routed between workspaces.
	/// </summary>
	event EventHandler<RouteEventArgs>? WindowRouted;

	/// <summary>
	/// Event for when a monitor's workspace has changed.
	/// </summary>
	event EventHandler<MonitorWorkspaceChangedEventArgs>? MonitorWorkspaceChanged;

	/// <summary>
	/// Initialize the windows and workspaces.
	/// </summary>
	void Initialize();
}
