using CustomCoverArt.Configuration;
using CustomCoverArt.Models;

namespace CustomCoverArt.Services;

/// <summary>
/// Thread-safe access to the saved templates. Writers build a new list under a lock and
/// swap it in (copy-on-write), so a list a reader already holds is never mutated: a GET
/// that is serialising, or a batch apply that is enumerating, can no longer throw
/// "collection was modified" when a template is saved at the same time.
/// </summary>
public static class TemplateStore
{
    private static readonly object Gate = new();

    public static List<SavedTemplate> Snapshot(PluginConfiguration cfg)
    {
        lock (Gate)
        {
            return new List<SavedTemplate>(cfg.Templates ?? new List<SavedTemplate>());
        }
    }

    public static SavedTemplate? Find(PluginConfiguration cfg, string name)
    {
        lock (Gate)
        {
            return (cfg.Templates ?? new List<SavedTemplate>())
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Replaces any template with the same name (case-insensitive), then persists.</summary>
    public static void Upsert(PluginConfiguration cfg, SavedTemplate template, Action persist)
    {
        lock (Gate)
        {
            var next = (cfg.Templates ?? new List<SavedTemplate>())
                .Where(t => !string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            next.Add(template);
            cfg.Templates = next;
            persist();
        }
    }

    public static void Remove(PluginConfiguration cfg, string name, Action persist)
    {
        lock (Gate)
        {
            cfg.Templates = (cfg.Templates ?? new List<SavedTemplate>())
                .Where(t => !string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            persist();
        }
    }
}
