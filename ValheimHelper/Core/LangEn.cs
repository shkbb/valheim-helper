using System.Collections.Generic;

namespace ValheimHelper
{
    // English texts of the mod: Ukrainian text of the code → English.
    internal static partial class LangEn
    {
        internal static Dictionary<string, string> Table()
        {
            Dictionary<string, string> table = new Dictionary<string, string>();
            Add(table);
            return table;
        }

        static partial void Add(Dictionary<string, string> table);
    }
}
