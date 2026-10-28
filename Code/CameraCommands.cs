using Sandbox.Rendering;

namespace OceanLib;

/// <summary>
/// A command list kept on whichever camera the scene renders with, moving to the new one when the scene's camera
/// changes. Call <see cref="Follow"/> every frame and <see cref="Dispose"/> when done.
/// </summary>
internal sealed class CameraCommands
{
	public CommandList List { get; }
	public CameraComponent Camera { get; private set; }

	private readonly Stage m_Stage;
	private readonly int m_Order;

	public CameraCommands( string name, Stage stage, int order )
	{
		List = new CommandList( name );
		m_Stage = stage;
		m_Order = order;
	}

	public void Follow( Scene scene )
	{
		var camera = scene.Camera;

		if ( camera == Camera )
			return;

		if ( Camera.IsValid() )
			Camera.RemoveCommandList( List );

		Camera = camera;
		Camera?.AddCommandList( List, m_Stage, m_Order );
	}

	public void Dispose()
	{
		if ( Camera.IsValid() )
			Camera.RemoveCommandList( List );

		Camera = null;
	}
}
