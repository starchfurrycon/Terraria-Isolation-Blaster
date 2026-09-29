using System.Globalization;
using System.Text;
using System.Text.Json;
using ZhaDai.Core.Analysis;
using ZhaDai.Core.World;

namespace ZhaDai.Core.Planning;

/// <summary>Serialises a plan to the machine readable report and the in-game execution file.</summary>
public static class PlanWriter
{
    public const string ExecutionFormatVersion = "1";

    public static void WriteJson(BlastPlan plan, string path)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using FileStream stream = File.Create(path);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        WriteJson(plan, writer);
        writer.Flush();
    }

    public static string ToJson(BlastPlan plan)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            WriteJson(plan, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJson(BlastPlan plan, Utf8JsonWriter writer)
    {
        WorldMetadata world = plan.World;
        BlastPlanOptions opts = plan.Options;
        BlastPlanSummary summary = plan.Summary;

        writer.WriteStartObject();
        writer.WriteString("tool", "ZhaDai");
        writer.WriteNumber("planVersion", 1);

        writer.WriteStartObject("world");
        writer.WriteString("title", world.Title);
        writer.WriteString("seed", world.Seed);
        writer.WriteNumber("formatVersion", world.Version);
        writer.WriteNumber("width", world.Width);
        writer.WriteNumber("height", world.Height);
        writer.WriteNumber("spawnX", world.SpawnX);
        writer.WriteNumber("spawnY", world.SpawnY);
        writer.WriteNumber("worldSurface", world.WorldSurface);
        writer.WriteNumber("rockLayer", world.RockLayer);
        writer.WriteBoolean("hardMode", world.HardMode);
        writer.WriteBoolean("isCrimson", world.IsCrimson);
        writer.WriteBoolean("getGoodWorld", world.GetGoodWorld);
        writer.WriteBoolean("remixWorld", world.RemixWorld);
        writer.WriteBoolean("skyblockWorld", world.SkyblockWorld);
        writer.WriteEndObject();

        writer.WriteStartObject("options");
        writer.WriteNumber("clearance", opts.Clearance);
        writer.WriteNumber("mergeLinkDistance", opts.MergeLinkDistance);
        writer.WriteNumber("blastRadius", opts.BlastRadius);
        writer.WriteNumber("dynamiteFuseTicks", opts.DynamiteFuseTicks);
        writer.WriteNumber("retreatMarginTiles", opts.RetreatMarginTiles);
        writer.WriteNumber("vineReach", opts.VineReach);
        writer.WriteEndObject();

        writer.WriteStartObject("summary");
        writer.WriteNumber("infectionNodes", summary.InfectionNodes);
        writer.WriteNumber("evilSeeds", summary.EvilSeeds);
        writer.WriteNumber("hallowSeeds", summary.HallowSeeds);
        writer.WriteNumber("sections", summary.Sections);
        writer.WriteNumber("mixedSections", summary.MixedSections);
        writer.WriteNumber("fenceTiles", summary.FenceTiles);
        writer.WriteNumber("charges", summary.Charges);
        writer.WriteNumber("digTiles", summary.DigTiles);
        writer.WriteNumber("blockedTiles", summary.BlockedTiles);
        writer.WriteNumber("requiredPickPower", summary.RequiredPickPower);
        writer.WriteNumber("chargesWithCollateral", summary.ChargesWithCollateral);
        writer.WriteNumber("protectedTilesInBlast", summary.ProtectedTilesInBlast);
        writer.WriteNumber("playerBlocksInBlast", summary.PlayerBlocksInBlast);
        writer.WriteNumber("builtWallTilesInBlast", summary.BuiltWallTilesInBlast);
        writer.WriteNumber("builtWallTilesInWorld", summary.BuiltWallTilesWorld);
        writer.WriteNumber("plugTiles", summary.PlugTiles);
        writer.WriteNumber("requiredPlugBlocks", summary.RequiredPlugBlocks);
        writer.WriteNumber("plugItemId", summary.PlugItemId);
        writer.WriteNumber("vineCurtainTilesSaved", summary.VineCurtainTilesSaved);
        writer.WriteNumber("estimatedDigSeconds", summary.EstimatedDigSeconds);
        writer.WriteNumber("dynamiteStacks", summary.DynamiteStacks);
        writer.WriteNumber("seedsDestroyedByBlast", summary.SeedsDestroyedByBlast);
        writer.WriteNumber("enclosedSeedTiles", summary.EnclosedSeedTiles);
        writer.WriteNumber("blastImmuneNodesInFence", summary.BlastImmuneNodesInFence);
        writer.WriteNumber("lavaTilesInFence", summary.LavaTilesInFence);
        writer.WriteNumber("waterTilesInFence", summary.WaterTilesInFence);
        writer.WriteNumber("trapTilesInFence", summary.TrapTilesInFence);
        writer.WriteNumber("gravestoneTilesInFence", summary.GravestoneTilesInFence);
        writer.WriteNumber("explosivesTilesInFence", summary.ExplosivesTilesInFence);
        writer.WriteNumber("blastDestroyedTiles", summary.BlastDestroyedTiles);
        writer.WriteNumber("blastImmuneTilesInBlast", summary.BlastImmuneTilesInBlast);
        writer.WriteBoolean("allSectionsSealed", summary.AllSectionsSealed);
        writer.WriteNumber("vineAnchorTiles", summary.VineAnchorTiles);
        writer.WriteNumber("estimatedPlayerSeconds", summary.EstimatedPlayerSeconds);
        writer.WriteEndObject();

        writer.WriteStartArray("sections");
        foreach (FenceSection section in plan.Sections)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", section.Sequence);
            writer.WriteBoolean("hasEvil", section.HasEvil);
            writer.WriteBoolean("hasHallow", section.HasHallow);
            writer.WriteNumber("seedTiles", section.SeedTiles);
            writer.WriteNumber("fenceTiles", section.FenceTiles);
            writer.WriteNumber("charges", section.Charges);
            writer.WriteNumber("minX", section.Bounds.MinX);
            writer.WriteNumber("minY", section.Bounds.MinY);
            writer.WriteNumber("maxX", section.Bounds.MaxX);
            writer.WriteNumber("maxY", section.Bounds.MaxY);
            writer.WriteNumber("enclosedWidthWorldPercent", section.EnclosedWidthWorldPercent);
            writer.WriteNumber("enclosedInfectablePercent", section.EnclosedInfectablePercent);
            writer.WriteBoolean("withinAnalyzerLimits", section.WithinAnalyzerLimits);
            writer.WriteBoolean("sealedByFloodVerification", section.SealedByFloodVerification);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("charges");
        foreach (BlastCharge charge in plan.Charges)
        {
            writer.WriteStartObject();
            writer.WriteNumber("order", charge.Order);
            writer.WriteNumber("x", charge.X);
            writer.WriteNumber("y", charge.Y);
            writer.WriteNumber("section", charge.SectionSequence);
            writer.WriteNumber("coverTiles", charge.CoverTiles);
            writer.WriteNumber("seedsDestroyed", charge.SeedsDestroyed);
            writer.WriteNumber("standX", charge.StandX);
            writer.WriteNumber("standY", charge.StandY);
            writer.WriteBoolean("retreatAvailable", charge.RetreatAvailable);
            writer.WriteStartArray("hazards");
            if (charge.LavaInBlast) writer.WriteStringValue("lava");
            if (charge.WaterInBlast) writer.WriteStringValue("water");
            if (charge.TrapInBlast) writer.WriteStringValue("trap");
            if (charge.GravestoneInBlast) writer.WriteStringValue("gravestone");
            if (charge.ExplosivesInBlast) writer.WriteStringValue("explosives");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("notes");
        foreach (string note in plan.Notes)
        {
            writer.WriteStringValue(note);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes the compact line oriented execution file read by the in-game runtime. It is
    /// deliberately not JSON: the runtime is a .NET Framework assembly loaded inside Terraria and
    /// must not take a serializer dependency to read its work order.
    /// </summary>
    public static void WriteExecutionFile(BlastPlan plan, string path, string worldPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        BlastPlanOptions opts = plan.Options;
        WorldMetadata world = plan.World;
        StringBuilder text = new();

        text.Append("#ZHAODAI ").Append(ExecutionFormatVersion).Append('\n');
        text.Append("world=").Append(Path.GetFileName(worldPath)).Append('\n');
        text.Append("size=").Append(world.Width).Append(' ').Append(world.Height).Append('\n');
        text.Append("spawn=").Append(world.SpawnX).Append(' ').Append(world.SpawnY).Append('\n');
        text.Append("surface=").Append(world.WorldSurfaceY).Append('\n');
        text.Append("rock=").Append(world.RockLayerY).Append('\n');
        text.Append("hardmode=").Append(world.HardMode ? 1 : 0).Append('\n');
        text.Append("radius=").Append(opts.BlastRadius).Append('\n');
        text.Append("fuse=").Append(opts.DynamiteFuseTicks).Append('\n');
        text.Append("vine=").Append(opts.VineReach).Append('\n');
        text.Append("retreat=").Append(opts.BlastRadius + opts.RetreatMarginTiles).Append('\n');
        text.Append("protection=").Append(opts.Protection.ToString().ToLowerInvariant()).Append('\n');
        text.Append("pick=").Append(opts.PickPower).Append('\n');
        text.Append("sections=").Append(plan.Sections.Count).Append('\n');
        text.Append("charges=").Append(plan.Charges.Count).Append('\n');
        text.Append("digs=").Append(plan.DigOrders.Count(dig => dig.Hits > 0)).Append('\n');
        text.Append("blocked=").Append(plan.DigOrders.Count(dig => dig.Hits <= 0)).Append('\n');
        text.Append("plugs=").Append(plan.PlugOrders.Count).Append('\n');
        text.Append("plug-item=").Append(plan.PlugOrders.Count == 0 ? 0 : plan.PlugOrders[0].ItemId).Append('\n');

        foreach (FenceSection section in plan.Sections)
        {
            text.Append("#SECTION ").Append(section.Sequence)
                .Append(" evil=").Append(section.HasEvil ? 1 : 0)
                .Append(" hallow=").Append(section.HasHallow ? 1 : 0)
                .Append(" seeds=").Append(section.SeedTiles)
                .Append(" fence=").Append(section.FenceTiles)
                .Append(" box=").Append(section.Bounds.MinX).Append(',').Append(section.Bounds.MinY)
                .Append(',').Append(section.Bounds.MaxX).Append(',').Append(section.Bounds.MaxY)
                .Append('\n');
        }

        foreach (BlastCharge charge in plan.Charges)
        {
            int hazards = 0;
            if (charge.LavaInBlast) hazards |= 1;
            if (charge.WaterInBlast) hazards |= 2;
            if (charge.TrapInBlast) hazards |= 4;
            if (charge.GravestoneInBlast) hazards |= 8;
            if (charge.ExplosivesInBlast) hazards |= 16;
            if (charge.ProtectedTilesInBlast > 0) hazards |= 32;
            if (charge.PlayerBlocksInBlast > 0) hazards |= 64;

            text.Append("#CHARGE ").Append(charge.Order)
                .Append(' ').Append(charge.X)
                .Append(' ').Append(charge.Y)
                .Append(" sec=").Append(charge.SectionSequence)
                .Append(" stand=").Append(charge.StandX).Append(',').Append(charge.StandY)
                .Append(" retreat=").Append(charge.RetreatAvailable ? 1 : 0)
                .Append(" haz=").Append(hazards)
                .Append('\n');
        }

        // Dig orders come last and carry their own hit count so the executor can pace the swings and
        // knows which tiles a 100% pickaxe will never break.
        foreach (DigOrder dig in plan.DigOrders)
        {
            if (dig.Hits <= 0)
            {
                continue;
            }

            text.Append("#DIG ").Append(dig.X).Append(' ').Append(dig.Y)
                .Append(" type=").Append(dig.Type)
                .Append(" hits=").Append(dig.Hits)
                .Append(" why=").Append(dig.Reason.ToString().ToLowerInvariant())
                .Append('\n');
        }

        // Plugs come last on purpose: a block placed inside a blast radius would be blown up again, so the
        // executor fills these after every charge has gone off. By then the tile has to be air, and if it
        // is still rock the plug is skipped -- rock already stops a vine.
        foreach (PlugOrder plug in plan.PlugOrders)
        {
            text.Append("#PLUG ").Append(plug.X).Append(' ').Append(plug.Y)
                .Append(" item=").Append(plug.ItemId)
                .Append('\n');
        }

        text.Append("#END\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Plain text report for the console and for a human reading the JSON's company.</summary>
    public static string ToTextReport(BlastPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        BlastPlanSummary s = plan.Summary;
        StringBuilder text = new();
        CultureInfo culture = CultureInfo.InvariantCulture;

        text.AppendLine(string.Format(culture, "世界：{0}（格式 {1}，{2}x{3}）", plan.World.Title, plan.World.Version, plan.World.Width, plan.World.Height));
        text.AppendLine(string.Format(culture, "难度：{0}", plan.World.HardMode ? "困难模式" : "肉前"));
        text.AppendLine(string.Format(culture, "感染源：邪恶 {0} 格，神圣 {1} 格；可感染物块 {2} 格", s.EvilSeeds, s.HallowSeeds, s.InfectionNodes));
        text.AppendLine(string.Format(culture, "隔离段：{0} 段（其中混合段 {1}）", s.Sections, s.MixedSections));
        if (s.PlugTiles > 0)
        {
            text.AppendLine(string.Format(
                culture,
                "封堵：{0} 格（物品 {1}，需备 {2} 个；顶掉了 {3} 格藤蔓竖井）",
                s.PlugTiles,
                s.PlugItemId,
                s.RequiredPlugBlocks,
                s.VineCurtainTilesSaved));
        }

        text.AppendLine(string.Format(culture, "封带物块：{0} 格；雷管：{1} 发（约 {2} 组）", s.FenceTiles, s.Charges, s.DynamiteStacks));
        text.AppendLine(string.Format(
            culture,
            "镐子分担：{0} 格（约 {1} 分钟，需要镐力 {2}%）；两样都处理不掉：{3} 格",
            s.DigTiles,
            Math.Round(s.EstimatedDigSeconds / 60d, 1),
            s.RequiredPickPower,
            s.BlockedTiles));
        text.AppendLine(string.Format(culture, "封住的感染物块：{0} 格（占全部可感染物块 {1:0.###}%）", s.EnclosedSeedTiles, s.InfectionNodes == 0 ? 0d : 100d * s.EnclosedSeedTiles / s.InfectionNodes));
        text.AppendLine(string.Format(culture, "爆破总摧毁：{0} 格（其中炸不掉而留存 {1} 格）", s.BlastDestroyedTiles, s.BlastImmuneTilesInBlast));
        text.AppendLine(string.Format(culture, "封带泛洪复核：{0}", s.AllSectionsSealed ? "全部封住" : "有段未封住"));
        text.AppendLine(string.Format(culture, "预计施工：约 {0} 分钟", Math.Round(s.EstimatedPlayerSeconds / 60d, 1)));
        text.AppendLine(string.Format(culture, "危险物块：岩浆 {0}、水 {1}、陷阱 {2}、墓碑 {3}、爆炸物 {4}", s.LavaTilesInFence, s.WaterTilesInFence, s.TrapTilesInFence, s.GravestoneTilesInFence, s.ExplosivesTilesInFence));

        if (s.VineAnchorTiles > 0)
        {
            text.AppendLine(string.Format(culture, "带草的前沿物块（会长藤蔓）：{0} 格", s.VineAnchorTiles));
        }

        if (s.SeedsDestroyedByBlast > 0)
        {
            text.AppendLine(string.Format(culture, "顺带摧毁的感染源：{0} 格（爆破半径本身就盖住了它们）", s.SeedsDestroyedByBlast));
        }

        foreach (string note in plan.Notes)
        {
            text.AppendLine("· " + note);
        }

        return text.ToString();
    }
}
