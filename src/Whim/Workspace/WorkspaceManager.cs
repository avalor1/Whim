using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Whim;

/// <summary>
/// Implementation of <see cref="IWorkspaceManager"/>.
/// </summary>
internal class WorkspaceManager : IWorkspaceManager
{
	private bool _initialized;

	private readonly IContext _context;

	/// <summary>
	/// The <see cref="IWorkspace"/>s stored by this manager.
	/// </summary>
	private readonly List<IWorkspace> _workspaces = new();

	/// <summary>
	/// Maps windows to their workspace.
	/// </summary>
	private readonly Dictionary<IWindow, IWorkspace> _windowWorkspaceMap = new();

	/// <summary>
	/// All the phantom windows that are currently added.
	/// </summary>
	private readonly HashSet<IWindow> _phantomWindows = new();

	internal IEnumerable<IWindow> PhantomWindows => _phantomWindows;

	/// <summary>
	/// Maps monitors to their active workspace.
	/// </summary>
	private readonly Dictionary<IMonitor, IWorkspace> _monitorWorkspaceMap = new();

	public event EventHandler<MonitorWorkspaceChangedEventArgs>? MonitorWorkspaceChanged;

	public event EventHandler<RouteEventArgs>? WindowRouted;

	public event EventHandler<WorkspaceEventArgs>? WorkspaceAdded;

	public event EventHandler<WorkspaceEventArgs>? WorkspaceRemoved;

	public event EventHandler<ActiveLayoutEngineChangedEventArgs>? ActiveLayoutEngineChanged;

	public event EventHandler<WorkspaceRenamedEventArgs>? WorkspaceRenamed;

	public Func<IContext, string, IWorkspace> WorkspaceFactory { get; set; } =
		(context, name) =>
		{
			return new Workspace(context, name, new ColumnLayoutEngine());
		};

	/// <summary>
	/// The active workspace.
	/// </summary>
	public IWorkspace ActiveWorkspace => _monitorWorkspaceMap[_context.MonitorManager.FocusedMonitor];

	private readonly List<ProxyLayoutEngine> _proxyLayoutEngines = new();

	private bool _disposedValue;

	public IEnumerable<ProxyLayoutEngine> ProxyLayoutEngines => _proxyLayoutEngines;

	public WorkspaceManager(IContext context)
	{
		_context = context;
	}

	public void Initialize()
	{
		Logger.Debug("Initializing workspace manager...");

		_context.MonitorManager.MonitorsChanged += MonitorManager_MonitorsChanged;

		// Ensure there's at least n workspaces, for n monitors.
		if (_context.MonitorManager.Length > _workspaces.Count)
		{
			throw new InvalidOperationException("There must be at least as many workspaces as monitors.");
		}

		// Assign workspaces to monitors.
		int idx = 0;
		foreach (IMonitor monitor in _context.MonitorManager)
		{
			// Get the workspace for this monitor. If the user hasn't provided enough workspaces, create a new one.
			IWorkspace workspace =
				idx < _workspaces.Count ? _workspaces[idx] : WorkspaceFactory(_context, $"Workspace {idx + 1}");

			Activate(workspace, monitor);
			idx++;
		}

		// Initialize each of the workspaces.
		foreach (IWorkspace workspace in _workspaces)
		{
			workspace.Initialize();
		}

		_initialized = true;
	}

	#region Workspaces
	public IWorkspace? this[string workspaceName] => TryGet(workspaceName);

	public void Add(IWorkspace workspace)
	{
		Logger.Debug($"Adding workspace {workspace}");

		if (_workspaces.Contains(workspace))
		{
			Logger.Debug($"Workspace {workspace} already exists");
			return;
		}

		_workspaces.Add(workspace);

		if (_initialized)
		{
			workspace.Initialize();
		}

		WorkspaceAdded?.Invoke(this, new WorkspaceEventArgs() { Workspace = workspace });
	}

	public IEnumerator<IWorkspace> GetEnumerator() => _workspaces.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	public bool Remove(IWorkspace workspace)
	{
		Logger.Debug($"Removing workspace {workspace}");

		if (_workspaces.Count - 1 < _context.MonitorManager.Length)
		{
			Logger.Debug($"There must be at least {_context.MonitorManager.Length} workspaces.");
			return false;
		}

		bool wasFound = _workspaces.Remove(workspace);

		if (!wasFound)
		{
			Logger.Debug($"Workspace {workspace} was not found");
			return false;
		}

		WorkspaceRemoved?.Invoke(this, new WorkspaceEventArgs() { Workspace = workspace });

		// Remap windows to the last workspace
		IWorkspace lastWorkspace = _workspaces[^1];

		foreach (IWindow window in workspace.Windows)
		{
			lastWorkspace.AddWindow(window);
			_windowWorkspaceMap[window] = lastWorkspace;
		}

		return wasFound;
	}

