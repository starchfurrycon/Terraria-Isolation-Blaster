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

        public int SectionCount { get; set; }

        public List<ChargeOrder> Charges { get; } = new List<ChargeOrder>();

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
                if (line.Length == 0 || line[0] != '#')
                {
                    continue;
                }

                if (line.StartsWith("#SECTION", StringComparison.Ordinal))
                {
                    plan.SectionCount++;
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
