using UnityEngine;
using Verse;

namespace ColonyForeman;

[StaticConstructorOnStartup]
public static class ForemanTex
{
	// 2.2.1: never null. Without the Textures folder the lock icon was null and
	// "null texture passed to GUI.DrawTexture" filled the log every frame.
	public static readonly Texture2D Lock =
		ContentFinder<Texture2D>.Get("UI/ColonyForeman/Lock", reportFailure: false)
		?? ContentFinder<Texture2D>.Get("UI/Commands/ForbidOn", reportFailure: false)
		?? BaseContent.WhiteTex;
}