	public bool Remove(string workspaceName)
	{
		Logger.Debug($"Trying to remove workspace {workspaceName}");

		IWorkspace? workspace = _workspaces.Find(w => w.Name == workspaceName);
		if (workspace == null)
		{
			Logger.Debug($"Workspace {workspaceName} not found");
			return false;
		}

		return Remove(workspace);
	}

	public IWorkspace? TryGet(string workspaceName)
	{
		Logger.Debug($"Trying to get workspace {workspaceName}");
		return _workspaces.Find(w => w.Name == workspaceName);
	}

	public void Activate(IWorkspace workspace, IMonitor? focusedMonitor = null)
	{
		Logger.Debug($"Activating workspace {workspace}");

		focusedMonitor ??= _context.MonitorManager.FocusedMonitor;

		// Get the old workspace for the event.
		_monitorWorkspaceMap.TryGetValue(focusedMonitor, out IWorkspace? oldWorkspace);

		// Find the monitor which just lost `workspace`.
		IMonitor? loserMonitor = _monitorWorkspaceMap.FirstOrDefault(m => m.Value == workspace).Key;

		// Update the focused monitor. Having this line before the old workspace is deactivated
		// is important, as WindowManager.OnWindowHidden() checks to see if a window is in a
		// visible workspace when it receives the EVENT_OBJECT_HIDE event.
		_monitorWorkspaceMap[focusedMonitor] = workspace;

		// Hide all the windows from the old workspace.
		oldWorkspace?.Deactivate();

		// Send out an event about the losing monitor.
		if (loserMonitor != null && oldWorkspace != null)
		{
			_monitorWorkspaceMap[loserMonitor] = oldWorkspace;
			MonitorWorkspaceChanged?.Invoke(
				this,
				new MonitorWorkspaceChangedEventArgs()
				{
					Monitor = loserMonitor,
					OldWorkspace = workspace,
					NewWorkspace = oldWorkspace
				}
			);
		}

		// Layout the new workspace.
		workspace.DoLayout();
		workspace.FocusFirstWindow();
		MonitorWorkspaceChanged?.Invoke(
			this,
			new MonitorWorkspaceChangedEventArgs()
			{
				Monitor = focusedMonitor,
				OldWorkspace = oldWorkspace,
				NewWorkspace = workspace
			}
		);
	}

	public IMonitor? GetMonitorForWorkspace(IWorkspace workspace)
	{
		Logger.Debug($"Getting monitor for active workspace {workspace}");

		// Linear search for the monitor that contains the workspace.
		foreach ((IMonitor m, IWorkspace w) in _monitorWorkspaceMap)
		{
			if (w == workspace)
			{
				Logger.Debug($"Found monitor {m} for workspace {workspace}");
				return m;
			}
		}

		Logger.Debug($"Could not find monitor for workspace {workspace}");
		return null;
	}

	public void LayoutAllActiveWorkspaces()
	{
		Logger.Debug("Layout all active workspaces");

		// For each workspace which is active in a monitor, do a layout.
		foreach (IWorkspace workspace in _monitorWorkspaceMap.Values)
		{
			workspace.DoLayout();
		}
	}
	#endregion

	#region Windows
	/// <summary>
	/// Called when a window has been added by the <see cref="IWindowManager"/>.
	/// </summary>
	/// <param name="window">The window that was added.</param>
	internal virtual void WindowAdded(IWindow window)
	{
		Logger.Debug($"Adding window {window}");
		IWorkspace? workspace = _context.RouterManager.RouteWindow(window);

		if (!_context.RouterManager.RouteToActiveWorkspace && workspace == null)
		{
			IMonitor? monitor = _context.MonitorManager.GetMonitorAtPoint(window.Center);
			if (monitor is not null)
			{
				workspace = GetWorkspaceForMonitor(monitor);
			}
		}
		workspace ??= ActiveWorkspace;

		_windowWorkspaceMap[window] = workspace;
		workspace.AddWindow(window);
		WindowRouted?.Invoke(this, RouteEventArgs.WindowAdded(window, workspace));
		Logger.Debug($"Window {window} added to workspace {workspace.Name}");
	}

