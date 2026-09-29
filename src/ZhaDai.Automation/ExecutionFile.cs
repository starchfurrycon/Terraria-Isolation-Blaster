using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ZhaDai.Automation
{
    /// <summary>One Dynamite charge as read from the plan's execution file.</summary>
    public sealed class ChargeOrder
    {
        public int Order { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public int Section { get; set; }

        public int StandX { get; set; }

        public int StandY { get; set; }

        public bool RetreatAvailable { get; set; }

        public bool HasLava { get; set; }

        public bool HasWater { get; set; }

        public bool HasTrap { get; set; }

        public bool HasGravestone { get; set; }

        public bool HasExplosives { get; set; }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "#{0} ({1},{2}) 站位 ({3},{4})",
                Order,
                X,
                Y,
                StandX,
                StandY);
        }
    }

    /// <summary>One tile the pickaxe has to remove, as read from the plan's execution file.</summary>
    /// <summary>A tile to fill with an inert block once the blasting is done.</summary>
    public sealed class PlugOrder
    {
        public int X { get; set; }

        public int Y { get; set; }

        /// <summary>Item to place, as an inventory item id (wood is 9).</summary>
        public int ItemId { get; set; }
    }

    public sealed class DigOrder
    {
        public int X { get; set; }

        public int Y { get; set; }

        public int Type { get; set; }

        /// <summary>Swings a pickaxe of the planned power needs. Zero means blocked, never executed.</summary>
        public int Hits { get; set; }

        /// <summary>Why it is a dig rather than a blast: blastimmune, collateral, uncovered.</summary>
        public string Reason { get; set; } = string.Empty;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "挖 ({0},{1}) 物块 {2} x{3} [{4}]", X, Y, Type, Hits, Reason);
        }
    }

    /// <summary>The work order the planner wrote, parsed back into something the runtime can run.</summary>
    public sealed class ExecutionPlan
    {
        public int Width { get; set; }

        public int Height { get; set; }

        public int SpawnX { get; set; }

        public int SpawnY { get; set; }

        public int BlastRadius { get; set; } = 7;

        public int FuseTicks { get; set; } = 300;

        public int RetreatTiles { get; set; } = 10;

        /// <summary>Pick power the planner assumed for the dig list.</summary>
        public int PickPower { get; set; }

        public int SectionCount { get; set; }

        public List<ChargeOrder> Charges { get; } = new List<ChargeOrder>();

        /// <summary>Tiles the plan wants dug instead of blasted; empty when dynamite covers everything.</summary>
        public List<DigOrder> Digs { get; } = new List<DigOrder>();

        /// <summary>Tiles to fill with an inert block after the last blast, to stop vines at their source.</summary>
        public List<PlugOrder> Plugs { get; } = new List<PlugOrder>();

        /// <summary>Item id the plugs use; 0 when the plan has none.</summary>
        public int PlugItemId { get; set; }

        /// <summary>
        /// Parses the line oriented file written by <c>PlanWriter.WriteExecutionFile</c>. Only the
        /// keys this runtime needs are read, so a newer planner may add lines without breaking it.
        /// </summary>
        public static ExecutionPlan Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            ExecutionPlan plan = new ExecutionPlan();
            string[] lines = text.Split('\n');
            if (lines.Length == 0 || !lines[0].TrimEnd('\r').StartsWith("#ZHAODAI", StringComparison.Ordinal))
            {
                throw new InvalidDataException("不是炸带施工文件：缺少 #ZHAODAI 头。");
            }

            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("pick=", StringComparison.Ordinal))
                {
                    plan.PickPower = ParseInt(line.Substring("pick=".Length));
                    continue;
                }

                if (line.StartsWith("plug-item=", StringComparison.Ordinal))
                {
                    plan.PlugItemId = ParseInt(line.Substring("plug-item=".Length));
                    continue;
                }

                if (line.Length == 0 || line[0] != '#')
                {
                    continue;
                }

                if (line.StartsWith("#SECTION", StringComparison.Ordinal))
                {
                    plan.SectionCount++;
                    continue;
                }

                if (line.StartsWith("#DIG ", StringComparison.Ordinal))
                {
                    plan.Digs.Add(ParseDig(line));
                    continue;
                }

                if (line.StartsWith("#PLUG ", StringComparison.Ordinal))
                {
                    plan.Plugs.Add(ParsePlug(line));
                    continue;
                }

                if (!line.StartsWith("#CHARGE ", StringComparison.Ordinal))
                {
                    continue;
                }

                plan.Charges.Add(ParseCharge(line));
            }

            return plan;
        }

        private static PlugOrder ParsePlug(string line)
    {
        // #PLUG x y item=9
        string[] parts = line.Substring("#PLUG ".Length).Split(' ');
        if (parts.Length < 2)
        {
            throw new InvalidDataException("施工文件里的 #PLUG 行格式不对：" + line);
        }

        PlugOrder plug = new PlugOrder
        {
            X = ParseInt(parts[0]),
            Y = ParseInt(parts[1]),
        };

        foreach (string part in parts)
        {
            if (part.StartsWith("item=", StringComparison.Ordinal))
            {
                plug.ItemId = ParseInt(part.Substring("item=".Length));
            }
        }

        return plug;
    }

    private static DigOrder ParseDig(string line)
        {
            DigOrder dig = new DigOrder();
            string[] parts = line.Substring("#DIG ".Length).Split(' ');
            if (parts.Length < 2)
            {
                throw new InvalidDataException("施工文件里的 #DIG 行格式不对：" + line);
            }

            dig.X = ParseInt(parts[0]);
            dig.Y = ParseInt(parts[1]);
            for (int i = 2; i < parts.Length; i++)
            {
                string[] pair = parts[i].Split('=');
                if (pair.Length != 2)
                {
                    continue;
                }

                switch (pair[0])
                {
                    case "type":
                        dig.Type = ParseInt(pair[1]);
                        break;
                    case "hits":
                        dig.Hits = ParseInt(pair[1]);
                        break;
                    case "why":
                        dig.Reason = pair[1];
                        break;
                }
            }

            return dig;
        }

        private static int ParseInt(string text)
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }

        public static ExecutionPlan Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        private static ChargeOrder ParseCharge(string line)
        {
            ChargeOrder charge = new ChargeOrder();
            string[] parts = line.Substring("#CHARGE ".Length).Split(' ');
            if (parts.Length < 3)
            {
                throw new InvalidDataException("施工文件里的 #CHARGE 行格式不对：" + line);
            }

            charge.Order = int.Parse(parts[0], CultureInfo.InvariantCulture);
            charge.X = int.Parse(parts[1], CultureInfo.InvariantCulture);
            charge.Y = int.Parse(parts[2], CultureInfo.InvariantCulture);

            for (int i = 3; i < parts.Length; i++)
            {
                string[] pair = parts[i].Split('=');
                if (pair.Length != 2)
                {
                    continue;
                }

                switch (pair[0])
                {
                    case "sec":
                        charge.Section = int.Parse(pair[1], CultureInfo.InvariantCulture);
                        break;
                    case "stand":
                        string[] coordinates = pair[1].Split(',');
                        if (coordinates.Length == 2)
                        {
                            charge.StandX = int.Parse(coordinates[0], CultureInfo.InvariantCulture);
                            charge.StandY = int.Parse(coordinates[1], CultureInfo.InvariantCulture);
                        }

                        break;
                    case "retreat":
                        charge.RetreatAvailable = pair[1] == "1";
                        break;
                    case "haz":
                        int hazards = int.Parse(pair[1], CultureInfo.InvariantCulture);
                        charge.HasLava = (hazards & 1) != 0;
                        charge.HasWater = (hazards & 2) != 0;
                        charge.HasTrap = (hazards & 4) != 0;
                        charge.HasGravestone = (hazards & 8) != 0;
                        charge.HasExplosives = (hazards & 16) != 0;
                        break;
                }
            }

            return charge;
        }
    }
}
