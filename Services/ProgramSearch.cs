using AeroStart.Models;
using System.Text.RegularExpressions;

namespace AeroStart.Services;
public static partial class ProgramSearch
{
    public static List<StartItem> Filter(IEnumerable<StartItem> items, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();

        return items.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                || item.Category.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(item => item.Name.Equals(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(item => AuxiliaryName().IsMatch(item.Name) ? 1 : 0)
            .ThenBy(item => item.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0
                         : item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 1 : 2)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex(@"(?:^|[^\p{L}\p{N}])(?:uninstall\w*|deinstall\w*|unins\d+|setup|installer|update(?:r)?|readme|liesmich|hilfe|help|handbuch|manual|dokumentation|documentation|support|repair|reparieren|entfernen|lizenz|license|release\s+notes)(?:$|[^\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuxiliaryName();
}