	/// <summary>
	/// Called when a window has been removed by the <see cref="IWindowManager"/>.
	/// </summary>
	/// <param name="window">The window that was removed.</param>
	internal virtual void WindowRemoved(IWindow window)
	{
		Logger.Debug($"Window removed: {window}");

		if (!_windowWorkspaceMap.TryGetValue(window, out IWorkspace? workspace))
		{
			Logger.Error($"Window {window} was not found in any workspace");
			return;
		}

		workspace.RemoveWindow(window);
		_windowWorkspaceMap.Remove(window);
		WindowRouted?.Invoke(this, RouteEventArgs.WindowRemoved(window, workspace));
	}

	/// <summary>
	/// Called when a window has been focused by the <see cref="IWindowManager"/>.
	/// </summary>
	/// <param name="window">The window that was focused.</param>
	internal virtual void WindowFocused(IWindow window)
	{
		Logger.Debug($"Window focused: {window}");

		foreach (IWorkspace workspace in _workspaces)
		{
			if (workspace is Workspace ws)
			{
				ws.WindowFocused(window);
			}
		}
	}

	/// <summary>
	/// Called when a window is about to be minimized.
	/// </summary>
	/// <param name="window">The window that is minimizing.</param>
	internal virtual void WindowMinimizeStart(IWindow window)
	{
		Logger.Debug($"Window minimize start: {window}");

		if (!_windowWorkspaceMap.TryGetValue(window, out IWorkspace? workspace))
		{
			Logger.Error($"Window {window} was not found in any workspace");
			return;
		}

		workspace.RemoveWindow(window);
	}

	/// <summary>
	/// Called when a window is about to be restored.
	/// </summary>
	/// <param name="window">The window that is restoring.</param>
	internal virtual void WindowMinimizeEnd(IWindow window)
	{
		Logger.Debug($"Window minimize end: {window}");

		WindowAdded(window);
	}
	#endregion

	#region Monitors
	public void MonitorManager_MonitorsChanged(object? sender, MonitorsChangedEventArgs e)
	{
		Logger.Debug($"MonitorManager_MonitorsChanged: {e}");

		// If a monitor was removed, remove the workspace from the map.
		foreach (IMonitor monitor in e.RemovedMonitors)
		{
			IWorkspace workspace = _monitorWorkspaceMap[monitor];
			workspace.Deactivate();

			_monitorWorkspaceMap.Remove(monitor);
		}

		// If a monitor was added, set it to an inactive workspace.
		foreach (IMonitor monitor in e.AddedMonitors)
		{
			// Try find a workspace which doesn't have a monitor.
			IWorkspace? workspace = _workspaces.Find(w => GetMonitorForWorkspace(w) == null);

			// If there's no workspace, create one.
			if (workspace is null)
			{
				workspace = WorkspaceFactory(_context, $"Workspace {_workspaces.Count + 1}");
				workspace.Initialize();
			}

			// Add the workspace to the map.
			Activate(workspace, monitor);
		}

		// For each workspace which is active in a monitor, do a layout.
		// This will handle cases when the monitor's properties have changed.
		LayoutAllActiveWorkspaces();
	}

	public void AddProxyLayoutEngine(ProxyLayoutEngine proxyLayoutEngine)
	{
		_proxyLayoutEngines.Add(proxyLayoutEngine);
	}

	public IWorkspace? GetWorkspaceForMonitor(IMonitor monitor)
	{
		return _monitorWorkspaceMap.TryGetValue(monitor, out IWorkspace? workspace) ? workspace : null;
	}

	public IMonitor? GetMonitorForWindow(IWindow window)
	{
		return _windowWorkspaceMap.TryGetValue(window, out IWorkspace? workspace)
			? GetMonitorForWorkspace(workspace)
			: null;
	}
	#endregion

	public void TriggerActiveLayoutEngineChanged(ActiveLayoutEngineChangedEventArgs args)
	{
		ActiveLayoutEngineChanged?.Invoke(this, args);
	}

	public void TriggerWorkspaceRenamed(WorkspaceRenamedEventArgs args)
	{
		WorkspaceRenamed?.Invoke(this, args);
	}

	public void MoveWindowToWorkspace(IWorkspace workspace, IWindow? window = null)
	{
		window ??= ActiveWorkspace.LastFocusedWindow;
		if (window == null)
		{
			Logger.Error("No window was found");
			return;
		}

		if (_phantomWindows.Contains(window))
		{
			Logger.Error($"Window {window} is a phantom window and cannot be moved");
			return;
		}

		Logger.Debug($"Moving window {window} to workspace {workspace}");

		// Find the current workspace for the window.
		if (!_windowWorkspaceMap.TryGetValue(window, out IWorkspace? currentWorkspace))
		{
			Logger.Error($"Window {window} was not found in any workspace");
			return;
		}

		_windowWorkspaceMap[window] = workspace;
		currentWorkspace.RemoveWindow(window);
		workspace.AddWindow(window);
	}

