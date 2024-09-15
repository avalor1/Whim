using System.Diagnostics.CodeAnalysis;
using Corvus.Json;
using Whim.CommandPalette;
using Whim.Gaps;

namespace Whim.Yaml;

/// <summary>
/// Loads plugins from the YAML configuration.
/// </summary>
[SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Items will be disposed by the context where appropriate."
)]
internal static class YamlPluginLoader
{
	public static void LoadPlugins(IContext ctx, Schema schema)
	{
		LoadGapsPlugin(ctx, schema);
		LoadCommandPalettePlugin(ctx, schema);
	}

	private static void LoadGapsPlugin(IContext ctx, Schema schema)
	{
		var gaps = schema.Plugins.Gaps;

		if (!gaps.IsValid())
		{
			Logger.Debug("Gaps plugin is not valid.");
			return;
		}

		if (gaps.IsEnabled.AsOptional() is { } isEnabled && !isEnabled)
		{
			Logger.Debug("Gaps plugin is not enabled.");
			return;
		}

		GapsConfig config = new();

		if (gaps.OuterGap.AsOptional() is { } outerGap)
		{
			config.OuterGap = (int)outerGap;
		}

		if (gaps.InnerGap.AsOptional() is { } innerGap)
		{
			config.InnerGap = (int)innerGap;
		}

		if (gaps.DefaultOuterDelta.AsOptional() is { } defaultOuterDelta)
		{
			config.DefaultOuterDelta = (int)defaultOuterDelta;
		}

		if (gaps.DefaultInnerDelta.AsOptional() is { } defaultInnerDelta)
		{
			config.DefaultInnerDelta = (int)defaultInnerDelta;
		}

		ctx.PluginManager.AddPlugin(new GapsPlugin(ctx, config));
	}

	private static void LoadCommandPalettePlugin(IContext ctx, Schema schema)
	{
		var commandPalette = schema.Plugins.CommandPalette;

		if (!commandPalette.IsValid())
		{
			Logger.Debug("CommandPalette plugin is not valid.");
			return;
		}

		if (commandPalette.IsEnabled.AsOptional() is { } isEnabled && !isEnabled)
		{
			Logger.Debug("CommandPalette plugin is not enabled.");
			return;
		}

		CommandPaletteConfig config = new(ctx);

		if (commandPalette.MaxHeightPercent.AsOptional() is { } maxHeightPercent)
		{
			config.MaxHeightPercent = (int)maxHeightPercent;
		}

		if (commandPalette.MaxWidthPixels.AsOptional() is { } maxWidthPixels)
		{
			config.MaxWidthPixels = (int)maxWidthPixels;
		}

		if (commandPalette.YPositionPercent.AsOptional() is { } yPositionPercent)
		{
			config.YPositionPercent = (int)yPositionPercent;
		}

		if (commandPalette.Backdrop.AsOptional() is { } backdrop)
		{
			config.Backdrop = YamlLoader.ParseWindowBackdropConfig(backdrop);
		}

		ctx.PluginManager.AddPlugin(new CommandPalettePlugin(ctx, config));
	}
}
