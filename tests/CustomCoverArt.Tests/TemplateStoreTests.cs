using System;
using System.Linq;
using System.Threading.Tasks;
using CustomCoverArt.Configuration;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Xunit;

namespace CustomCoverArt.Tests;

public class TemplateStoreTests
{
    [Fact]
    public void Snapshot_IsACopy()
    {
        var cfg = new PluginConfiguration();
        cfg.Templates.Add(new SavedTemplate { Name = "A" });

        var snapshot = TemplateStore.Snapshot(cfg);
        snapshot.Clear();

        Assert.Single(cfg.Templates);
    }

    /// <summary>Writers swap in a new list, so a reader holding the old one never sees it change.</summary>
    [Fact]
    public void Upsert_ReplacesTheListInsteadOfMutatingIt()
    {
        var cfg = new PluginConfiguration();
        cfg.Templates.Add(new SavedTemplate { Name = "A" });
        var before = cfg.Templates;
        var persisted = 0;

        TemplateStore.Upsert(cfg, new SavedTemplate { Name = "a" }, () => persisted++);

        Assert.NotSame(before, cfg.Templates);
        Assert.Single(before);
        Assert.Single(cfg.Templates);           // upsert is case-insensitive
        Assert.Equal("a", cfg.Templates[0].Name);
        Assert.Equal(1, persisted);
    }

    [Fact]
    public void Remove_And_Find_AreCaseInsensitive()
    {
        var cfg = new PluginConfiguration();
        TemplateStore.Upsert(cfg, new SavedTemplate { Name = "Neon" }, () => { });

        Assert.NotNull(TemplateStore.Find(cfg, "NEON"));
        TemplateStore.Remove(cfg, "neon", () => { });
        Assert.Null(TemplateStore.Find(cfg, "Neon"));
        Assert.Empty(cfg.Templates);
    }

    [Fact]
    public void ConcurrentWritesAndReads_NeverThrow()
    {
        var cfg = new PluginConfiguration();
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                TemplateStore.Upsert(cfg, new SavedTemplate { Name = $"t{i % 20}" }, () => { });
                if (i % 3 == 0)
                {
                    TemplateStore.Remove(cfg, $"t{(i + w) % 20}", () => { });
                }
            }
        }));
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                foreach (var t in TemplateStore.Snapshot(cfg)) { _ = t.Name; }
                _ = TemplateStore.Find(cfg, "t1");
            }
        });

        Task.WaitAll(writers.Append(reader).ToArray());

        var final = TemplateStore.Snapshot(cfg);
        Assert.True(final.Count <= 20);
        Assert.Equal(final.Count, final.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