	public void MoveWindowToMonitor(IMonitor monitor, IWindow? window = null)
	{
		window ??= ActiveWorkspace.LastFocusedWindow;
		if (window == null)
		{
			Logger.Error("No window was found");
			return;
		}

		Logger.Debug($"Moving window {window} to monitor {monitor}");
		IMonitor? oldMonitor = GetMonitorForWindow(window);
		if (oldMonitor == null)
		{
			Logger.Error($"Window {window} was not found in any monitor");
			return;
		}

		if (oldMonitor == monitor)
		{
			Logger.Error($"Window {window} is already on monitor {monitor}");
			return;
		}

		IWorkspace? workspace = GetWorkspaceForMonitor(monitor);
		if (workspace == null)
		{
			Logger.Error($"Monitor {monitor} was not found in any workspace");
			return;
		}

		MoveWindowToWorkspace(workspace, window);
	}

	public void MoveWindowToPreviousMonitor(IWindow? window = null)
	{
		Logger.Debug($"Moving window {window} to previous monitor");

		// Get the previous monitor.
		IMonitor monitor = _context.MonitorManager.FocusedMonitor;
		IMonitor previousMonitor = _context.MonitorManager.GetPreviousMonitor(monitor);

		MoveWindowToMonitor(previousMonitor, window);
	}

	public void MoveWindowToNextMonitor(IWindow? window = null)
	{
		Logger.Debug($"Moving window {window} to next monitor");

		// Get the next monitor.
		IMonitor monitor = _context.MonitorManager.FocusedMonitor;
		IMonitor nextMonitor = _context.MonitorManager.GetNextMonitor(monitor);

		MoveWindowToMonitor(nextMonitor, window);
	}

	public void MoveWindowToPoint(IWindow window, IPoint<int> point)
	{
		Logger.Debug($"Moving window {window} to location {point}");

		// Get the monitor.
		IMonitor targetMonitor = _context.MonitorManager.GetMonitorAtPoint(point);

		// Get the target workspace.
		IWorkspace? targetWorkspace = GetWorkspaceForMonitor(targetMonitor);
		if (targetWorkspace == null)
		{
			Logger.Error($"Monitor {targetMonitor} was not found to correspond to any workspace");
			return;
		}

		Logger.Debug($"Moving window {window} to workspace {targetWorkspace} in monitor {targetMonitor}");
		Logger.Debug($"Active workspace is {ActiveWorkspace}");

		bool isPhantom = _phantomWindows.Contains(window);
		if (isPhantom && targetWorkspace != ActiveWorkspace)
		{
			Logger.Error($"Window {window} is a phantom window and cannot be moved");
			return;
		}

		if (!ActiveWorkspace.RemoveWindow(window))
		{
			Logger.Error($"Could not remove window {window} from workspace {ActiveWorkspace}");
			return;
		}

		IPoint<int> pointInMonitor = targetMonitor.WorkingArea.ToMonitorCoordinates(point);
		IPoint<double> normalized = targetMonitor.WorkingArea.ToUnitSquare(pointInMonitor);
		Logger.Debug($"Normalized location: {normalized}");

		targetWorkspace.MoveWindowToPoint(window, normalized);
		_windowWorkspaceMap[window] = targetWorkspace;

		// Trigger layouts.
		window.Focus();
	}

	#region Phantom Windows
	public void AddPhantomWindow(IWorkspace workspace, IWindow window)
	{
		Logger.Debug($"Adding phantom window {window} to workspace {workspace}");
		_phantomWindows.Add(window);
		_windowWorkspaceMap[window] = workspace;
	}

	public void RemovePhantomWindow(IWindow window)
	{
		Logger.Debug($"Removing phantom window {window}");
		_phantomWindows.Remove(window);
		_windowWorkspaceMap.Remove(window);
	}
	#endregion

	protected virtual void Dispose(bool disposing)
	{
		if (!_disposedValue)
		{
			if (disposing)
			{
				Logger.Debug("Disposing workspace manager");

				// dispose managed state (managed objects)
				foreach (IWorkspace workspace in _workspaces)
				{
					workspace.Dispose();
				}
			}

			// free unmanaged resources (unmanaged objects) and override finalizer
			// set large fields to null
			_disposedValue = true;
		}
	}

	public void Dispose()
	{
		// Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}
