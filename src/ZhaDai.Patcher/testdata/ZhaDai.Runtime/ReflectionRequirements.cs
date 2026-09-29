using System.Collections.Generic;

namespace ZhaDai.Runtime
{
    /// <summary>
    /// Test-only mirror of the real runtime's self-describing requirement list. It exists so the
    /// patcher's IL decoder (which reads this list without loading the assembly) is exercised on
    /// the same shape the real `ZhaDai.Runtime.ReflectionRequirements` uses: literal factory
    /// calls in a type initializer, a nested type name with `+`, and a params string[] parameter
    /// list on the method entries.
    /// </summary>
    public static class ReflectionRequirements
    {
        public sealed class Requirement
        {
            public string Type;
            public string Member;
            public string Kind;
            public bool Required;
            public string[] Parameters;
        }

        public static IReadOnlyList<Requirement> All
        {
            get { return Requirements; }
        }

        private static readonly Requirement[] Requirements =
        {
            Field("Terraria.Main", "player", true),
            Field("Terraria.Main", "myPlayer", true),
            Property("Terraria.Main", "GameUpdateCount", false),
            Field("Terraria.Player", "selectedItemState", true),
            Field("Terraria.Player+SelectedItemState", "selected", true),
            Field("Terraria.Entity", "whoAmI", false),
            Method("Terraria.GameInput.TriggersSet", "CopyInto", true, "Terraria.Player")
        };

        private static Requirement Field(string type, string member, bool required)
        {
            return new Requirement { Type = type, Member = member, Kind = "Field", Required = required, Parameters = new string[0] };
        }

        private static Requirement Property(string type, string member, bool required)
        {
            return new Requirement { Type = type, Member = member, Kind = "Property", Required = required, Parameters = new string[0] };
        }

        private static Requirement Method(string type, string member, bool required, params string[] parameters)
        {
            return new Requirement { Type = type, Member = member, Kind = "Method", Required = required, Parameters = parameters };
        }
    }
}
